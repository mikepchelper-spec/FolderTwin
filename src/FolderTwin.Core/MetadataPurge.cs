using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FolderTwin.Core;


internal sealed record PurgedMetadataFile(string Area, string Name, byte[] Identity, long Length, byte[] Hash);
internal sealed record PurgedMetadataPoint(string Generation, byte[] DocumentHash, PurgedMetadataFile[] Files);
internal sealed record MetadataPurgePlan(int Version, Guid Marker, byte[] Identity, string Operation,
    byte[] RetentionHash, PurgedMetadataPoint[] Points);

internal sealed partial class VersionStore
{
    private const string MetadataPlanName = "metadata-plan";
    private const string MetadataReplicaName = "metadata-replica";

    // Keep the compact evidence in the existing retention journal. The replicated
    // registry records its hash before removing any generation expectation.
    private void CompleteMetadataPurge(SafeFileHandle root, SafeFileHandle journal, RetentionPlan retention,
        Action<RetentionStep>? checkpoint, Action<RegistryStep>? registryCheckpoint = null, bool alreadyVerifiedObjects = false)
    {
        if (RetentionJournal.HasState(journal, retention, "metadata-complete")) return;
        bool noHooks = checkpoint is null && registryCheckpoint is null;
        bool verifiedInLock = alreadyVerifiedObjects && noHooks;
        MetadataPurgePlan plan;
        if (WindowsProbeNative.PlainFileExists(journal, MetadataPlanName) || WindowsProbeNative.PlainFileExists(journal, MetadataReplicaName))
            plan = ReadMetadataPurge(journal, retention, requireBoth: false);
        else
        {
            var inventory = AuditRetention(root, allowMetadataPurge: true, verifyObjectHashes: !verifiedInLock);
            verifiedInLock = noHooks;
            ValidatePlanReferences(retention, inventory);
            if (retention.Review.Expire.Length == 0)
            {
                RetentionJournal.SetState(journal, retention, "metadata-complete");
                return;
            }
            var points = retention.Review.Expire.Select(id =>
            {
                if (!inventory.Registry.Expired.Contains(id))
                    throw new InvalidDataException("Una fecha de la purga volvió a estar retenida.");
                return new PurgedMetadataPoint(id, inventory.Registry.Generations[id], ReadMetadataFiles(root, id));
            }).ToArray();
            plan = new(1, binding.Marker, binding.Identity, retention.Id, SHA256.HashData(RetentionJournal.Encode(retention)), points);
        }
        byte[] encoded = EncodeMetadataPurge(plan), hash = SHA256.HashData(encoded);
        var before = GenerationRegistry.ReadCommonPrefix(root, binding);
        bool deleting = RetentionJournal.HasState(journal, retention, "metadata-deleting");
        bool hasProof = before.PurgeProofs.TryGetValue(retention.Id, out var registered);
        if (hasProof && !registered!.AsSpan().SequenceEqual(hash) || deleting && !hasProof)
            throw new InvalidDataException("La prueba de purga registrada cambió o falta.");
        foreach (var point in plan.Points)
        {
            if (before.Generations.TryGetValue(point.Generation, out var expected))
            {
                if (!before.Expired.Contains(point.Generation) || !expected.AsSpan().SequenceEqual(point.DocumentHash))
                    throw new InvalidDataException("Una fecha de la prueba no coincide con el registro de expiraciones.");
            }
            else if (!deleting) throw new InvalidDataException("Falta una fecha antes de comenzar la purga de sus metadatos.");
        }
        GenerationRegistry.ReserveTransitions(root, binding, checked(plan.Points.Length + 1));
        WriteAbandonRecord(journal, MetadataPlanName, encoded, () => checkpoint?.Invoke(RetentionStep.MetadataPlanPartial));
        WriteAbandonRecord(journal, MetadataReplicaName, encoded, () => checkpoint?.Invoke(RetentionStep.MetadataPlanPartial));
        checkpoint?.Invoke(RetentionStep.MetadataPlanned);
        if (!hasProof) GenerationRegistry.RecordPurgeProof(root, binding, retention.Id, hash, registryCheckpoint);
        checkpoint?.Invoke(RetentionStep.MetadataRegistered);
        if (deleting)
            foreach (var point in plan.Points)
            {
                using var commits = WindowsProbeNative.OpenPlainDirectory(root, CommitDirectory);
                var prefix = GenerationRegistry.ReadCommonPrefix(root, binding);
                if (prefix.Generations.ContainsKey(point.Generation) && !WindowsProbeNative.PlainFileExists(commits, point.Generation + ".commit"))
                    GenerationRegistry.ForgetPurged(root, binding, point.Generation, point.DocumentHash, registryCheckpoint);
            }
        // The continuing inventory checks all retained catalogs, expectations and
        // active objects. Only this exact durable set may have missing metadata.
        var live = AuditRetention(root, plan, verifyObjectHashes: !verifiedInLock);
        foreach (var point in plan.Points)
        {
            if (live.Registry.Generations.ContainsKey(point.Generation) && !live.Registry.Expired.Contains(point.Generation) ||
                live.Registry.MirrorGeneration == point.Generation || live.Registry.PendingMirrorGeneration == point.Generation)
                throw new InvalidDataException("La limpieza de metadatos alcanza una fecha retenida o un espejo pendiente.");
            foreach (var file in point.Files)
            {
                using var area = OpenMetadataArea(root, file.Area);
                if (!WindowsProbeNative.PlainFileExists(area, file.Name))
                {
                    if (!deleting) throw new InvalidDataException("Falta un metadato antes de comenzar su purga.");
                    continue;
                }
                using var input = WindowsProbeNative.OpenPlainFile(area, file.Name, FileAccess.Read, FileShare.None);
                RequireMetadataFile(input, file);
            }
        }
        RetentionJournal.SetState(journal, retention, "metadata-deleting");
        checkpoint?.Invoke(RetentionStep.MetadataDeleting);
        foreach (var point in plan.Points)
        {
            // Remove the commit first, so an interrupted cleanup never advertises
            // a forgotten point as retained in the independent recovery browser.
            foreach (var file in point.Files.OrderBy(file => file.Area == CommitDirectory ? 0 : 1))
            {
                using var area = OpenMetadataArea(root, file.Area);
                if (!WindowsProbeNative.PlainFileExists(area, file.Name)) continue;
                using (var input = WindowsProbeNative.OpenPlainFile(area, file.Name, FileAccess.Read, FileShare.None, rename: true))
                {
                    RequireMetadataFile(input, file);
                    WindowsProbeNative.DeletePlainFile(input.SafeFileHandle);
                }
                checkpoint?.Invoke(RetentionStep.MetadataDeleted);
            }
            var prefix = GenerationRegistry.ReadCommonPrefix(root, binding);
            if (prefix.Generations.ContainsKey(point.Generation))
                GenerationRegistry.ForgetPurged(root, binding, point.Generation, point.DocumentHash, registryCheckpoint);
            checkpoint?.Invoke(RetentionStep.MetadataForgotten);
        }
        _ = GenerationRegistry.Read(root, binding);
        RetentionJournal.SetState(journal, retention, "metadata-complete");
        checkpoint?.Invoke(RetentionStep.MetadataCompleted);
    }

