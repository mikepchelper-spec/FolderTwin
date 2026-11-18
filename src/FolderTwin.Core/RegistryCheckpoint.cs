using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace FolderTwin.Core;


internal enum RegistryCheckpointStep { DirectoryCreated, PlanPartial, Planned, SnapshotPartial, Staged, PrimarySaved, PrimaryPublished, ReplicaSaved, ReplicaPublished, Completed }

internal static partial class GenerationRegistry
{
    private const string CheckpointDirectory = ".foldertwin-registry-checkpoints";
    private sealed record CheckpointPlan(int Version, Guid Marker, byte[] Identity, byte[] Before, byte[] After,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? ArchiveAfter = null);
    private sealed record ArchiveDocument(int Version, Guid JobId, byte[] Identity,
        SortedDictionary<string, byte[]> Abandoned, SortedDictionary<string, byte[]> PurgeProofs);

    // The caller holds the store's exclusive lock. Reserving the maximum encoded
    // event size keeps the complete mirror/retention operation within the cap.
    internal static void Reserve(SafeFileHandle root, DestinationBinding binding, int events,
        Action<RegistryCheckpointStep>? checkpoint = null, int maximumBytes = MaximumBytes)
    {
        if (events < 1 || maximumBytes is < MaximumLineBytes or > MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(events));
        ReserveBytes(root, binding, checked((long)events * MaximumLineBytes), checkpoint, maximumBytes);
    }

    internal static void ReserveTransitions(SafeFileHandle root, DestinationBinding binding, int transitions,
        Action<RegistryCheckpointStep>? checkpoint = null, int maximumBytes = MaximumBytes)
    {
        if (transitions < 1 || maximumBytes is < MaximumLineBytes or > MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(transitions));
        ReserveBytes(root, binding, Math.Max(MaximumLineBytes, checked((long)transitions * TypicalTransitionBytes)), checkpoint, maximumBytes);
    }

    private static void ReserveBytes(SafeFileHandle root, DestinationBinding binding, long reserved,
        Action<RegistryCheckpointStep>? checkpoint, int maximumBytes)
    {
        CompleteCheckpoint(root, binding, checkpoint, reserved, maximumBytes);
        byte[] first = ReadAt(root, PrimaryName), second = ReadAt(root, ReplicaName);
        // A split append may only be repaired by Append's exact event proof.
        if (!first.AsSpan().SequenceEqual(second)) return;
        if (first.Length + reserved <= maximumBytes) return;
        var state = Parse(first, binding, root);
        var (compact, archiveHash) = BuildCompact(root, state, binding, reserved, maximumBytes);
        if (compact.Length + reserved > maximumBytes)
            throw new InvalidDataException("Las generaciones conservadas superan la capacidad del registro. Revisa la retención o crea otro destino conservando este respaldo.");
        using var checkpoints = WindowsProbeNative.OpenOrCreatePlainDirectory(root, CheckpointDirectory);
        long next = CheckpointNames(checkpoints).Select(ParseNumber).DefaultIfEmpty(-1).Max() + 1;
        string id = next.ToString("D16", CultureInfo.InvariantCulture);
        using var directory = WindowsProbeNative.OpenOrCreatePlainDirectory(checkpoints, id);
        checkpoint?.Invoke(RegistryCheckpointStep.DirectoryCreated);
        var plan = new CheckpointPlan(1, binding.Marker, binding.Identity, SHA256.HashData(first), SHA256.HashData(compact), archiveHash);
        WriteCheckpointFile(directory, "plan", EncodePlan(plan), () => checkpoint?.Invoke(RegistryCheckpointStep.PlanPartial));
        checkpoint?.Invoke(RegistryCheckpointStep.Planned);
        CompleteCheckpoint(root, binding, checkpoint, reserved, maximumBytes);
    }

