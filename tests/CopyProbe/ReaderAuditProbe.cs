using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

// New semantic mutations deliberately recompute checksums: a checksum is not a schema validator.
// All writes are confined to freshly registered synthetic workspaces, below a small resource budget.
internal static class ReaderAuditProbe
{
    internal static int Run()
    {
        if (new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace < 256L * 1024 * 1024)
            throw new IOException("Reader audit needs a 256 MiB free-space reserve.");
        var failures = new List<string>();
        int cases = 0;
        CheckConfiguration();
        CheckRetention();
        CheckAbandonment();
        CheckRunSequence();
        CheckLegacyRunSequence();
        foreach (string failure in failures) Console.Error.WriteLine("FAIL reader audit: " + failure);
        Console.WriteLine($"Reader audit: {cases} bounded semantic cases; failures={failures.Count}; no source/target from user configuration.");
        return failures.Count == 0 ? 0 : 1;

        void Reject(string label, string root, Action operation)
        {
            cases++;
            var before = Snapshot(root);
            try { operation(); failures.Add(label + " accepted an invalid document"); }
            catch (Exception error) when (VersionJob.Expected(error)) { }
            catch (Exception error) { failures.Add(label + " escaped as " + error.GetType().Name); }
            if (!Snapshot(root).SequenceEqual(before, StringComparer.Ordinal))
                failures.Add(label + " changed fixture bytes or paths during a rejected read");
        }

        void CheckConfiguration()
        {
            using var fixture = new Fixture();
            string source = fixture.Folder("source"), parent = fixture.Folder("backups"), settings = fixture.Folder("settings");
            string output = Path.Combine(fixture.Folder("exports"), "job.foldertwin.json");
            using var registry = new ProductJobRegistry(settings);
            var job = registry.Create(new("Reader audit", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, parent));
            registry.ExportConfiguration(job.Id, output);
            byte[] original = File.ReadAllBytes(output);
            _ = ProductJobRegistry.ReadConfiguration(output);
            foreach (var (label, mutate) in new (string, Action<JsonObject>)[]
            {
                ("config-null-alias", node => node["Job"]!["SourceAliases"]!.AsArray().Add((JsonNode?)null)),
                ("config-duplicate-alias", node => node["Job"]!["SourceAliases"]!.AsArray().Add(node["Job"]!["SourceAliases"]![0]!.GetValue<string>())),
                ("config-null-options", node => node["Job"]!["Options"] = null),
                ("config-unknown-member", node => node["Job"]!["Unexpected"] = true),
                ("config-unknown-version", node => node["Job"]!["Version"] = 99),
                ("config-unknown-mode", node => node["Job"]!["Options"]!["Mode"] = 99),
                ("config-source-traversal", node => node["Job"]!["Source"]!["VolumePath"] = @"..\outside")
            })
            {
                var node = JsonNode.Parse(original)!.AsObject();
                mutate(node);
                node["Checksum"] = Convert.ToBase64String(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(node["Job"])));
                byte[] candidate = JsonSerializer.SerializeToUtf8Bytes(node);
                File.WriteAllBytes(output, candidate);
                Reject(label, fixture.Root.Root, () => ProductJobRegistry.ReadConfiguration(output));
            }
            File.WriteAllBytes(output, original);
            _ = ProductJobRegistry.ReadConfiguration(output);
        }

