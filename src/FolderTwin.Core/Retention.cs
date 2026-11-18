using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace FolderTwin.Core;


internal sealed record RetentionPolicy(int DailyDays = 30, int WeeklyWeeks = 8, int MonthlyMonths = 12, int GraceDays = 7)
{
    private sealed record Point(string Id, DateTimeOffset Date);
    internal void Validate()
    {
        if (DailyDays is < 1 or > 3660 || WeeklyWeeks is < 0 or > 520 || MonthlyMonths is < 0 or > 120 ||
            GraceDays is < 1 or > 365)
            throw new InvalidDataException("Retención fuera de límites: diarios 1–3660, semanas 0–520, meses 0–120, gracia 1–365 días.");
    }

    // UTC calendar buckets; always keep the most recently registered generation as well.
    internal HashSet<string> Select(string[] registered, DateTimeOffset now)
    {
        Validate();
        now = now.ToUniversalTime();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        if (registered.Length == 0)
            return kept;
        var points = registered.Select(id => new Point(id, VersionStore.GenerationTime(id))).ToArray();
        if (points.Any(point => point.Date > now))
            throw new InvalidDataException("Hay generaciones posteriores al reloj actual. La retención queda suspendida.");
        kept.Add(registered[^1]);
        AddBuckets(points.Where(point => point.Date.UtcDateTime.Date >= now.UtcDateTime.Date.AddDays(1 - DailyDays)),
            point => point.Date.UtcDateTime.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        DateTime monday = now.UtcDateTime.Date.AddDays(-((int)now.DayOfWeek + 6) % 7);
        if (WeeklyWeeks > 0)
            AddBuckets(points.Where(point => point.Date.UtcDateTime.Date >= monday.AddDays(-7 * (WeeklyWeeks - 1))),
                point => $"{ISOWeek.GetYear(point.Date.UtcDateTime)}-{ISOWeek.GetWeekOfYear(point.Date.UtcDateTime)}");
        if (MonthlyMonths > 0)
        {
            DateTime first = new(now.Year, now.Month, 1);
            AddBuckets(points.Where(point => point.Date.UtcDateTime.Date >= first.AddMonths(1 - MonthlyMonths)),
                point => point.Date.ToString("yyyyMM", CultureInfo.InvariantCulture));
        }
        return kept;

        void AddBuckets(IEnumerable<Point> candidates, Func<Point, string> bucket)
        {
            // The enumeration follows the durable registration order, independent of clock rollback.
            foreach (var group in candidates.GroupBy(bucket))
                kept.Add(group.Last().Id);
        }
    }
}

internal sealed record RetentionPreview(RetentionPolicy Policy, DateTimeOffset ReviewedUtc, byte[] RegistryHash,
    string[] Keep, string[] Expire, string[] Objects, long QuarantineBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long MetadataBytes = 0);

internal sealed partial class VersionStore
{
    private sealed record RetentionInventory(RegistryState Registry, Dictionary<string, HashSet<string>> References,
        Dictionary<string, BatchFile> Objects, Dictionary<string, string> Sources, HashSet<string> AbandonedObjects,
        Dictionary<string, long> MetadataBytes);

    internal RetentionPreview PreviewRetention(RetentionPolicy policy, DateTimeOffset? clock = null)
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.ReadWrite);
        RequireNoRetentionInProgress(store.Root);
        var inventory = AuditRetention(store.Root);
        DateTimeOffset now = (clock ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return BuildRetentionPreview(inventory, policy, now);
    }

    private static RetentionPreview BuildRetentionPreview(RetentionInventory inventory, RetentionPolicy policy, DateTimeOffset now)
    {
        string[] active = inventory.Registry.RegistrationOrder.Where(id => !inventory.Registry.Expired.Contains(id)).ToArray();
        var keep = active.GroupBy(id => (inventory.Sources[id], Partial: IsPartialPoint(id)))
            .SelectMany(group => policy.Select(group.ToArray(), now)).ToHashSet(StringComparer.Ordinal);
        if (inventory.Registry.MirrorGeneration is { } current)
            keep.Add(current);
        string[] expire = active.Where(id => !keep.Contains(id)).ToArray();
        var referenced = new HashSet<string>(keep.SelectMany(id => inventory.References[id]), StringComparer.Ordinal);
        string[] candidates = expire.SelectMany(id => inventory.References[id])
            .Concat(inventory.AbandonedObjects)
            .Distinct(StringComparer.Ordinal).Where(digest => !referenced.Contains(digest)).Order(StringComparer.Ordinal).ToArray();
        return new(policy, now, inventory.Registry.LastHash, active.Where(keep.Contains).ToArray(), expire,
            candidates, candidates.Sum(digest => inventory.Objects[digest].Length), expire.Sum(id => inventory.MetadataBytes[id]));
    }

