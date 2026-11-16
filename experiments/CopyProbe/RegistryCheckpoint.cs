using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

internal enum RegistryCheckpointStep { DirectoryCreated, PlanPartial, Planned, SnapshotPartial, Staged, PrimarySaved, PrimaryPublished, ReplicaSaved, ReplicaPublished, Completed }

internal static partial class GenerationRegistry
{
    private const string CheckpointDirectory = ".foldertwin-registry-checkpoints";
    private sealed record CheckpointPlan(int Version, Guid Marker, byte[] Identity, byte[] Before, byte[] After);

    // The caller holds the store's exclusive lock. Reserving the maximum encoded
    // event size keeps the complete mirror/retention operation within the cap.
    internal static void Reserve(SafeFileHandle root, DestinationBinding binding, int events,
        Action<RegistryCheckpointStep>? checkpoint = null, int maximumBytes = MaximumBytes)
    {
        if (events < 1 || maximumBytes is < MaximumLineBytes or > MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(events));
        CompleteCheckpoint(root, binding, checkpoint);
        byte[] first = ReadAt(root, PrimaryName), second = ReadAt(root, ReplicaName);
        long reserved = checked((long)events * MaximumLineBytes);
        // A split append may only be repaired by Append's exact event proof.
        if (!first.AsSpan().SequenceEqual(second)) return;
        if (first.Length + reserved <= maximumBytes) return;
        var state = Parse(first, binding);
        byte[] compact = Compact(state, binding);
        if (compact.Length + reserved > maximumBytes)
            throw new InvalidDataException("Las generaciones conservadas superan la capacidad del registro. Revisa la retención o crea otro destino conservando este respaldo.");
        using var checkpoints = WindowsProbeNative.OpenOrCreatePlainDirectory(root, CheckpointDirectory);
        long next = CheckpointNames(checkpoints).Select(ParseNumber).DefaultIfEmpty(-1).Max() + 1;
        string id = next.ToString("D16", CultureInfo.InvariantCulture);
        using var directory = WindowsProbeNative.OpenOrCreatePlainDirectory(checkpoints, id);
        checkpoint?.Invoke(RegistryCheckpointStep.DirectoryCreated);
        var plan = new CheckpointPlan(1, binding.Marker, binding.Identity, SHA256.HashData(first), SHA256.HashData(compact));
        WriteCheckpointFile(directory, "plan", EncodePlan(plan), () => checkpoint?.Invoke(RegistryCheckpointStep.PlanPartial));
        checkpoint?.Invoke(RegistryCheckpointStep.Planned);
        CompleteCheckpoint(root, binding, checkpoint);
    }