    private PurgedMetadataFile[] ReadMetadataFiles(SafeFileHandle root, string generation)
    {
        var result = new List<PurgedMetadataFile>();
        foreach (var (area, name) in MetadataPaths(root, generation)) Add(area, name);
        return result.ToArray();

        void Add(string area, string name)
        {
            using var parent = OpenMetadataArea(root, area);
            using var input = WindowsProbeNative.OpenPlainFile(parent, name, FileAccess.Read, FileShare.None);
            WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, name);
            if (input.Length is < 1 or > BatchCatalog.VersionMaximumBytes)
                throw new InvalidDataException("Un metadato de la purga supera su tamaño admitido.");
            result.Add(new(area, name, WindowsProbeNative.DirectoryIdentity(input.SafeFileHandle), input.Length, SHA256.HashData(input)));
        }
    }

    private static (string Area, string Name)[] MetadataPaths(SafeFileHandle root, string generation)
    {
        var result = RequiredMetadataPaths(generation).ToList();
        string context = Path.Combine(MirrorJournal.DirectoryName, MirrorJournal.OperationsName, generation);
        SafeFileHandle? directory = null;
        try { directory = OpenMetadataArea(root, context); }
        catch (FileNotFoundException) { }
        using (directory)
            if (directory is not null)
                foreach (string name in new[] { "source.jsonl", "source.location.json", "baseline.jsonl" })
                    if (WindowsProbeNative.PlainFileExists(directory, name)) result.Add((context, name));
        return result.ToArray();
    }

    private static long MeasureMetadataBytes(SafeFileHandle root, string generation)
    {
        long bytes = 0;
        foreach (var (area, name) in MetadataPaths(root, generation))
        {
            using var parent = OpenMetadataArea(root, area);
            using var input = WindowsProbeNative.OpenPlainFile(parent, name, FileAccess.Read, FileShare.None);
            bytes = checked(bytes + input.Length);
        }
        return bytes;
    }

    private static IEnumerable<(string Area, string Name)> RequiredMetadataPaths(string generation) =>
    [
        (PrimaryDirectory, generation + ".jsonl"), (ReplicaDirectory, generation + ".jsonl"),
        (CommitDirectory, generation + ".commit"), (PendingDirectory, generation + ".jsonl"),
        (PendingDirectory, generation + LocationSuffix)
    ];

    private static SafeFileHandle OpenMetadataArea(SafeFileHandle root, string area)
    {
        string[] parts = area.Split(Path.DirectorySeparatorChar);
        SafeFileHandle? current = null;
        try
        {
            foreach (string part in parts)
            {
                DirectoryFence.CheckName(part);
                var next = WindowsProbeNative.OpenPlainDirectory(current ?? root, part);
                current?.Dispose();
                current = next;
            }
            var result = current ?? throw new InvalidDataException("Falta el directorio de metadatos.");
            current = null;
            return result;
        }
        finally { current?.Dispose(); }
    }

    private static void RequireMetadataFile(FileStream input, PurgedMetadataFile file)
    {
        WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, file.Name);
        if (input.Length != file.Length || !WindowsProbeNative.DirectoryIdentity(input.SafeFileHandle).AsSpan().SequenceEqual(file.Identity) ||
            !SHA256.HashData(input).AsSpan().SequenceEqual(file.Hash))
            throw new InvalidDataException("Un metadato cambió desde la revisión de purga. Se conserva para investigar.");
    }

    private static byte[] EncodeMetadataPurge(MetadataPurgePlan plan)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(plan, AbandonJson);
        if (body.Length > BatchCatalog.VersionMaximumBytes - 32)
            throw new InvalidDataException("La prueba de purga de metadatos supera su límite.");
        return [.. SHA256.HashData(body), .. body];
    }

    private MetadataPurgePlan ReadMetadataPurge(SafeFileHandle journal, RetentionPlan retention, bool requireBoth = true)
    {
        string first = WindowsProbeNative.PlainFileExists(journal, MetadataPlanName) ? MetadataPlanName : MetadataReplicaName;
        byte[] encoded = Read(first);
        if (requireBoth && (!Read(MetadataPlanName).AsSpan().SequenceEqual(encoded) || !Read(MetadataReplicaName).AsSpan().SequenceEqual(encoded)))
            throw new InvalidDataException("Las pruebas de purga de metadatos difieren.");
        if (encoded.Length < 33 || !SHA256.HashData(encoded.AsSpan(32)).AsSpan().SequenceEqual(encoded.AsSpan(0, 32)))
            throw new InvalidDataException("La prueba de purga de metadatos está dañada.");
        var plan = JsonSerializer.Deserialize<MetadataPurgePlan>(encoded.AsSpan(32), AbandonJson)
            ?? throw new InvalidDataException("Falta la prueba de purga de metadatos.");
        if (plan.Version != 1 || plan.Marker != binding.Marker || plan.Identity is not { Length: 24 } ||
            !plan.Identity.AsSpan().SequenceEqual(binding.Identity) || plan.Operation != retention.Id ||
            plan.RetentionHash is not { Length: 32 } || !plan.RetentionHash.AsSpan().SequenceEqual(SHA256.HashData(RetentionJournal.Encode(retention))) ||
            plan.Points is null || !plan.Points.Select(point => point?.Generation).SequenceEqual(retention.Review.Expire))
            throw new InvalidDataException("La prueba de metadatos no corresponde al plan de retención.");
        foreach (var point in plan.Points)
        {
            if (point.DocumentHash is not { Length: 32 } || point.Files is null || point.Files.Length is < 5 or > 8)
                throw new InvalidDataException("La prueba de una fecha purgada no está completa.");
            var required = RequiredMetadataPaths(point.Generation).ToHashSet();
            var allowed = required.Concat(new[] { "source.jsonl", "source.location.json", "baseline.jsonl" }
                .Select(name => (Path.Combine(MirrorJournal.DirectoryName, MirrorJournal.OperationsName, point.Generation), name))).ToHashSet();
            var observed = new HashSet<(string, string)>();
            foreach (var file in point.Files)
                if (file is null || !allowed.Contains((file.Area, file.Name)) || !observed.Add((file.Area, file.Name)) ||
                    file.Identity is not { Length: 24 } || file.Hash is not { Length: 32 } || file.Length is < 1 or > BatchCatalog.VersionMaximumBytes)
                    throw new InvalidDataException("La prueba de metadatos contiene una ruta, tamaño o hash no permitido.");
            if (!required.IsSubsetOf(observed)) throw new InvalidDataException("Faltan metadatos esperados en la prueba de purga.");
        }
        return plan;

        byte[] Read(string name)
        {
            using var file = WindowsProbeNative.OpenPlainFile(journal, name, FileAccess.Read, FileShare.Read);
            if (file.Length is < 33 or > BatchCatalog.VersionMaximumBytes)
                throw new InvalidDataException("La prueba de purga tiene un tamaño fuera de límites.");
            byte[] bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            return bytes;
        }
    }

    private void RequireMetadataPurgeProofs(SafeFileHandle root, bool allowPending = false)
    {
        if (!registeredGenerations) return;
        var registry = GenerationRegistry.ReadCommonPrefix(root, binding);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        SafeFileHandle all;
        try { all = WindowsProbeNative.OpenPlainDirectory(root, RetentionJournal.DirectoryName); }
        catch (FileNotFoundException) when (registry.PurgeProofs.Count == 0) { return; }
        using (all)
        foreach (var entry in WindowsProbeNative.ReadDirectory(all).Where(entry => entry.Name is not ("." or "..")))
        {
            using var directory = WindowsProbeNative.OpenPlainDirectory(all, entry.Name);
            bool planned = WindowsProbeNative.PlainFileExists(directory, MetadataPlanName) ||
                WindowsProbeNative.PlainFileExists(directory, MetadataReplicaName) ||
                WindowsProbeNative.PlainFileExists(directory, MetadataPlanName + ".tmp") ||
                WindowsProbeNative.PlainFileExists(directory, MetadataReplicaName + ".tmp");
            if (!planned) continue;
            var retention = RetentionJournal.Read(directory, binding, entry.Name);
            _ = RetentionJournal.State(directory, retention);
            bool complete = RetentionJournal.HasState(directory, retention, "metadata-complete");
            if (!complete)
            {
                if (!allowPending) throw new InvalidDataException("Hay una purga de metadatos pendiente. Reanúdala antes de copiar o limpiar.");
                if (registry.PurgeProofs.ContainsKey(entry.Name)) observed.Add(entry.Name);
                continue;
            }
            var plan = ReadMetadataPurge(directory, retention);
            if (!registry.PurgeProofs.TryGetValue(entry.Name, out var hash) ||
                !hash.AsSpan().SequenceEqual(SHA256.HashData(EncodeMetadataPurge(plan))) ||
                plan.Points.Any(point => registry.Generations.ContainsKey(point.Generation)))
                throw new InvalidDataException("La prueba de una purga confirmada falta o contradice el registro.");
            observed.Add(entry.Name);
        }
        if (!observed.SetEquals(registry.PurgeProofs.Keys))
            throw new InvalidDataException("Falta una prueba de purga esperada. La escritura y limpieza quedan suspendidas.");
    }

    private void RequireGenerationNotPurged(SafeFileHandle root, string generation)
    {
        if (!registeredGenerations) return;
        var registry = GenerationRegistry.Read(root, binding);
        if (registry.PurgeProofs.Count == 0) return;
        using var all = WindowsProbeNative.OpenPlainDirectory(root, RetentionJournal.DirectoryName);
        foreach (string id in registry.PurgeProofs.Keys)
        {
            using var journal = WindowsProbeNative.OpenPlainDirectory(all, id);
            var retention = RetentionJournal.Read(journal, binding, id);
            if (ReadMetadataPurge(journal, retention).Points.Any(point => point.Generation == generation))
                throw new InvalidDataException("La fecha ya se purgó de este almacén. Inicia un punto con identidad nueva.");
        }
    }
}