    private static (byte[] Compact, byte[]? ArchiveHash) BuildCompact(SafeFileHandle root, RegistryState state, DestinationBinding binding,
        long reserved, int maximumBytes)
    {
        bool hasHistorical = state.Abandoned.Count > 0 || state.PurgeProofs.Count > 0;
        if (hasHistorical && WindowsProbeNative.PlainFileExists(root, ArchiveName))
        {
            byte[] archiveHash = SHA256.HashData(EncodeArchive(state, binding));
            return (Compact(state, binding, archiveHash), archiveHash);
        }
        byte[] inline = Compact(state, binding);
        if (inline.Length + reserved <= maximumBytes || !hasHistorical)
            return (inline, null);
        byte[] spillHash = SHA256.HashData(EncodeArchive(state, binding));
        return (Compact(state, binding, spillHash), spillHash);
    }

    private static byte[] Compact(RegistryState state, DestinationBinding binding, byte[]? archiveHash = null)
    {
        using var bytes = new MemoryStream();
        long sequence = 0;
        byte[] previous = new byte[32];
        Add("initialize", "", new byte[32]);
        if (archiveHash is not null)
            Add("archive-link", "archive", archiveHash);
        foreach (string id in state.RegistrationOrder)
        {
            Add("register", id, state.Generations[id]);
            if (state.Expired.Contains(id)) Add("expire", id, state.Generations[id]);
        }
        if (archiveHash is null)
        {
            foreach (var (id, hash) in state.Abandoned.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Add("register", id, hash);
                Add("abandon", id, hash);
            }
        }
        if (state.MirrorGeneration is { } mirror) Add("mirror", mirror, state.Generations[mirror]);
        if (state.PendingMirrorGeneration is { } pending) Add("mirror-begin", pending, state.Generations[pending]);
        if (archiveHash is null)
        {
            foreach (var (id, hash) in state.PurgeProofs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                Add("purge-proof", id, hash);
        }
        return bytes.ToArray();

        void Add(string kind, string id, byte[] hash)
        {
            byte[] line = Encode(new(EventVersion(kind), sequence++, kind, id, hash, binding.Marker, binding.Identity, previous));
            bytes.Write(line);
            previous = JsonSerializer.Deserialize<Envelope>(line.AsSpan(0, line.Length - 1), Json)!.Checksum;
        }
    }

    private static byte[] EncodeArchive(RegistryState state, DestinationBinding binding)
    {
        var abandoned = new SortedDictionary<string, byte[]>(state.Abandoned, StringComparer.Ordinal);
        var proofs = new SortedDictionary<string, byte[]>(state.PurgeProofs, StringComparer.Ordinal);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new ArchiveDocument(1, binding.Marker, binding.Identity, abandoned, proofs), Json);
        if (body.Length > MaximumArchiveBytes - 32)
            throw new InvalidDataException("El archivo histórico del registro supera su capacidad máxima.");
        return [.. SHA256.HashData(body), .. body];
    }

    private static void LoadArchivedEntries(SafeFileHandle root, DestinationBinding binding, byte[] expectedHash,
        Dictionary<string, byte[]> abandoned, Dictionary<string, byte[]> purgeProofs)
    {
        byte[]? verified = TryReadMatchingArchive(root, ArchiveName, expectedHash);
        byte[]? replica = TryReadMatchingArchive(root, ArchiveReplicaName, expectedHash);
        if (verified is null && replica is not null) verified = replica;
        if (verified is null)
        {
            using var checkpoints = OpenCheckpointDirectory(root);
            if (checkpoints is not null && CheckpointNames(checkpoints).LastOrDefault() is { } latest)
            {
                using var directory = WindowsProbeNative.OpenPlainDirectory(checkpoints, latest);
                foreach (string candidate in new[] { "archive-primary", "archive-replica", "old-archive-primary", "old-archive-replica" })
                {
                    verified = TryReadMatchingArchive(directory, candidate, expectedHash);
                    if (verified is not null) break;
                }
            }
        }
        if (verified is null)
            throw new InvalidDataException("Falta o difiere el archivo histórico enlazado por el registro de generaciones.");
        ParseArchiveBytes(verified, binding, abandoned, purgeProofs);
    }

