using System.Globalization;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class RetentionProbe
{
    internal static int Run()
    {
        CheckPolicy();
        foreach (var boundary in new[] { RetentionStep.Planned, RetentionStep.Expired, RetentionStep.Moved, RetentionStep.Quarantined })
        {
            using var fixture = new Fixture();
            var review = fixture.Store.PreviewRetention(fixture.Policy);
            Check(review.Expire.Length == 2 && review.Keep.SequenceEqual([fixture.Latest]) && review.Objects.Length == 2,
                "The reference union did not preserve the shared object.");
            bool reached = false;
            Reject(() => fixture.Store.BeginRetention(review, step =>
            {
                if (step == boundary) { reached = true; throw new IOException("Injected quarantine interruption."); }
            }));
            Check(reached, "A quarantine checkpoint was not reached.");
            string operation = fixture.Store.ListRetention().Single().Id;
            fixture.Store.ResumeRetention(operation);
            Check(fixture.Store.ListRetention().Single().State == "quarantined" && !fixture.Store.Verify(fixture.Latest),
                "The retained point failed after resuming quarantine.");
            Reject(() => fixture.Store.PreviewRetention(fixture.Policy));
            fixture.Store.UndoRetention(operation);
            Check(fixture.Store.ListRetention().Single().State == "undone" &&
                fixture.Points.All(point => !fixture.Store.Verify(point)) && fixture.Store.RegisteredGenerations().Expired.Count == 0,
                "Undo did not restore every expired generation and shared object.");
        }
        Console.WriteLine("PASS quarantine resumes after every transition and preserves shared objects; undo restores expired points.");

        foreach (var boundary in new[] { RetentionStep.Undoing, RetentionStep.Returned, RetentionStep.Retained, RetentionStep.Undone })
        {
            using var fixture = new Fixture();
            string operation = fixture.Store.BeginRetention(fixture.Store.PreviewRetention(fixture.Policy));
            bool reached = false;
            Reject(() => fixture.Store.UndoRetention(operation, step =>
            {
                if (step == boundary) { reached = true; throw new IOException("Injected undo interruption."); }
            }));
            Check(reached, "An undo checkpoint was not reached.");
            fixture.Store.UndoRetention(operation);
            Check(fixture.Points.All(point => !fixture.Store.Verify(point)), "Interrupted undo lost a generation.");
        }
        Console.WriteLine("PASS undo resumes before/after moves and retention registration.");

        foreach (var boundary in new[] { RetentionStep.Purging, RetentionStep.Deleted, RetentionStep.Purged,
            RetentionStep.MetadataPlanPartial, RetentionStep.MetadataPlanned, RetentionStep.MetadataRegistered,
            RetentionStep.MetadataDeleting, RetentionStep.MetadataDeleted, RetentionStep.MetadataForgotten, RetentionStep.MetadataCompleted })
        {
            using var fixture = new Fixture();
            string operation = fixture.Store.BeginRetention(fixture.Store.PreviewRetention(fixture.Policy));
            Reject(() => fixture.Store.PurgeRetention(operation));
            bool reached = false;
            DateTimeOffset later = DateTimeOffset.UtcNow.AddDays(2);
            Reject(() => fixture.Store.PurgeRetention(operation, step =>
            {
                if (step == boundary) { reached = true; throw new IOException("Injected purge interruption."); }
            }, later));
            Check(reached, "A purge checkpoint was not reached.");
            fixture.Store.PurgeRetention(operation, clock: later);
            Check(fixture.Store.ListRetention().Single().State == "purged" && !fixture.Store.Verify(fixture.Latest),
                "Purge failed or damaged a retained point.");
            Check(fixture.Store.RegisteredGenerations().Generations.Keys.SequenceEqual([fixture.Latest]) &&
                fixture.Store.RegisteredGenerations().PurgeProofs.Count == 1 &&
                fixture.Store.ListPoints().Select(point => point.Id).SequenceEqual([fixture.Latest]),
                "Purged metadata kept retired generations live or lost their compact proof.");
            foreach (string point in fixture.Points[..^1])
                foreach (string area in new[] { "manifests", "manifests_backup", "pending" })
                    Check(!Directory.EnumerateFiles(Path.Combine(fixture.Destination, area), point + "*").Any(),
                        "A purged catalog or execution descriptor was retained.");
            Reject(() => fixture.Store.CreatePoint(fixture.Source, fixture.Points[0]));
            Reject(() => fixture.Store.UndoRetention(operation));
            fixture.Seed("old one");
            string next = VersionStore.NewGenerationId();
            fixture.Store.CreatePoint(fixture.Source, next);
            Check(!fixture.Store.Verify(next), "A legitimately purged object could not be copied again.");
            fixture.Store.PreviewRetention(fixture.Policy);
        }
        Console.WriteLine("PASS grace blocks immediate purge; object/metadata purge resumes at ten boundaries, removes catalogs/descriptors with compact proofs, refuses generation reuse and permits recopying purged content.");

        CheckRescueAndCoordination();
        CheckMultipleSources();
        CheckRegistryRecovery();
        CheckWorkerTermination();
        CheckMetadataRegistryRecovery();
        CheckMetadataProofs();
        CheckMetadataWorkerTermination();
        CheckMetadataResumeClock();
        CheckLegacyMetadataPurge();
        CheckFailures();
        return 0;
    }

    internal static int InterruptChild(string root)
    {
        var workspace = LabWorkspace.Open(root);
        var store = VersionStore.Open(workspace, Path.Combine(root, "store"));
        store.BeginRetention(store.PreviewRetention(new(1, 0, 0, 1)), step =>
        {
            if (step == RetentionStep.Moved)
            {
                Console.WriteLine("READY");
                Console.Out.Flush();
                Console.ReadLine();
            }
        });
        return 0;
    }

    internal static int InterruptMetadataChild(string root, string operation)
    {
        var workspace = LabWorkspace.Open(root);
        var store = VersionStore.Open(workspace, Path.Combine(root, "store"));
        store.PurgeRetention(operation, step =>
        {
            if (step != RetentionStep.MetadataDeleted) return;
            Console.WriteLine("READY");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
        }, DateTimeOffset.UtcNow.AddDays(2));
        return 1;
    }

    private static void CheckMetadataRegistryRecovery()
    {
        foreach (bool forget in new[] { false, true })
        foreach (var boundary in Enum.GetValues<RegistryStep>())
        {
            using var fixture = new Fixture();
            string operation = fixture.Store.BeginRetention(fixture.Store.PreviewRetention(fixture.Policy));
            var later = DateTimeOffset.UtcNow.AddDays(2);
            if (forget)
                Reject(() => fixture.Store.PurgeRetention(operation, step =>
                { if (step == RetentionStep.MetadataDeleting) throw new IOException("Synthetic metadata deletion boundary."); }, later));
            bool reached = false;
            Reject(() => fixture.Store.PurgeRetention(operation, clock: later, metadataRegistryCheckpoint: step =>
            {
                if (step != boundary) return;
                reached = true;
                throw new IOException("Synthetic split metadata registry event.");
            }));
            Check(reached, "The metadata registry event did not reach its boundary.");
            Check(!fixture.Store.Verify(fixture.Latest), "Split metadata proof damaged the retained generation.");
            Reject(() => fixture.Store.CreatePoint(fixture.Source, VersionStore.NewGenerationId()));
            fixture.Store.PurgeRetention(operation, clock: later);
            Check(fixture.Store.ListRetention().Single().MetadataCleaned && fixture.Store.RegisteredGenerations().Generations.Count == 1,
                "A split metadata expectation could not resume from its exact proof.");
        }
        Console.WriteLine("PASS metadata proof and forgetting resume all four split-registry boundaries while blocking new writers.");
    }

    private static void CheckMetadataProofs()
    {
        using var fixture = new Fixture();
        string operation = fixture.Store.BeginRetention(fixture.Store.PreviewRetention(fixture.Policy));
        fixture.Store.PurgeRetention(operation, clock: DateTimeOffset.UtcNow.AddDays(2));
        string receipt = Path.Combine(fixture.Destination, "quarantine", operation, "metadata-plan");
        string replica = Path.Combine(fixture.Destination, "quarantine", operation, "metadata-replica");
        byte[] bytes = File.ReadAllBytes(receipt);
        byte[] registry = File.ReadAllBytes(Path.Combine(fixture.Destination, GenerationRegistry.PrimaryName));
        File.Delete(receipt);
        Reject(() => fixture.Store.PreviewRetention(fixture.Policy));
        Reject(() => fixture.Store.CreatePoint(fixture.Source, VersionStore.NewGenerationId()));
        Check(File.ReadAllBytes(Path.Combine(fixture.Destination, GenerationRegistry.PrimaryName)).AsSpan().SequenceEqual(registry) &&
            !fixture.Store.Verify(fixture.Latest), "Missing proof changed the registry or blocked independent verification.");
        File.WriteAllBytes(receipt, bytes);
        byte[] bad = (byte[])bytes.Clone(); bad[^1] ^= 1;
        File.WriteAllBytes(replica, bad);
        Reject(() => fixture.Store.PreviewRetention(fixture.Policy));
        File.WriteAllBytes(replica, bytes);
        string deletion = Path.Combine(Path.GetDirectoryName(receipt)!, "metadata-deleting");
        byte[] deletionState = File.ReadAllBytes(deletion);
        File.Delete(deletion);
        Reject(() => fixture.Store.PreviewRetention(fixture.Policy));
        Reject(() => fixture.Store.CreatePoint(fixture.Source, VersionStore.NewGenerationId()));
        File.WriteAllBytes(deletion, deletionState);
        foreach (var mutate in new Action<JsonObject>[]
        {
            node => node["Points"]!.AsArray()[0] = null,
            node => node["Points"]![0]!["Files"]!.AsArray()[0] = null,
            node => node["Points"]![0]!["Files"]![0]!["Area"] = "..\\outside",
            node => node["Points"]![0]!["Files"]!.AsArray().Add(node["Points"]![0]!["Files"]![0]!.DeepClone()),
            node => node["Unexpected"] = true
        })
        {
            var node = JsonNode.Parse(bytes.AsSpan(32))!.AsObject();
            mutate(node);
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(node);
            byte[] invalid = [.. System.Security.Cryptography.SHA256.HashData(body), .. body];
            File.WriteAllBytes(receipt, invalid);
            File.WriteAllBytes(replica, invalid);
            Reject(() => fixture.Store.PreviewRetention(fixture.Policy));
            Check(File.ReadAllBytes(Path.Combine(fixture.Destination, GenerationRegistry.PrimaryName)).AsSpan().SequenceEqual(registry),
                "A semantically invalid metadata proof changed the registry.");
        }
        File.WriteAllBytes(receipt, bytes);
        File.WriteAllBytes(replica, bytes);
        long original = RegistryCheckpointProbe.FillNearProductionLimit(fixture.Workspace, fixture.Destination, fixture.Store.Binding, fixture.Latest);
        using (var root = new DirectoryFence(fixture.Destination))
        using (var operationLock = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.ReadWrite, FileShare.None))
            GenerationRegistry.Reserve(root.Root, fixture.Store.Binding, 3);
        Check(fixture.Store.RegisteredGenerations().PurgeProofs.Count == 1 && fixture.Store.PreviewRetention(fixture.Policy).Keep.Contains(fixture.Latest),
            "Compaction lost a purged operation's expected proof.");
        string absent = Path.Combine(fixture.Workspace.Root, "source absent");
        Directory.Move(fixture.Source, absent);
        string output = Directory.CreateDirectory(Path.Combine(fixture.Workspace.Root, "recovered")).FullName;
        fixture.Store.Restore(fixture.Latest, output);
        Check(File.ReadAllBytes(Path.Combine(output, "shared.txt")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(absent, "shared.txt"))),
            "Recovery failed after metadata purge and registry compaction.");
        Console.WriteLine($"PASS missing or damaged metadata proofs stop writes/cleanup, preserve recovery, and survive compaction from {original} bytes.");
    }

    private static void CheckMetadataWorkerTermination()
    {
        using var fixture = new Fixture();
        string operation = fixture.Store.BeginRetention(fixture.Store.PreviewRetention(fixture.Policy));
        string executable = Environment.ProcessPath ?? throw new IOException("Missing metadata worker host.");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in new[] { "--interrupt-metadata-child", fixture.Workspace.Root, operation }) start.ArgumentList.Add(argument);
        using var worker = Process.Start(start) ?? throw new IOException("Could not start metadata purge worker.");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Check(worker.StandardOutput.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult() == "READY", "Metadata worker did not reach deletion.");
            worker.Kill(entireProcessTree: true);
            worker.WaitForExit();
        }
        finally { if (!worker.HasExited) { worker.Kill(entireProcessTree: true); worker.WaitForExit(); } }
        Directory.Move(fixture.Source, Path.Combine(fixture.Workspace.Root, "source absent"));
        var reopened = VersionStore.Open(LabWorkspace.Open(fixture.Workspace.Root), fixture.Destination);
        Check(reopened.ListRetention().Single().State == "purging" && !reopened.Verify(fixture.Latest), "A killed metadata purge lost its pending state.");
        reopened.PurgeRetention(operation, clock: DateTimeOffset.UtcNow.AddDays(2));
        Check(reopened.ListPoints().Length == 1 && reopened.ListRetention().Single().MetadataCleaned && !reopened.Verify(fixture.Latest),
            "Another worker could not complete metadata purge with no source.");
        Console.WriteLine("PASS a real worker terminated after removing metadata; another instance resumed offline and verified the retained point.");
    }

    private static void CheckMetadataResumeClock()
    {
        using var fixture = new Fixture();
        string operation = fixture.Store.BeginRetention(fixture.Store.PreviewRetention(fixture.Policy));
        string future = DateTimeOffset.UtcNow.AddDays(5).ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N");
        fixture.Store.CreatePoint(fixture.Source, future);
        bool reached = false;
        Reject(() => fixture.Store.PurgeRetention(operation, step =>
        { if (step == RetentionStep.MetadataPlanned) { reached = true; throw new IOException("Synthetic metadata clock boundary."); } },
            DateTimeOffset.UtcNow.AddDays(7)));
        Check(reached, "The clock fixture did not reach its metadata phase.");
        Reject(() => fixture.Store.PurgeRetention(operation, clock: DateTimeOffset.UtcNow.AddDays(3)));
        Check(!fixture.Store.Verify(future) && !fixture.Store.Verify(fixture.Latest), "A clock rollback damaged retained data.");
        fixture.Store.PurgeRetention(operation, clock: DateTimeOffset.UtcNow.AddDays(7));
        Check(fixture.Store.ListRetention().Single().MetadataCleaned, "Metadata purge did not resume after restoring the clock.");
        Console.WriteLine("PASS metadata purge rechecks future generations on resume after the grace period.");
    }

    private static void CheckLegacyMetadataPurge()
    {
        using var fixture = new Fixture();
        string operation = fixture.Store.BeginRetention(fixture.Store.PreviewRetention(fixture.Policy));
        var later = DateTimeOffset.UtcNow.AddDays(2);
        bool reached = false;
        Reject(() => fixture.Store.PurgeRetention(operation, step =>
        { if (step == RetentionStep.MetadataPlanned) { reached = true; throw new IOException("Synthetic legacy purge state."); } }, later));
        Check(reached && fixture.Store.RegisteredGenerations().PurgeProofs.Count == 0, "Legacy purge fixture contains a new proof.");
        string directory = Path.Combine(fixture.Destination, "quarantine", operation);
        // Reconstruct only the old completed-object state: catalogs and expired
        // expectations still exist, and no new-format proof has been registered.
        foreach (string name in new[] { "metadata-plan", "metadata-replica", "objects-purged" }) File.Delete(Path.Combine(directory, name));
        using (var journal = new DirectoryFence(directory))
        {
            var plan = RetentionJournal.Read(journal.Root, fixture.Store.Binding, operation);
            RetentionJournal.SetState(journal.Root, plan, "purged");
        }
        Check(fixture.Store.ListRetention().Single() is { State: "purged", MetadataCleaned: false }, "Old purges were silently migrated.");
        fixture.Store.PurgeRetention(operation, clock: later);
        Check(fixture.Store.ListRetention().Single().MetadataCleaned && fixture.Store.ListPoints().Length == 1 && !fixture.Store.Verify(fixture.Latest),
            "A legacy completed purge could not explicitly retire its old metadata.");
        Console.WriteLine("PASS an old completed object purge preserves catalogs until explicit metadata cleanup and keeps retained recovery.");
    }

    private static void CheckWorkerTermination()
    {
        using var fixture = new Fixture();
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("No executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--interrupt-retention-child");
        start.ArgumentList.Add(fixture.Workspace.Root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("No retention worker.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            string? ready = process.StandardOutput.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
            Check(ready == "READY", "The retention worker did not reach its move boundary.");
            process.Kill();
            Check(process.WaitForExit(15000), "The retention worker did not stop.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(15000); }
        }
        Directory.Move(fixture.Source, Path.Combine(fixture.Workspace.Root, "source absent"));
        var reopened = VersionStore.Open(LabWorkspace.Open(fixture.Workspace.Root), fixture.Destination);
        string operation = reopened.ListRetention().Single().Id;
        reopened.ResumeRetention(operation);
        reopened.UndoRetention(operation);
        Check(fixture.Points.All(point => !reopened.Verify(point)), "A killed GC worker lost recoverable objects.");
        Console.WriteLine("PASS another instance resumes and undoes quarantine after a real worker termination with no source.");
    }

    private static void CheckRegistryRecovery()
    {
        foreach (bool undo in new[] { false, true })
        {
            using var fixture = new Fixture();
            var preview = fixture.Store.PreviewRetention(fixture.Policy);
            if (!undo)
                Reject(() => fixture.Store.BeginRetention(preview, step =>
                {
                    if (step == RetentionStep.Planned) throw new IOException("Injected plan boundary.");
                }));
            else
                fixture.Store.BeginRetention(preview);
            string id = fixture.Store.ListRetention().Single().Id;
            if (undo)
            {
                int returned = 0;
                Reject(() => fixture.Store.UndoRetention(id, step =>
                {
                    if (step == RetentionStep.Returned && ++returned == preview.Objects.Length)
                        throw new IOException("Injected all objects returned.");
                }));
            }
            using (var root = new DirectoryFence(fixture.Destination))
            using (var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.ReadWrite, FileShare.None))
            {
                if (undo)
                {
                    using var quarantine = WindowsProbeNative.OpenPlainDirectory(root.Root, "quarantine");
                    using var directory = WindowsProbeNative.OpenPlainDirectory(quarantine, id);
                    RetentionJournal.SetState(directory, RetentionJournal.Read(directory, fixture.Store.Binding, id), "returned");
                }
                var registry = GenerationRegistry.Read(root.Root, fixture.Store.Binding);
                GenerationRegistry.SetExpired(root.Root, fixture.Store.Binding, preview.Expire[0], registry.Generations[preview.Expire[0]], expired: !undo);
                Reject(() => GenerationRegistry.SetExpired(root.Root, fixture.Store.Binding, preview.Expire[1],
                    registry.Generations[preview.Expire[1]], expired: !undo, step =>
                    {
                        if (step == RegistryStep.PrimaryPartial) throw new IOException("Injected second registry transition.");
                    }));
            }
            if (!undo)
                fixture.Store.ResumeRetention(id);
            fixture.Store.UndoRetention(id);
            Check(fixture.Points.All(point => !fixture.Store.Verify(point)), "A partial second registry event did not recover from the journal.");
        }
        Console.WriteLine("PASS the durable GC journal repairs split expiration and retention events after earlier events already committed.");
    }

    private static void CheckMultipleSources()
    {
        using var fixture = new Fixture();
        string other = Directory.CreateDirectory(Path.Combine(fixture.Workspace.Root, "another source")).FullName;
        File.WriteAllText(Path.Combine(other, "different.txt"), "another job");
        string point = VersionStore.NewGenerationId();
        fixture.Store.CreatePoint(other, point);
        var review = fixture.Store.PreviewRetention(fixture.Policy);
        Check(review.Keep.Contains(fixture.Latest) && review.Keep.Contains(point),
            "Retention mixed independent sources into the same calendar bucket.");
        string operation = fixture.Store.BeginRetention(review);
        fixture.Store.PurgeRetention(operation, clock: DateTimeOffset.UtcNow.AddDays(2));
        Check(!fixture.Store.Verify(point) && !fixture.Store.Verify(fixture.Latest), "A source lost its newest point.");
        Console.WriteLine("PASS independent source identities retain independent calendars and newest registered points.");
    }

    private static void CheckRescueAndCoordination()
    {
        using var fixture = new Fixture();
        var review = fixture.Store.PreviewRetention(fixture.Policy);
        bool lockChecked = false;
        string operation = fixture.Store.BeginRetention(review, step =>
        {
            if (step == RetentionStep.Moved && !lockChecked)
            {
                string output = Directory.CreateDirectory(Path.Combine(fixture.Workspace.Root, "restore lock")).FullName;
                Reject(() => fixture.Store.Restore(fixture.Latest, output));
                Check(!Directory.EnumerateFileSystemEntries(output).Any(), "Restore wrote while cleanup held the store lock.");
                lockChecked = true;
            }
        });
        Check(lockChecked, "The cleanup/restore lock was not checked.");
        fixture.Seed("old one");
        string next = VersionStore.NewGenerationId();
        fixture.Store.CreatePoint(fixture.Source, next);
        Check(!fixture.Store.Verify(next), "A new point did not rescue its quarantined object.");
        fixture.Store.PurgeRetention(operation, clock: DateTimeOffset.UtcNow.AddDays(2));
        Check(!fixture.Store.Verify(next) && !fixture.Store.Verify(fixture.Latest), "Purge removed a newly referenced object.");
        Console.WriteLine("PASS restore and cleanup share one lock; new points rescue quarantined objects before confirmation.");

        using var delayed = new Fixture();
        Reject(() => delayed.Store.BeginRetention(delayed.Store.PreviewRetention(delayed.Policy), step =>
        {
            if (step == RetentionStep.Planned)
                throw new IOException("Injected delayed operation.");
        }));
        string id = delayed.Store.ListRetention().Single().Id;
        DateTimeOffset completed = DateTimeOffset.UtcNow.AddDays(30);
        delayed.Store.ResumeRetention(id, clock: completed);
        Reject(() => delayed.Store.PurgeRetention(id, clock: completed.AddHours(12)));
        delayed.Store.PurgeRetention(id, clock: completed.AddDays(2));
        Console.WriteLine("PASS grace starts when quarantine completes, including a long delayed resume.");
    }

    private static void CheckFailures()
    {
        foreach (string scenario in new[] { "primary", "both", "commit", "corrupt", "descriptor", "pending", "stale", "journal", "object" })
        {
            using var fixture = new Fixture();
            var preview = fixture.Store.PreviewRetention(fixture.Policy);
            string? operation = scenario is "journal" or "object" ? fixture.Store.BeginRetention(preview) : null;
            switch (scenario)
            {
                case "primary": File.Delete(Path.Combine(fixture.Destination, "manifests", fixture.Points[0] + ".jsonl")); break;
                case "both":
                    File.Delete(Path.Combine(fixture.Destination, "manifests", fixture.Points[0] + ".jsonl"));
                    File.Delete(Path.Combine(fixture.Destination, "manifests_backup", fixture.Points[0] + ".jsonl")); break;
                case "commit": File.Delete(Path.Combine(fixture.Destination, "commits", fixture.Points[0] + ".commit")); break;
                case "corrupt": File.WriteAllText(Path.Combine(fixture.Destination, "manifests_backup", fixture.Points[0] + ".jsonl"), "bad"); break;
                case "descriptor":
                    File.Copy(Path.Combine(fixture.Destination, "pending", fixture.Points[1] + ".jsonl"),
                        Path.Combine(fixture.Destination, "pending", fixture.Points[0] + ".jsonl"), overwrite: true);
                    Check(!fixture.Store.Verify(fixture.Points[0]), "Descriptor damage blocked independent verification.");
                    break;
                case "pending":
                    fixture.Seed("unfinished");
                    Reject(() => fixture.Store.CreatePoint(fixture.Source, VersionStore.NewGenerationId(), objectCheckpoint: (step, _) =>
                    {
                        if (step == CopyStep.Written) throw new IOException("Injected unfinished copy.");
                    })); break;
                case "stale": fixture.Store.CreatePoint(fixture.Source, VersionStore.NewGenerationId()); break;
                case "journal": File.WriteAllText(Path.Combine(fixture.Destination, "quarantine", operation!, "plan"), "bad"); break;
                case "object":
                    File.WriteAllText(Path.Combine(fixture.Destination, "quarantine", operation!, preview.Objects[0]), "bad"); break;
            }
            string[] before = Directory.GetFiles(Path.Combine(fixture.Destination, "objects"), "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).ToArray();
            if (operation is null)
                Reject(() => fixture.Store.BeginRetention(preview));
            else
                Reject(() => fixture.Store.PurgeRetention(operation, clock: DateTimeOffset.UtcNow.AddDays(2)));
            Check(before.SequenceEqual(Directory.GetFiles(Path.Combine(fixture.Destination, "objects"), "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)), "A rejected cleanup modified the object set.");
        }
        Console.WriteLine("PASS missing, damaged or stale catalogs, pending copies, corrupted journals and objects stop cleanup.");
    }

    private static void CheckPolicy()
    {
        string Id(DateTimeOffset time) => time.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        string old = Id(now.AddYears(-2)), month = Id(now.AddMonths(-5)), week = Id(now.AddDays(-40)),
            yesterday = Id(now.AddDays(-1)), first = Id(now.AddHours(-2)), last = Id(now.AddHours(-1));
        var policy = new RetentionPolicy();
        var keep = policy.Select([old, month, week, yesterday, first, last], now);
        Check(keep.SetEquals([month, week, yesterday, last]), "Daily/weekly/monthly calendar buckets are incorrect.");
        Check(policy.Select([last, old], now).Contains(old), "Clock rollback changed the newest registered generation.");
        Reject(() => policy.Select([Id(now.AddSeconds(1))], now));
        Reject(() => new RetentionPolicy(GraceDays: 0).Validate());
        Console.WriteLine("PASS calendar retention, last-registration protection, future clocks and grace validation.");
    }

    private sealed class Fixture : IDisposable
    {
        internal LabWorkspace Workspace { get; } = LabWorkspace.Create();
        internal string Source { get; }
        internal string Destination { get; }
        internal VersionStore Store { get; }
        internal RetentionPolicy Policy { get; } = new(1, 0, 0, 1);
        internal string[] Points { get; }
        internal string Latest => Points[^1];
        internal Fixture()
        {
            Source = Directory.CreateDirectory(Path.Combine(Workspace.Root, "source")).FullName;
            Destination = Directory.CreateDirectory(Path.Combine(Workspace.Root, "store")).FullName;
            Store = VersionStore.Initialize(Workspace, Destination);
            Points = new[] { -3, -2, 0 }.Select(days => DateTimeOffset.UtcNow.AddDays(days)
                .ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")).ToArray();
            foreach (var (point, text) in Points.Zip(new[] { "old one", "old two", "latest" }))
            {
                Seed(text);
                Store.CreatePoint(Source, point);
            }
        }
        internal void Seed(string text)
        {
            File.WriteAllText(Path.Combine(Source, "shared.txt"), "shared content");
            File.WriteAllText(Path.Combine(Source, "changed.txt"), text);
        }
        public void Dispose()
        {
            using (Workspace.OpenRegisteredRoot()) { }
            Directory.Delete(Workspace.Root, recursive: true);
        }
    }

    private static void Reject(Action operation)
    {
        try { operation(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("An unsafe or interrupted retention operation was accepted.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
