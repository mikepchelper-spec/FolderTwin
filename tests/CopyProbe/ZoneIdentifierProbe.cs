using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ZoneIdentifierProbe
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("Synthetic shared file bytes for Zone.Identifier.");
    private static readonly byte[] Internet = Encoding.UTF8.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://synthetic.invalid/first\r\n");
    private static readonly byte[] Restricted = Encoding.UTF8.GetBytes("[ZoneTransfer]\nZoneId=4\nReferrerUrl=https://synthetic.invalid/second\n");

    internal static int Run()
    {
        foreach (var mode in Enum.GetValues<ProductBackupMode>()) CheckMode(mode);
        Console.WriteLine("PASS Zone.Identifier in all modes: per-path marks with shared CAS, empty/absent distinction, metadata-only changes, interrupted offline publication, independent restore, quarantine/undo/purge, bounds and other-stream rejection.");
        return 0;
    }

    private static void CheckMode(ProductBackupMode mode)
    {
        var fixture = LabWorkspace.Create();
        bool passed = false;
        try
        {
            string source = Folder("source"), targets = Folder("backups");
            Directory.CreateDirectory(Path.Combine(source, "Documents"));
            Directory.CreateDirectory(Path.Combine(source, "empty-folder"));
            var marks = new Dictionary<string, byte[]?>(StringComparer.Ordinal)
            {
                [Path.Combine("Documents", "internet.txt")] = Internet,
                [Path.Combine("Documents", "restricted.txt")] = Restricted,
                ["empty-mark.txt"] = [],
                ["unmarked.txt"] = null
            };
            for (int index = 0; index < 16; index++) marks.Add($"ordinary-{index:D2}.txt", null);
            foreach (var (path, zone) in marks)
            {
                File.WriteAllBytes(Path.Combine(source, path), Body);
                if (zone is not null) File.WriteAllBytes(Path.Combine(source, path) + ":Zone.Identifier", zone);
            }
            using var registry = new ProductJobRegistry(Folder("settings"));
            var job = registry.Create(new("Synthetic marks " + mode, mode, null), LocalJobRoots.ReviewDedicatedDestination(source, targets));
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching,
                authorizeMirror: mode == ProductBackupMode.ProtectedMirror);
            var first = ProductRunner.Run(registry, job.Id, automatic: true);
            Check(first is { State: ProductRunState.Confirmed, Generation: not null }, first.Detail ?? "Initial marked backup failed.");
            var store = VersionStore.OpenProduct(job);
            var original = marks.ToDictionary(pair => pair.Key, pair => pair.Value);
            CheckPoint(first.Generation!, original, "first");
            Reject(() => VersionStore.ExportConfirmedPoint(registry, job.Id, first.Generation!, Folder("legacy-export-rejected"), layout: 0));
            if (mode != ProductBackupMode.Versions) Compare(store.MirrorCurrentPath, original);
            string digest = Convert.ToHexStringLower(SHA256.HashData(Body));
            string objectPath = Path.Combine(store.Destination, "objects", "sha256", digest[..2], digest);
            Check(Directory.EnumerateFiles(Path.Combine(store.Destination, "objects", "sha256"), "*", SearchOption.AllDirectories)
                .Count(path => Path.GetFileName(path).Length == 64) == 1 && ReadMark(objectPath) is null,
                "Per-path marks duplicated or contaminated the shared content object.");
            CheckCatalogValidation(store, first.Generation!);

            string changedPath = Path.Combine("Documents", "internet.txt");
            var full = Path.Combine(source, changedPath);
            DateTime unchangedTime = File.GetLastWriteTimeUtc(full);
            SetMark(full, Restricted, unchangedTime);
            marks[changedPath] = Restricted;
            var next = ProductRunner.Run(registry, job.Id, automatic: true);
            Check(next is { State: ProductRunState.Confirmed, Generation: not null } && next.Generation != first.Generation,
                next.Detail ?? "An ADS-only change did not create a point.");
            CheckPoint(next.Generation!, marks, "mark-only");
            CheckPoint(first.Generation!, original, "older-mark");

            SetMark(full, null, unchangedTime);
            marks[changedPath] = null;
            string pending;
            bool reached = false;
            if (mode == ProductBackupMode.Versions)
            {
                pending = VersionStore.NewGenerationId();
                Reject(() => store.CreatePoint(job.Source.VolumePath, pending, checkpoint: step =>
                { if (step == CatalogStep.BackupPublished) { reached = true; throw new IOException("Synthetic marked catalog boundary."); } }));
            }
            else
            {
                var review = store.PreviewMirror(job.Source.VolumePath);
                pending = review.Generation;
                Reject(() => store.BeginMirror(review, massReviewed: true, checkpoint: step =>
                { if (step == MirrorStep.Retired) { reached = true; throw new IOException("Synthetic marked mirror boundary."); } }));
            }
            Check(reached, "The marked point did not reach its interruption boundary.");
            string absent = Path.Combine(fixture.Root, "source-aside");
            Directory.Move(source, absent);
            if (mode == ProductBackupMode.Versions) store.ResumePoint(pending, expectedSourceIdentity: job.Source.Identity);
            else store.ResumeMirror(pending);
            CheckPoint(pending, marks, "resumed-offline");
            if (mode != ProductBackupMode.Versions) Compare(store.MirrorCurrentPath, marks);
            Directory.Move(absent, source);

            var beforeFailure = store.ListPoints().Select(point => point.Id).ToArray();
            SetMark(full, new byte[ZoneIdentifierStream.MaximumBytes + 1], unchangedTime);
            var oversized = ProductRunner.Run(registry, job.Id, automatic: true);
            Check(oversized.State != ProductRunState.Confirmed && store.ListPoints().Select(point => point.Id).SequenceEqual(beforeFailure),
                "An oversized mark confirmed an incomplete point.");
            SetMark(full, null, unchangedTime);
            File.WriteAllBytes(full + ":unsupported", [1]);
            var unsupported = ProductRunner.Run(registry, job.Id, automatic: true);
            Check(unsupported.State != ProductRunState.Confirmed && store.ListPoints().Select(point => point.Id).SequenceEqual(beforeFailure),
                "Another named stream was silently dropped.");
            File.Delete(full + ":unsupported");

            File.WriteAllBytes(objectPath + ":Zone.Identifier", Internet);
            Reject(() => store.Verify(pending));
            File.Delete(objectPath + ":Zone.Identifier");
            Check(!store.Verify(pending), "The shared content control did not recover after removing synthetic corruption.");

            Directory.Move(source, absent);
            var clock = DateTimeOffset.UtcNow.AddDays(2);
            var policy = new RetentionPolicy(1, 0, 0, 1);
            var cleanup = store.PreviewRetention(policy, clock);
            Check(cleanup.Expire.Length == 2 && cleanup.Objects.Length == 0, "Shared metadata-only history changed CAS reference accounting.");
            string operation = store.BeginRetention(cleanup, clock: clock);
            store.UndoRetention(operation);
            CheckPoint(first.Generation!, original, "after-undo");
            operation = store.BeginRetention(store.PreviewRetention(policy, clock), clock: clock);
            store.PurgeRetention(operation, clock: clock.AddDays(2));
            Check(store.ListPoints().Select(point => point.Id).SequenceEqual([pending]), "Metadata purge retained expired marked dates.");
            CheckPoint(pending, marks, "after-purge");
            Compare(absent, marks);
            passed = true;
            Console.WriteLine($"PASS marked local mode={mode}, points=3, shared_objects=1, SHA256={digest}, offline resume/restore and retention.");

            void CheckPoint(string id, Dictionary<string, byte[]?> expected, string label)
            {
                Check(!store.Verify(id), "Marked point verification degraded a catalog.");
                string output = Folder("restore-" + label);
                store.Restore(id, output);
                Compare(output, expected);
            }
        }
        finally
        {
            if (passed)
            {
                using (fixture.OpenRegisteredRoot()) { }
                if (Path.GetDirectoryName(fixture.Root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
                    throw new IOException("Synthetic mark cleanup left its temporary parent.");
                Directory.Delete(fixture.Root, recursive: true);
            }
            else Console.Error.WriteLine("Synthetic mark fixture preserved: " + fixture.Root);
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    private static void CheckCatalogValidation(VersionStore store, string generation)
    {
        string primary = Path.Combine(store.Destination, "manifests", generation + ".jsonl");
        byte[] original = File.ReadAllBytes(primary);
        foreach (var mutate in new Action<JsonObject>[]
        {
            node => node["zoneData"] = "not-base64",
            node => node["zoneHash"] = new string('0', 64),
            node => node["zoneHash"] = null,
            node => node["zoneData"] = null,
            node => node["zoneData"] = Convert.ToBase64String(new byte[ZoneIdentifierStream.MaximumBytes + 1]),
            node => node["extraMetadata"] = true
        })
        {
            var lines = Encoding.UTF8.GetString(original).TrimEnd('\n').Split('\n').Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
            mutate(lines.First(node => (string?)node["kind"] == "file" && node["zoneData"] is not null));
            using var body = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var changed = new MemoryStream();
            foreach (var node in lines[..^1])
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(node);
                body.AppendData(bytes); body.AppendData([10]);
                changed.Write(bytes); changed.WriteByte(10);
            }
            lines[^1]["hash"] = Convert.ToBase64String(body.GetHashAndReset());
            changed.Write(JsonSerializer.SerializeToUtf8Bytes(lines[^1])); changed.WriteByte(10);
            Reject(() => BatchCatalog.Parse(new MemoryStream(changed.ToArray()), generation));
        }
    }

    private static void SetMark(string path, byte[]? mark, DateTime stamp)
    {
        if (mark is null) File.Delete(path + ":Zone.Identifier");
        else File.WriteAllBytes(path + ":Zone.Identifier", mark);
        File.SetLastWriteTimeUtc(path, stamp);
    }

    private static byte[]? ReadMark(string path)
    {
        try { return File.ReadAllBytes(path + ":Zone.Identifier"); }
        catch (FileNotFoundException) { return null; }
    }

    private static void Compare(string output, Dictionary<string, byte[]?> expected)
    {
        foreach (var (path, mark) in expected)
        {
            string file = Path.Combine(output, path);
            Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(Body), "Independent primary bytes differ.");
            byte[]? actual = ReadMark(file);
            Check(mark is null ? actual is null : actual is not null && actual.AsSpan().SequenceEqual(mark),
                "Independent Zone.Identifier bytes differ, including empty/absent state.");
        }
        Check(Directory.Exists(Path.Combine(output, "empty-folder")), "Recovery lost an empty folder.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidDataException("An unsafe marked-data operation was accepted.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