    private static void ParseArchiveBytes(ReadOnlySpan<byte> encoded, DestinationBinding binding,
        Dictionary<string, byte[]> abandoned, Dictionary<string, byte[]> purgeProofs)
    {
        if (encoded.Length < 33 || encoded.Length > MaximumArchiveBytes ||
            !SHA256.HashData(encoded[32..]).AsSpan().SequenceEqual(encoded[..32]))
            throw new InvalidDataException("El archivo histórico del registro está dañado.");
        var doc = JsonSerializer.Deserialize<ArchiveDocument>(encoded[32..], Json)
            ?? throw new InvalidDataException("Falta el contenido del archivo histórico del registro.");
        if (doc.Version != 1 || doc.JobId != binding.Marker || doc.Identity is not { Length: 24 } ||
            !doc.Identity.AsSpan().SequenceEqual(binding.Identity) || doc.Abandoned is null || doc.PurgeProofs is null)
            throw new InvalidDataException("El archivo histórico del registro pertenece a otro almacén.");
        foreach (var (id, hash) in doc.Abandoned)
        {
            _ = VersionStore.GenerationTime(id);
            if (hash is not { Length: 32 } || !abandoned.TryAdd(id, hash))
                throw new InvalidDataException("Una generación abandonada archivada no es válida.");
        }
        foreach (var (id, hash) in doc.PurgeProofs)
        {
            RetentionJournal.CheckId(id);
            if (hash is not { Length: 32 } || !purgeProofs.TryAdd(id, hash))
                throw new InvalidDataException("Una prueba de purga archivada no es válida.");
        }
    }

    private static byte[]? TryReadMatchingArchive(SafeFileHandle parent, string name, ReadOnlySpan<byte> expectedHash)
    {
        if (!WindowsProbeNative.PlainFileExists(parent, name)) return null;
        using var input = WindowsProbeNative.OpenPlainFile(parent, name, FileAccess.Read, FileShare.Read);
        if (input.Length is < 33 or > MaximumArchiveBytes) return null;
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return SHA256.HashData(bytes).AsSpan().SequenceEqual(expectedHash) ? bytes : null;
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
        return CheckpointContent(root, directory, plan, binding).Compact;
    }