    private RetentionInventory AuditRetention(SafeFileHandle root, MetadataPurgePlan? continuing = null, bool allowMetadataPurge = false,
        bool verifyObjectHashes = true)
    {
        if (!registeredGenerations)
            throw new InvalidDataException("El almacén anterior no tiene registro durable para retención.");
        RequireNoAbandonInProgress(root, allowMetadataPurge: continuing is not null || allowMetadataPurge);
        RequireNoPendingMirror(root);
        var registry = GenerationRegistry.Read(root, binding);
        var pruning = continuing?.Points.Select(point => point.Generation).ToHashSet(StringComparer.Ordinal) ?? [];
        using var pending = WindowsProbeNative.OpenPlainDirectory(root, PendingDirectory);
        foreach (var entry in WindowsProbeNative.ReadDirectory(pending).Where(entry => entry.Name is not ("." or "..")))
        {
            string? id = entry.Name.EndsWith(LocationSuffix, StringComparison.Ordinal) ? entry.Name[..^LocationSuffix.Length] :
                entry.Name.EndsWith(".jsonl", StringComparison.Ordinal) ? entry.Name[..^6] : null;
            if (id is null || !pruning.Contains(id) && !registry.Abandoned.ContainsKey(id) && (!registry.Generations.ContainsKey(id) || !IsCommitted(root, id)))
                throw new InvalidDataException("Hay una operación pendiente o desconocida. La limpieza queda suspendida.");
        }
        using var commits = WindowsProbeNative.OpenPlainDirectory(root, CommitDirectory);
        var observed = WindowsProbeNative.ReadDirectory(commits).Where(entry => entry.Name is not ("." or "..")).Select(entry =>
        {
            if (!entry.Name.EndsWith(".commit", StringComparison.Ordinal))
                throw new InvalidDataException("Hay una confirmación incompleta o desconocida. La limpieza queda suspendida.");
            string id = entry.Name[..^7];
            _ = GenerationTime(id);
            return id;
        }).Where(id => !pruning.Contains(id)).ToHashSet(StringComparer.Ordinal);
        if (!observed.SetEquals(registry.Generations.Keys.Where(id => !pruning.Contains(id))))
            throw new InvalidDataException("Falta o sobra una generación esperada. La limpieza queda suspendida.");
        foreach (string name in new[] { PrimaryDirectory, ReplicaDirectory })
        {
            using var manifests = WindowsProbeNative.OpenPlainDirectory(root, name);
            var names = WindowsProbeNative.ReadDirectory(manifests).Where(entry => entry.Name is not ("." or ".."))
                .Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
            names.ExceptWith(pruning.Select(id => id + ".jsonl"));
            if (!names.SetEquals(registry.Generations.Keys.Where(id => !pruning.Contains(id)).Select(id => id + ".jsonl")))
                throw new InvalidDataException("El conjunto de manifiestos no coincide con las generaciones esperadas.");
        }
        var references = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var objects = new Dictionary<string, BatchFile>(StringComparer.Ordinal);
        var canonicalDigests = new Dictionary<string, string>(StringComparer.Ordinal);
        var digestLookup = canonicalDigests.GetAlternateLookup<ReadOnlySpan<char>>();
        var abandonedObjects = new HashSet<string>(StringComparer.Ordinal);
        var metadataBytes = new Dictionary<string, long>(StringComparer.Ordinal);
        Span<char> hexBuffer = stackalloc char[64];
        using var scope = workspace.OpenRegisteredRoot();
        foreach (var (id, hash) in registry.Generations)
        {
            if (pruning.Contains(id)) continue;
            var catalog = ReadPoint(root, id, requireBoth: true);
            if (!catalog.DocumentHash.AsSpan().SequenceEqual(hash))
                throw new InvalidDataException("Un catálogo esperado falta, difiere o está dañado. La limpieza queda suspendida.");
            var source = workspace.ReadRegisteredSource(Destination, scope.Root, IntentFiles(pending, id), hash);
            workspace.CheckRoots(source.Source, Destination);
            if (source.Identity.AsSpan().SequenceEqual(binding.Identity))
                throw new InvalidDataException("La identidad de origen no corresponde al catálogo esperado.");
            sources.Add(id, Convert.ToHexStringLower(source.Identity));
            metadataBytes.Add(id, MeasureMetadataBytes(root, id));
            var point = new HashSet<string>(catalog.Files.Length, StringComparer.Ordinal);
            foreach (var file in catalog.Files)
            {
                string digest = InternDigest(file, "Un objeto compartido tiene tamaños contradictorios.", hexBuffer);
                point.Add(digest);
            }
            references.Add(id, point);
        }
        foreach (var (id, hash) in registry.Abandoned)
        {
            var plan = ReadAbandonedIntent(scope.Root, root, pending, id, hash);
            var point = new HashSet<string>(plan.Files.Length, StringComparer.Ordinal);
            foreach (var file in plan.Files)
            {
                string digest = InternDigest(file, "Un objeto abandonado tiene referencias contradictorias.", hexBuffer);
                point.Add(digest);
                abandonedObjects.Add(digest);
            }
            references.Add(id, point);
        }
        var inventory = new RetentionInventory(registry, references, objects, sources, FindStoredObjects(root, abandonedObjects), metadataBytes);
        var activeFiles = ActiveReferences(inventory).Select(digest => objects[digest]).ToArray();
        if (verifyObjectHashes) VerifyObjects(root, activeFiles);
        else VerifyObjectsFast(root, activeFiles);
        return inventory;

        string InternDigest(BatchFile file, string mismatchMessage, Span<char> hex)
        {
            Convert.TryToHexStringLower(file.Hash, hex, out _);
            if (digestLookup.TryGetValue(hex, out string? existing))
            {
                if (objects[existing].Length != file.Length)
                    throw new InvalidDataException(mismatchMessage);
                return existing;
            }
            string created = new(hex);
            canonicalDigests.Add(created, created);
            objects.Add(created, file);
            return created;
        }
    }