    private static byte[] Compact(RegistryState state, DestinationBinding binding)
    {
        using var bytes = new MemoryStream();
        long sequence = 0;
        byte[] previous = new byte[32];
        Add("initialize", "", new byte[32]);
        foreach (string id in state.RegistrationOrder)
        {
            Add("register", id, state.Generations[id]);
            if (state.Expired.Contains(id)) Add("expire", id, state.Generations[id]);
        }
        foreach (var (id, hash) in state.Abandoned.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Add("register", id, hash);
            Add("abandon", id, hash);
        }
        if (state.MirrorGeneration is { } mirror) Add("mirror", mirror, state.Generations[mirror]);
        if (state.PendingMirrorGeneration is { } pending) Add("mirror-begin", pending, state.Generations[pending]);
        foreach (var (id, hash) in state.PurgeProofs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            Add("purge-proof", id, hash);
        return bytes.ToArray();

        void Add(string kind, string id, byte[] hash)
        {
            byte[] line = Encode(new(EventVersion(kind), sequence++, kind, id, hash, binding.Marker, binding.Identity, previous));
            bytes.Write(line);
            previous = JsonSerializer.Deserialize<Envelope>(line.AsSpan(0, line.Length - 1), Json)!.Checksum;
        }
    }

    private static byte[]? ReadCheckpoint(SafeFileHandle root, DestinationBinding binding)
    {
        using var checkpoints = OpenCheckpointDirectory(root);
        if (checkpoints is null) return null;
        string? latest = CheckpointNames(checkpoints).LastOrDefault();
        if (latest is null) return null;
        using var directory = WindowsProbeNative.OpenPlainDirectory(checkpoints, latest);
        if (!WindowsProbeNative.PlainFileExists(directory, "plan"))
        {
            RequireUnpublishedCheckpoint(root, directory, binding);
            return null;
        }
        var plan = ReadPlan(directory, binding);
        if (Complete(directory, plan)) return null;
        return CheckpointContent(root, directory, plan, binding);
    }

    private static void CompleteCheckpoint(SafeFileHandle root, DestinationBinding binding, Action<RegistryCheckpointStep>? checkpoint)
    {
        using var checkpoints = OpenCheckpointDirectory(root);
        if (checkpoints is null) return;
        string? latest = CheckpointNames(checkpoints).LastOrDefault();
        if (latest is null) return;
        using var directory = WindowsProbeNative.OpenPlainDirectory(checkpoints, latest);
        if (!WindowsProbeNative.PlainFileExists(directory, "plan"))
        {
            byte[] original = RequireUnpublishedCheckpoint(root, directory, binding);
            var pending = new CheckpointPlan(1, binding.Marker, binding.Identity, SHA256.HashData(original),
                SHA256.HashData(Compact(Parse(original, binding), binding)));
            WriteCheckpointFile(directory, "plan", EncodePlan(pending));
        }
        var plan = ReadPlan(directory, binding);
        if (Complete(directory, plan)) { CleanOld(); return; }
        byte[] compact = CheckpointContent(root, directory, plan, binding);
        Prepare("primary", PrimaryName);
        Prepare("replica", ReplicaName);
        checkpoint?.Invoke(RegistryCheckpointStep.Staged);
        Publish("primary", PrimaryName, RegistryCheckpointStep.PrimarySaved, RegistryCheckpointStep.PrimaryPublished);
        Publish("replica", ReplicaName, RegistryCheckpointStep.ReplicaSaved, RegistryCheckpointStep.ReplicaPublished);
        WriteCheckpointFile(directory, "complete", SHA256.HashData(EncodePlan(plan)));
        checkpoint?.Invoke(RegistryCheckpointStep.Completed);
        CleanOld();

        void Prepare(string name, string rootName)
        {
            if (Matches(root, rootName, plan.After)) return;
            WriteCheckpointFile(directory, name, compact, () => checkpoint?.Invoke(RegistryCheckpointStep.SnapshotPartial));
        }
        void Publish(string name, string rootName, RegistryCheckpointStep saved, RegistryCheckpointStep published)
        {
            if (Matches(root, rootName, plan.After)) return;
            if (WindowsProbeNative.PlainFileExists(root, rootName))
            {
                if (!Matches(root, rootName, plan.Before)) throw new InvalidDataException("El registro cambió durante su compactación.");
                using var old = WindowsProbeNative.OpenPlainFile(root, rootName, FileAccess.Read, FileShare.None, rename: true);
                WindowsProbeNative.RenameNew(old.SafeFileHandle, directory, "old-" + name);
            }
            else if (!Matches(directory, "old-" + name, plan.Before))
                throw new InvalidDataException("Falta el registro original de una compactación pendiente.");
            checkpoint?.Invoke(saved);
            using var next = WindowsProbeNative.OpenPlainFile(directory, name, FileAccess.Read, FileShare.None, rename: true);
            if (!SHA256.HashData(next).AsSpan().SequenceEqual(plan.After)) throw new InvalidDataException("El registro preparado está dañado.");
            WindowsProbeNative.RenameNew(next.SafeFileHandle, root, rootName);
            checkpoint?.Invoke(published);
        }
        void CleanOld()
        {
            foreach (string name in new[] { "old-primary", "old-replica" })
            {
                if (!WindowsProbeNative.PlainFileExists(directory, name)) continue;
                using var old = WindowsProbeNative.OpenPlainFile(directory, name, FileAccess.Read, FileShare.None, rename: true);
                if (!SHA256.HashData(old).AsSpan().SequenceEqual(plan.Before)) throw new InvalidDataException("El registro anterior de compactación está dañado.");
                WindowsProbeNative.DeletePlainFile(old.SafeFileHandle);
            }
        }
    }

    private static byte[] RequireUnpublishedCheckpoint(SafeFileHandle root, SafeFileHandle directory, DestinationBinding binding)
    {
        if (WindowsProbeNative.ReadDirectory(directory).Any(entry => entry.Name is not ("." or ".." or "plan.tmp")))
            throw new InvalidDataException("La compactación sin plan contiene archivos inesperados.");
        byte[] original = ReadAt(root, PrimaryName);
        if (!original.AsSpan().SequenceEqual(ReadAt(root, ReplicaName)))
            throw new InvalidDataException("La compactación sin plan no conserva dos registros idénticos.");
        _ = Parse(original, binding);
        return original;
    }

    private static void WriteCheckpointFile(SafeFileHandle directory, string name, byte[] expected, Action? partial = null)
    {
        if (WindowsProbeNative.PlainFileExists(directory, name))
        {
            if (!ReadAt(directory, name).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("El archivo de compactación difiere del contenido esperado.");
            return;
        }
        using var draft = WindowsProbeNative.OpenPlainFile(directory, name + ".tmp", FileAccess.ReadWrite,
            FileShare.None, FileMode.OpenOrCreate, rename: true);
        if (draft.Length > expected.Length)
            throw new InvalidDataException("El temporal de compactación supera su tamaño esperado.");
        byte[] prefix = new byte[(int)draft.Length];
        draft.ReadExactly(prefix);
        if (!expected.AsSpan(0, prefix.Length).SequenceEqual(prefix))
            throw new InvalidDataException("El temporal de compactación contiene otros bytes.");
        if (draft.Length < expected.Length / 2)
        {
            draft.Write(expected.AsSpan(prefix.Length, expected.Length / 2 - prefix.Length));
            draft.Flush(flushToDisk: true);
            partial?.Invoke();
        }
        draft.Write(expected.AsSpan((int)draft.Length));
        draft.Flush(flushToDisk: true);
        draft.Position = 0;
        if (!SHA256.HashData(draft).AsSpan().SequenceEqual(SHA256.HashData(expected)))
            throw new InvalidDataException("El temporal de compactación no verifica.");
        WindowsProbeNative.RenameNew(draft.SafeFileHandle, directory, name);
    }

    private static byte[] CheckpointContent(SafeFileHandle root, SafeFileHandle directory, CheckpointPlan plan, DestinationBinding binding)
    {
        byte[]? compact = null, original = null;
        foreach (var (parent, name) in new[] { (root, PrimaryName), (root, ReplicaName), (directory, "old-primary"),
            (directory, "old-replica"), (directory, "primary"), (directory, "replica") })
        {
            if (!WindowsProbeNative.PlainFileExists(parent, name)) continue;
            byte[] value = ReadAt(parent, name), hash = SHA256.HashData(value);
            if (hash.AsSpan().SequenceEqual(plan.After)) compact = value;
            else if (hash.AsSpan().SequenceEqual(plan.Before)) original = value;
            else throw new InvalidDataException("Un archivo de compactación no coincide con su registro durable.");
        }
        if (compact is null && original is not null) compact = Compact(Parse(original, binding), binding);
        if (compact is null || !SHA256.HashData(compact).AsSpan().SequenceEqual(plan.After))
            throw new InvalidDataException("No se puede comprobar el registro compactado.");
        _ = Parse(compact, binding);
        return compact;
    }

    private static SafeFileHandle? OpenCheckpointDirectory(SafeFileHandle root)
    {
        try { return WindowsProbeNative.OpenPlainDirectory(root, CheckpointDirectory); }
        catch (FileNotFoundException) { return null; }
    }
    private static string[] CheckpointNames(SafeFileHandle root)
    {
        string[] names = WindowsProbeNative.ReadDirectory(root).Where(entry => entry.Name is not ("." or ".."))
            .Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray();
        for (int index = 0; index < names.Length; index++)
            if (ParseNumber(names[index]) != index) throw new InvalidDataException("Falta una transición del registro compactado.");
        return names;
    }
    private static long ParseNumber(string name) => name.Length == 16 && long.TryParse(name, NumberStyles.None,
        CultureInfo.InvariantCulture, out long number) ? number : throw new InvalidDataException("Nombre de compactación inválido.");
    private static byte[] EncodePlan(CheckpointPlan plan)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(plan, Json);
        return [.. SHA256.HashData(bytes), .. bytes];
    }
    private static CheckpointPlan ReadPlan(SafeFileHandle directory, DestinationBinding binding)
    {
        byte[] bytes = ReadAt(directory, "plan");
        if (bytes.Length is < 33 or > 1024 || !SHA256.HashData(bytes.AsSpan(32)).AsSpan().SequenceEqual(bytes.AsSpan(0, 32)))
            throw new InvalidDataException("El plan de compactación está dañado.");
        var plan = JsonSerializer.Deserialize<CheckpointPlan>(bytes.AsSpan(32), Json)
            ?? throw new InvalidDataException("Falta el plan de compactación.");
        if (plan.Version != 1 || plan.Marker != binding.Marker || plan.Identity is not { Length: 24 } ||
            !plan.Identity.AsSpan().SequenceEqual(binding.Identity) || plan.Before is not { Length: 32 } || plan.After is not { Length: 32 })
            throw new InvalidDataException("El plan de compactación pertenece a otro almacén.");
        return plan;
    }
    private static bool Complete(SafeFileHandle directory, CheckpointPlan plan) =>
        WindowsProbeNative.PlainFileExists(directory, "complete") &&
        (ReadAt(directory, "complete").AsSpan().SequenceEqual(SHA256.HashData(EncodePlan(plan))) ? true :
            throw new InvalidDataException("La confirmación de compactación está dañada."));
    private static bool Matches(SafeFileHandle parent, string name, byte[] hash) =>
        WindowsProbeNative.PlainFileExists(parent, name) && SHA256.HashData(ReadAt(parent, name)).AsSpan().SequenceEqual(hash);
    private static byte[] ReadAt(SafeFileHandle parent, string name)
    {
        using var input = WindowsProbeNative.OpenPlainFile(parent, name, FileAccess.Read, FileShare.Read);
        return ReadBytes(input);
    }
}