    private static void CompleteCheckpoint(SafeFileHandle root, DestinationBinding binding, Action<RegistryCheckpointStep>? checkpoint,
        long reserved = MaximumLineBytes, int maximumBytes = MaximumBytes)
    {
        using var checkpoints = OpenCheckpointDirectory(root);
        if (checkpoints is null) return;
        string? latest = CheckpointNames(checkpoints).LastOrDefault();
        if (latest is null) return;
        using var directory = WindowsProbeNative.OpenPlainDirectory(checkpoints, latest);
        if (!WindowsProbeNative.PlainFileExists(directory, "plan"))
        {
            byte[] original = RequireUnpublishedCheckpoint(root, directory, binding);
            var (rebuilt, archiveHash) = BuildCompact(root, Parse(original, binding, root), binding, reserved, maximumBytes);
            var pending = new CheckpointPlan(1, binding.Marker, binding.Identity, SHA256.HashData(original),
                SHA256.HashData(rebuilt), archiveHash);
            WriteCheckpointFile(directory, "plan", EncodePlan(pending));
        }
        var plan = ReadPlan(directory, binding);
        if (Complete(directory, plan)) { CleanOld(); return; }
        var (compact, archiveBytes) = CheckpointContent(root, directory, plan, binding);
        if (plan.ArchiveAfter is not null && archiveBytes is not null)
        {
            PrepareContent("archive-primary", ArchiveName, archiveBytes, plan.ArchiveAfter);
            PrepareContent("archive-replica", ArchiveReplicaName, archiveBytes, plan.ArchiveAfter);
        }
        Prepare("primary", PrimaryName);
        Prepare("replica", ReplicaName);
        checkpoint?.Invoke(RegistryCheckpointStep.Staged);
        if (plan.ArchiveAfter is not null)
        {
            PublishArchive("archive-primary", ArchiveName, plan.ArchiveAfter);
            PublishArchive("archive-replica", ArchiveReplicaName, plan.ArchiveAfter);
        }
        Publish("primary", PrimaryName, RegistryCheckpointStep.PrimarySaved, RegistryCheckpointStep.PrimaryPublished);
        Publish("replica", ReplicaName, RegistryCheckpointStep.ReplicaSaved, RegistryCheckpointStep.ReplicaPublished);
        WindowsProbeNative.FlushDirectory(root);
        WriteCheckpointFile(directory, "complete", SHA256.HashData(EncodePlan(plan)));
        checkpoint?.Invoke(RegistryCheckpointStep.Completed);
        CleanOld();

        void Prepare(string name, string rootName) => PrepareContent(name, rootName, compact, plan.After);
        void PrepareContent(string name, string rootName, byte[] content, byte[] expectedHash)
        {
            if (MatchesAny(root, rootName, expectedHash)) return;
            WriteCheckpointFile(directory, name, content, () => checkpoint?.Invoke(RegistryCheckpointStep.SnapshotPartial));
        }
        void PublishArchive(string name, string rootName, byte[] expectedHash)
        {
            if (MatchesAny(root, rootName, expectedHash)) return;
            if (WindowsProbeNative.PlainFileExists(root, rootName))
            {
                using var old = WindowsProbeNative.OpenPlainFile(root, rootName, FileAccess.Read, FileShare.None, rename: true);
                WindowsProbeNative.RenameNew(old.SafeFileHandle, directory, "old-" + name);
            }
            using var next = WindowsProbeNative.OpenPlainFile(directory, name, FileAccess.Read, FileShare.None, rename: true);
            if (!SHA256.HashData(next).AsSpan().SequenceEqual(expectedHash))
                throw new InvalidDataException("El archivo histórico preparado está dañado.");
            WindowsProbeNative.RenameNew(next.SafeFileHandle, root, rootName);
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
            foreach (string name in new[] { "old-archive-primary", "old-archive-replica" })
            {
                if (!WindowsProbeNative.PlainFileExists(directory, name)) continue;
                using var old = WindowsProbeNative.OpenPlainFile(directory, name, FileAccess.Read, FileShare.None, rename: true);
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
        _ = Parse(original, binding, root);
        return original;
    }

    private static void WriteCheckpointFile(SafeFileHandle directory, string name, byte[] expected, Action? partial = null)
    {
        if (WindowsProbeNative.PlainFileExists(directory, name))
        {
            if (!ReadAtBounded(directory, name, Math.Max(MaximumBytes, MaximumArchiveBytes)).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("El archivo de compactación difiere del contenido esperado.");
            return;
        }
        using var draft = WindowsProbeNative.OpenPlainFile(directory, name + ".tmp", FileAccess.ReadWrite,
            FileShare.None, FileMode.OpenOrCreate, rename: true);
        if (draft.Length > expected.Length)
            throw new InvalidDataException("El temporal de compactación supera su tamaño esperado.");
        byte[] prefix = new byte[(int)draft.Length];
        draft.ReadExactly(prefix);
        int clean = ValidZeroExtendedPrefix(prefix, expected);
        if (clean >= 0 && clean < prefix.Length)
        {
            draft.SetLength(clean);
            draft.Flush(flushToDisk: true);
            draft.Position = clean;
            prefix = prefix[..clean];
        }
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
        WindowsProbeNative.FlushDirectory(directory);
    }

    private static (byte[] Compact, byte[]? ArchiveBytes) CheckpointContent(SafeFileHandle root, SafeFileHandle directory,
        CheckpointPlan plan, DestinationBinding binding)
    {
        byte[]? compact = null, original = null, archiveBytes = null;
        foreach (var (parent, name) in new[] { (root, PrimaryName), (root, ReplicaName), (directory, "old-primary"),
            (directory, "old-replica"), (directory, "primary"), (directory, "replica") })
        {
            if (!WindowsProbeNative.PlainFileExists(parent, name)) continue;
            byte[] value = ReadAt(parent, name), hash = SHA256.HashData(value);
            if (hash.AsSpan().SequenceEqual(plan.After)) compact = value;
            else if (hash.AsSpan().SequenceEqual(plan.Before)) original = value;
            else throw new InvalidDataException("Un archivo de compactación no coincide con su registro durable.");
        }
        if (plan.ArchiveAfter is not null)
        {
            foreach (var (parent, name) in new[] { (root, ArchiveName), (root, ArchiveReplicaName),
                (directory, "archive-primary"), (directory, "archive-replica") })
            {
                archiveBytes = TryReadMatchingArchive(parent, name, plan.ArchiveAfter);
                if (archiveBytes is not null) break;
            }
        }
        if ((compact is null || plan.ArchiveAfter is not null && archiveBytes is null) && original is not null)
        {
            var parsed = Parse(original, binding, root);
            if (plan.ArchiveAfter is not null)
            {
                archiveBytes ??= EncodeArchive(parsed, binding);
                if (!SHA256.HashData(archiveBytes).AsSpan().SequenceEqual(plan.ArchiveAfter))
                    throw new InvalidDataException("No se puede reconstruir el archivo histórico del registro.");
                compact ??= Compact(parsed, binding, plan.ArchiveAfter);
            }
            else compact ??= Compact(parsed, binding);
        }
        if (compact is null || !SHA256.HashData(compact).AsSpan().SequenceEqual(plan.After))
            throw new InvalidDataException("No se puede comprobar el registro compactado.");
        if (plan.ArchiveAfter is not null)
        {
            if (archiveBytes is null)
                throw new InvalidDataException("Falta el archivo histórico preparado para la compactación.");
            ParseArchiveBytes(archiveBytes, binding, new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        }
        else _ = Parse(compact, binding, root);
        return (compact, archiveBytes);
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
            !plan.Identity.AsSpan().SequenceEqual(binding.Identity) || plan.Before is not { Length: 32 } || plan.After is not { Length: 32 } ||
            plan.ArchiveAfter is not null && plan.ArchiveAfter.Length != 32)
            throw new InvalidDataException("El plan de compactación pertenece a otro almacén.");
        return plan;
    }
    private static bool Complete(SafeFileHandle directory, CheckpointPlan plan) =>
        WindowsProbeNative.PlainFileExists(directory, "complete") &&
        (ReadAt(directory, "complete").AsSpan().SequenceEqual(SHA256.HashData(EncodePlan(plan))) ? true :
            throw new InvalidDataException("La confirmación de compactación está dañada."));
    private static bool Matches(SafeFileHandle parent, string name, byte[] hash) =>
        WindowsProbeNative.PlainFileExists(parent, name) && SHA256.HashData(ReadAt(parent, name)).AsSpan().SequenceEqual(hash);
    private static bool MatchesAny(SafeFileHandle parent, string name, byte[] hash) =>
        TryReadMatchingArchive(parent, name, hash) is not null;
    private static byte[] ReadAt(SafeFileHandle parent, string name) => ReadAtBounded(parent, name, MaximumBytes);
    private static byte[] ReadAtBounded(SafeFileHandle parent, string name, int limit)
    {
        using var input = WindowsProbeNative.OpenPlainFile(parent, name, FileAccess.Read, FileShare.Read);
        if (input.Length < 1 || input.Length > limit)
            throw new InvalidDataException("Tamaño del registro de generaciones no válido.");
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }
}
