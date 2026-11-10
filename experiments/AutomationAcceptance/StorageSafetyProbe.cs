using System.Globalization;
using System.Security.Cryptography;

internal static class StorageSafetyProbe
{
    private const int DiskFull = unchecked((int)0x80070070);
    private const string FileName = "payload.bin";

    internal static void Run()
    {
        var fixture = LabWorkspace.Create();
        bool passed = false;
        try
        {
            using var root = fixture.OpenRegisteredRoot();
            LabWorkspace.RequireSpace(256 * 1024 * 1024, LabWorkspace.AvailableSpace(root.Root));
            using var registry = new ProductJobRegistry(Folder("settings"));
            string destinations = Folder("backups");
            var jobs = Enum.GetValues<ProductBackupMode>().Select(mode =>
            {
                string source = Folder("source-" + mode);
                byte[] payload = Payload((int)mode);
                File.WriteAllBytes(Path.Combine(source, FileName), payload);
                var job = registry.Create(new($"Synthetic capacity {mode}", mode, null), LocalJobRoots.ReviewDedicatedDestination(source, destinations));
                registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching,
                    authorizeMirror: mode == ProductBackupMode.ProtectedMirror);
                var initial = ProductRunner.Run(registry, job.Id, automatic: true);
                Check(initial is { State: ProductRunState.Confirmed, Generation: not null }, "Initial synthetic backup failed.");
                return (Job: job, Source: source, Hash: SHA256.HashData(payload));
            }).ToArray();

            for (int i = 0; i < jobs.Length; i++)
            {
                var (job, source, hash) = jobs[i];
                var initial = registry.RunHistory(job.Id).Runs.First(run => run.Result is { State: ProductRunState.Confirmed, Scoped: false }).Result!;
                byte[] next = Payload(10 + i);
                File.WriteAllBytes(Path.Combine(source, FileName), next);
                var store = VersionStore.OpenProduct(job);
                bool estimateChecked = false, beganWriting = false;
                var rejected = ProductRunner.Run(registry, job.Id, automatic: true,
                    measureAvailable: () => { estimateChecked = true; return LabWorkspace.ReserveBytes; },
                    checkpoint: work => beganWriting |= work.Phase is WorkPhase.Copying or WorkPhase.Publishing);
                Check(rejected is { State: ProductRunState.Review, ErrorCode: DiskFull } && estimateChecked && !beganWriting &&
                    (rejected.Generation is null || !store.ListPoints().Any(point => point.Id == rejected.Generation)),
                    "The actual preflight did not reject the work before copying.");
                Console.WriteLine($"capacity mode={job.Options.Mode} phase=preflight code={rejected.ErrorCode} detail={rejected.Detail}");
                CheckOffline(job, source, initial.Generation!, hash, "preflight");

                var interrupted = ProductRunner.Run(registry, job.Id, automatic: true, checkpoint: work =>
                {
                    if (work.Phase == WorkPhase.Publishing) throw new IOException("Synthetic disk full during publication.", DiskFull);
                });
                Check(interrupted is { State: ProductRunState.Review, ErrorCode: DiskFull, Generation: not null }, "The synthetic publication failure was not recorded.");
                var history = registry.RunHistory(job.Id);
                var complete = history.Runs.First(run => run.Result is { State: ProductRunState.Confirmed, Scoped: false }).Result!;
                Check(complete.Generation == initial.Generation && complete.FinishedUtc == initial.FinishedUtc,
                    "A failed attempt changed the last complete backup.");
                Check(!store.ListPoints().Any(point => point.Id == interrupted.Generation), "The interrupted generation became visible as confirmed.");
                Console.WriteLine($"capacity mode={job.Options.Mode} phase=publication code={interrupted.ErrorCode} detail={interrupted.Detail}");
                CheckOffline(job, source, initial.Generation!, hash, "publication");

                var other = jobs[(i + 1) % jobs.Length];
                byte[] otherPayload = Payload(20 + i);
                File.WriteAllBytes(Path.Combine(other.Source, "independent.bin"), otherPayload);
                var independent = ProductRunner.Run(registry, other.Job.Id, automatic: true);
                Check(independent.State == ProductRunState.Confirmed && !VersionStore.OpenProduct(other.Job).Verify(independent.Generation!),
                    "An independent job did not confirm while the full job awaited retry.");
                Console.WriteLine($"independent failed={job.Options.Mode} continued={other.Job.Options.Mode}");

                var resumed = ProductRunner.Run(registry, job.Id, automatic: true);
                Check(resumed.State == ProductRunState.Confirmed && resumed.Generation == interrupted.Generation &&
                    registry.RunHistory(job.Id).Runs.Any(run => run.Result is { ErrorCode: DiskFull }),
                    "Returning capacity did not resume the same generation and retain the error.");
                CheckOffline(job, source, resumed.Generation!, SHA256.HashData(next), "resumed");
            }

            string retentionSource = Folder("retention-source");
            var retentionJob = registry.Create(new("Synthetic retention footprint", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(retentionSource, destinations));
            var retainedStore = VersionStore.OpenProduct(retentionJob, initialize: true);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            int[] ages = [6, 1, 0];
            var ids = new List<string>();
            File.WriteAllBytes(Path.Combine(retentionSource, "shared.bin"), Payload(80));
            foreach (int age in ages)
            {
                File.WriteAllBytes(Path.Combine(retentionSource, FileName), Payload(age + 80 + 1));
                string id = now.AddDays(-age).ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                Check(retainedStore.CreatePoint(retentionJob.Source.VolumePath, id), "Synthetic retention point did not publish.");
                ids.Add(id);
            }
            Directory.Move(retentionSource, retentionSource + "-apart");
            RetentionPreview Preview() => retainedStore.PreviewRetention(new(2, 0, 0, 1), clock: now);
            var preview = Preview();
            Check(preview.Expire.SequenceEqual([ids[0]]) && preview.Keep.Length == 2 && preview.Objects.Length == 1,
                "Retention did not isolate the expired generation and its exclusive object.");
            RetentionSize("before");
            string undo = retainedStore.BeginRetention(preview);
            RetentionSize("quarantined");
            try { retainedStore.PurgeRetention(undo, clock: now.AddHours(1)); throw new Exception("Premature purge was accepted."); }
            catch (InvalidDataException) { }
            retainedStore.UndoRetention(undo);
            RetentionSize("undone");
            VerifyRetained(ids[0], ages[0], "undone");
            string purge = retainedStore.BeginRetention(Preview());
            var quarantined = RetentionSize("ready-to-purge");
            CheckPurgeRejectsBadReplica(purge, preview.Objects[0], ids[^1]);
            retainedStore.PurgeRetention(purge, clock: now.AddDays(2));
            var purged = RetentionSize("purged");
            Check(quarantined.Allocated - purged.Allocated > 0 && quarantined.Logical - purged.Logical > 0,
                "Measured purge did not release file allocation.");
            Console.WriteLine($"purge released_logical={quarantined.Logical - purged.Logical} released_allocated={quarantined.Allocated - purged.Allocated}");
            for (int i = 1; i < ids.Count; i++) VerifyRetained(ids[i], ages[i], $"purged-{i}");
            passed = true;

            void CheckOffline(ProductJob job, string source, string generation, byte[] expected, string phase)
            {
                string away = source + "-apart";
                Directory.Move(source, away);
                try
                {
                    string output = Folder($"restore-{job.Options.Mode}-{phase}");
                    VersionStore.OpenProduct(job).Restore(generation, output, FileName);
                    MatchFile(output, expected, $"{job.Options.Mode}-{phase}");
                }
                finally { Directory.Move(away, source); }
            }

            void VerifyRetained(string id, int age, string phase)
            {
                Check(!retainedStore.Verify(id), "A retained point lost a replica.");
                string output = Folder("retention-restore-" + phase);
                retainedStore.Restore(id, output, FileName);
                MatchFile(output, SHA256.HashData(Payload(age + 80 + 1)), phase);
            }

            (long Logical, long Allocated) RetentionSize(string phase)
            {
                var footprint = StorageGrowthProbe.ReadFootprint(retentionJob.Destination.LastVolumePath);
                var total = (Logical: footprint.Values.Sum(value => value.Logical), Allocated: footprint.Values.Sum(value => value.Allocated));
                Console.WriteLine($"retention phase={phase} total_logical={total.Logical} total_allocated={total.Allocated} " +
                    $"quarantine_logical={footprint["quarantine"].Logical} quarantine_allocated={footprint["quarantine"].Allocated}");
                return total;
            }

            void CheckPurgeRejectsBadReplica(string operation, string digest, string retained)
            {
                string destination = retentionJob.Destination.LastVolumePath;
                string replica = Path.Combine(destination, "manifests_backup", retained + ".jsonl");
                string quarantinedObject = Path.Combine(destination, "quarantine", operation, digest);
                byte[] original = File.ReadAllBytes(replica);
                byte[] objectHash = SHA256.HashData(File.ReadAllBytes(quarantinedObject));
                CatalogSnapshot catalog;
                using (var input = File.OpenRead(replica)) catalog = BatchCatalog.Parse(input, retained);
                var differentFiles = catalog.Files.ToArray();
                differentFiles[0] = differentFiles[0] with { Hash = SHA256.HashData("different synthetic catalog"u8) };
                var different = new BatchPlan(fixture.Root, retentionJob.Source.VolumePath, destination, retentionJob.Source.Identity,
                    retentionJob.Destination, catalog.Directories, differentFiles);
                try
                {
                    using (var output = File.Create(replica)) _ = BatchCatalog.WriteMetadata(output, different, retained);
                    using (var input = File.OpenRead(replica))
                        Check(!BatchCatalog.Parse(input, retained).DocumentHash.AsSpan().SequenceEqual(catalog.DocumentHash),
                            "The fixture did not contain two valid but different catalogs for the same generation.");
                    RejectPurge();
                    File.WriteAllText(replica, "synthetic damaged replica");
                    RejectPurge();
                }
                finally { File.WriteAllBytes(replica, original); }
                Console.WriteLine("PASS valid conflicting catalogs and damaged replicas both blocked purge and preserved the quarantined bytes.");

                void RejectPurge()
                {
                    try { retainedStore.PurgeRetention(operation, clock: now.AddDays(2)); throw new Exception("Unsafe purge was accepted."); }
                    catch (InvalidDataException) { }
                    Check(SHA256.HashData(File.ReadAllBytes(quarantinedObject)).AsSpan().SequenceEqual(objectHash),
                        "Rejected purge modified the quarantined bytes.");
                }
            }
        }
        finally
        {
            if (passed) StorageGrowthProbe.RemoveWorkspace(fixture, Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
            else Console.Error.WriteLine($"Synthetic fixture preserved: {fixture.Root}.");
        }

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    private static byte[] Payload(int seed)
    {
        var bytes = new byte[128 * 1024];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static void MatchFile(string root, byte[] expected, string phase)
    {
        byte[] actual = SHA256.HashData(File.ReadAllBytes(Path.Combine(root, FileName)));
        Check(actual.AsSpan().SequenceEqual(expected), $"Unexpected restored bytes for {phase}.");
        Console.WriteLine($"restore phase={phase} sha256={Convert.ToHexStringLower(actual)}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
