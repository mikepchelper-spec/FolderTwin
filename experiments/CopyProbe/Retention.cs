using System.Globalization;
using Microsoft.Win32.SafeHandles;

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
    string[] Keep, string[] Expire, string[] Objects, long QuarantineBytes);

internal sealed partial class VersionStore
{
    private sealed record RetentionInventory(RegistryState Registry, Dictionary<string, CatalogSnapshot> Catalogs,
        Dictionary<string, BatchFile> Objects, Dictionary<string, string> Sources);

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
        var keep = active.GroupBy(id => inventory.Sources[id], StringComparer.Ordinal)
            .SelectMany(group => policy.Select(group.ToArray(), now)).ToHashSet(StringComparer.Ordinal);
        if (inventory.Registry.MirrorGeneration is { } current)
            keep.Add(current);
        string[] expire = active.Where(id => !keep.Contains(id)).ToArray();
        var referenced = new HashSet<string>(keep.SelectMany(id => inventory.Catalogs[id].Files.Select(Digest)), StringComparer.Ordinal);
        string[] candidates = expire.SelectMany(id => inventory.Catalogs[id].Files.Select(Digest))
            .Distinct(StringComparer.Ordinal).Where(digest => !referenced.Contains(digest)).Order(StringComparer.Ordinal).ToArray();
        return new(policy, now, inventory.Registry.LastHash, active.Where(keep.Contains).ToArray(), expire,
            candidates, candidates.Sum(digest => inventory.Objects[digest].Length));
    }

    private RetentionInventory AuditRetention(SafeFileHandle root, bool verifyObjects = true)
    {
        if (!registeredGenerations)
            throw new InvalidDataException("El almacén anterior no tiene registro durable para retención.");
        RequireNoPendingMirror(root);
        var registry = GenerationRegistry.Read(root, binding);
        using var pending = WindowsProbeNative.OpenPlainDirectory(root, PendingDirectory);
        foreach (var entry in WindowsProbeNative.ReadDirectory(pending).Where(entry => entry.Name is not ("." or "..")))
        {
            string? id = entry.Name.EndsWith(LocationSuffix, StringComparison.Ordinal) ? entry.Name[..^LocationSuffix.Length] :
                entry.Name.EndsWith(".jsonl", StringComparison.Ordinal) ? entry.Name[..^6] : null;
            if (id is null || !registry.Generations.ContainsKey(id) || !IsCommitted(root, id))
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
        }).ToHashSet(StringComparer.Ordinal);
        if (!observed.SetEquals(registry.Generations.Keys))
            throw new InvalidDataException("Falta o sobra una generación esperada. La limpieza queda suspendida.");
        foreach (string name in new[] { PrimaryDirectory, ReplicaDirectory })
        {
            using var manifests = WindowsProbeNative.OpenPlainDirectory(root, name);
            var names = WindowsProbeNative.ReadDirectory(manifests).Where(entry => entry.Name is not ("." or ".."))
                .Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
            if (!names.SetEquals(registry.Generations.Keys.Select(id => id + ".jsonl")))
                throw new InvalidDataException("El conjunto de manifiestos no coincide con las generaciones esperadas.");
        }
        var catalogs = new Dictionary<string, CatalogSnapshot>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var objects = new Dictionary<string, BatchFile>(StringComparer.Ordinal);
        var activeObjects = new Dictionary<string, BatchFile>(StringComparer.Ordinal);
        foreach (var (id, hash) in registry.Generations)
        {
            var catalog = ReadPoint(root, id);
            if (catalog.Degraded || !catalog.DocumentHash.AsSpan().SequenceEqual(hash))
                throw new InvalidDataException("Un catálogo esperado falta, difiere o está dañado. La limpieza queda suspendida.");
            catalogs.Add(id, catalog);
            using (var scope = workspace.OpenRegisteredRoot())
            {
                var intent = ReadIntent(scope.Root, root, pending, id);
                if (!BatchCatalog.WriteMetadata(Stream.Null, intent, id).AsSpan().SequenceEqual(hash))
                    throw new InvalidDataException("La identidad de origen no corresponde al catálogo esperado.");
                sources.Add(id, Convert.ToHexStringLower(intent.SourceIdentity));
            }
            foreach (var file in catalog.Files)
            {
                string digest = Digest(file);
                if (objects.TryGetValue(digest, out var previous) && previous.Length != file.Length)
                    throw new InvalidDataException("Un objeto compartido tiene tamaños contradictorios.");
                objects[digest] = file;
                if (!registry.Expired.Contains(id))
                    activeObjects[digest] = file;
            }
        }
        if (verifyObjects)
            VerifyObjects(root, activeObjects.Values.ToArray());
        return new(registry, catalogs, objects, sources);
    }
}
