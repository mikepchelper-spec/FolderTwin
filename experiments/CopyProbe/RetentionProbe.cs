using System.Globalization;
using System.Diagnostics;
using System.Reflection;

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

        foreach (var boundary in new[] { RetentionStep.Purging, RetentionStep.Deleted, RetentionStep.Purged })
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
            Reject(() => fixture.Store.UndoRetention(operation));
            fixture.Seed("old one");
            string next = VersionStore.NewGenerationId();
            fixture.Store.CreatePoint(fixture.Source, next);
            Check(!fixture.Store.Verify(next), "A legitimately purged object could not be copied again.");
            fixture.Store.PreviewRetention(fixture.Policy);
        }
        Console.WriteLine("PASS grace blocks immediate purge; interrupted purge resumes and purged content can be copied again.");

        CheckRescueAndCoordination();
        CheckMultipleSources();
        CheckRegistryRecovery();
        CheckWorkerTermination();
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
        foreach (string scenario in new[] { "primary", "both", "commit", "corrupt", "pending", "stale", "journal", "object" })
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