        void CheckRetention()
        {
            using var fixture = new Fixture();
            string source = fixture.Folder("source"), target = fixture.Folder("store");
            var store = VersionStore.Initialize(fixture.Root, target);
            foreach (int days in new[] { -3, 0 })
            {
                File.WriteAllText(Path.Combine(source, "data.txt"), days.ToString(CultureInfo.InvariantCulture));
                store.CreatePoint(source, DateTimeOffset.UtcNow.AddDays(days).ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            }
            var review = store.PreviewRetention(new(1, 0, 0, 1));
            string id = Guid.NewGuid().ToString("N"), path = Path.Combine(fixture.Folder("journal"), "plan");
            byte[] original = RetentionJournal.Encode(new(1, store.Binding.Marker, store.Binding.Identity, id, review));
            File.WriteAllBytes(path, original);
            using var directory = new DirectoryFence(Path.GetDirectoryName(path)!);
            _ = RetentionJournal.Read(directory.Root, store.Binding, id);
            foreach (var (label, mutate) in new (string, Action<JsonObject>)[]
            {
                ("retention-null-keep-element", node => node["review"]!["keep"] = new JsonArray { null }),
                ("retention-null-expire-element", node => node["review"]!["expire"] = new JsonArray { null }),
                ("retention-null-object-element", node => node["review"]!["objects"] = new JsonArray { null }),
                ("retention-null-policy", node => node["review"]!["policy"] = null),
                ("retention-duplicate-keep", node => node["review"]!["keep"]!.AsArray().Add(review.Keep[0])),
                ("retention-overlapping-generations", node => node["review"]!["expire"]!.AsArray().Add(review.Keep[0])),
                ("retention-zero-grace", node => node["review"]!["policy"]!["graceDays"] = 0),
                ("retention-negative-bytes", node => node["review"]!["quarantineBytes"] = -1),
                ("retention-unknown-version", node => node["version"] = 99),
                ("retention-unknown-member", node => node["extra"] = true)
            })
            {
                byte[] candidate = MutateRecord(original, mutate);
                File.WriteAllBytes(path, candidate);
                Reject(label, fixture.Root.Root, () => RetentionJournal.Read(directory.Root, store.Binding, id));
            }
            File.WriteAllBytes(path, original);
            _ = RetentionJournal.Read(directory.Root, store.Binding, id);
        }

        void CheckAbandonment()
        {
            using var fixture = new Fixture();
            string source = fixture.Folder("source"), parent = fixture.Folder("backups"), settings = fixture.Folder("settings");
            File.WriteAllText(Path.Combine(source, "data.txt"), "baseline");
            using var registry = new ProductJobRegistry(settings);
            var job = registry.Create(new("Reader audit pending", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, parent));
            if (ProductRunner.Run(registry, job.Id).State != ProductRunState.Confirmed)
                throw new InvalidDataException("Reader audit could not prepare its baseline.");
            File.WriteAllText(Path.Combine(source, "data.txt"), "pending");
            File.WriteAllText(Path.Combine(source, "extra.txt"), "another pending file");
            var partial = ProductRunner.Run(registry, job.Id, checkpoint: work =>
            { if (work.Phase == WorkPhase.Copying && work.Processed == 1) throw new OperationCanceledException(); });
            if (partial.State != ProductRunState.Cancelled || partial.Generation is not { } generation)
                throw new InvalidDataException("Reader audit did not produce its own partial generation.");
            var store = VersionStore.OpenProduct(job);
            bool reached = false;
            try
            {
                store.BeginAbandonVersion(store.PreviewAbandonVersion(generation), step =>
                { if (step == AbandonStep.Planned) { reached = true; throw new IOException("Synthetic published-plan boundary."); } });
            }
            catch (IOException) when (reached) { }
            if (!reached) throw new InvalidDataException("Reader audit did not publish its abandonment plan.");
            string path = Path.Combine(job.Destination.LastVolumePath, "abandoned", generation, "plan");
            byte[] original = File.ReadAllBytes(path);
            foreach (var (label, mutate) in new (string, Action<JsonObject>)[]
            {
                ("abandon-null-artifact-element", node => node["Review"]!["Artifacts"] = new JsonArray { null }),
                ("abandon-null-review", node => node["Review"] = null),
                ("abandon-unknown-version", node => node["Version"] = 99),
                ("abandon-negative-file-count", node => node["Review"]!["Files"] = -1),
                ("abandon-unknown-member", node => node["Unexpected"] = true)
            })
            {
                File.WriteAllBytes(path, MutateRecord(original, mutate));
                Reject(label, fixture.Root.Root, () => store.ResumeAbandonVersion(generation));
            }
            File.WriteAllBytes(path, original);
            store.ResumeAbandonVersion(generation);
            if (store.RegisteredGenerations().Abandoned.Count != 1)
                throw new InvalidDataException("A valid restored plan did not resume after the negative cases.");
        }

        void CheckRunSequence()
        {
            using var fixture = new Fixture();
            string source = fixture.Folder("source"), parent = fixture.Folder("backups"), settings = fixture.Folder("settings");
            File.WriteAllText(Path.Combine(source, "data.txt"), "sequence baseline");
            using var registry = new ProductJobRegistry(settings);
            var job = registry.Create(new("Reader audit sequence", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, parent));
            var baseline = ProductRunner.Run(registry, job.Id);
            if (baseline.State != ProductRunState.Confirmed || baseline.Generation is null)
                throw new InvalidDataException("Reader audit could not create its sequence baseline.");
            Directory.CreateDirectory(Path.Combine(settings, job.Id.ToString("N"), "runs",
                long.MaxValue.ToString("D20", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")));
            if (registry.RunHistory(job.Id).Unreadable != 1)
                throw new InvalidDataException("The malformed run directory did not reserve an unreadable sequence.");
            Reject("run-exhausted-directory-sequence", fixture.Root.Root, () =>
            {
                using var attempted = registry.StartRun(job.Id);
            });
            if (VersionStore.OpenProduct(job).Verify(baseline.Generation))
                throw new InvalidDataException("An exhausted run sequence damaged its prior point.");
        }

        void CheckLegacyRunSequence()
        {
            using var fixture = new Fixture();
            string source = fixture.Folder("source"), target = fixture.Folder("store");
            File.WriteAllText(Path.Combine(source, "data.txt"), "legacy sequence baseline");
            var store = VersionStore.Initialize(fixture.Root, target);
            Guid id = VersionJob.Create(fixture.Root, source, target);
            var baseline = VersionJob.Run(fixture.Root, id);
            if (baseline.State != VersionJobState.Confirmed || baseline.Generation is null || baseline.HistoryProblem)
                throw new InvalidDataException("Reader audit could not create its legacy sequence baseline.");
            string result = Directory.GetFiles(fixture.Root.Root, $".foldertwin-job-{id:N}-*.result").Single();
            var exhausted = baseline with { Sequence = long.MaxValue };
            File.WriteAllBytes(result, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Payload = exhausted,
                Checksum = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(exhausted))
            }));
            var history = VersionJob.History(fixture.Root, id);
            if (history.Unreadable != 0 || history.Results.Single().Sequence != long.MaxValue)
                throw new InvalidDataException("The legacy sequence record was not a valid checksummed result.");
            Reject("legacy-run-exhausted-result-sequence", fixture.Root.Root, () => VersionJob.Run(fixture.Root, id));
            if (store.Verify(baseline.Generation))
                throw new InvalidDataException("An exhausted legacy sequence damaged its prior point.");
        }
    }

    private static byte[] MutateRecord(byte[] original, Action<JsonObject> mutation)
    {
        var node = JsonNode.Parse(original.AsSpan(32))!.AsObject();
        mutation(node);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(node);
        return [.. SHA256.HashData(body), .. body];
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))
        .Concat(Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path) + "/"))
        .Order(StringComparer.Ordinal).ToArray();

    private sealed class Fixture : IDisposable
    {
        internal LabWorkspace Root { get; } = LabWorkspace.Create();
        internal string Folder(string name) => Directory.CreateDirectory(Path.Combine(Root.Root, name)).FullName;
        public void Dispose()
        {
            string path = Path.GetFullPath(Root.Root);
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
                !Path.GetFileName(path).StartsWith(LabWorkspace.Prefix, StringComparison.Ordinal))
                throw new InvalidDataException("Refusing cleanup outside the owned reader-audit workspace.");
            using (Root.OpenRegisteredRoot()) { }
            Directory.Delete(path, recursive: true);
        }
    }
}