    private void VerifyObjectsFast(SafeFileHandle store, BatchFile[] files)
    {
        using var objects = WindowsProbeNative.OpenPlainDirectory(store, ObjectDirectory);
        using var algorithm = WindowsProbeNative.OpenPlainDirectory(objects, Algorithm);
        var unique = DistinctObjects(files).ToArray();
        foreach (var group in unique.GroupBy(file => Digest(file)[..2], StringComparer.Ordinal))
        {
            using var bucket = WindowsProbeNative.OpenPlainDirectory(algorithm, group.Key);
            foreach (var file in group)
            {
                string digest = Digest(file);
                using var input = WindowsProbeNative.OpenPlainFile(bucket, digest, FileAccess.Read, FileShare.Read);
                WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, digest);
                if (input.Length != file.Length)
                    throw new InvalidDataException($"El objeto {digest} no coincide con el tamaño del catálogo.");
            }
        }
    }

    private static HashSet<string> FindStoredObjects(SafeFileHandle root, HashSet<string> known)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (known.Count == 0) return found;
        using var objects = WindowsProbeNative.OpenPlainDirectory(root, ObjectDirectory);
        using var algorithm = WindowsProbeNative.OpenPlainDirectory(objects, Algorithm);
        foreach (var group in known.GroupBy(digest => digest[..2], StringComparer.Ordinal))
        {
            SafeFileHandle bucket;
            try { bucket = WindowsProbeNative.OpenPlainDirectory(algorithm, group.Key); }
            catch (FileNotFoundException) { continue; }
            using (bucket)
                foreach (string digest in group)
                    if (WindowsProbeNative.PlainFileExists(bucket, digest)) found.Add(digest);
        }
        return found;
    }
}
