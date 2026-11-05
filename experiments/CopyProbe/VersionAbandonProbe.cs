using System.Diagnostics;
using System.Security.Cryptography;

internal static class VersionAbandonProbe
{
    internal static int Run()
    {
        foreach (var step in Enum.GetValues<AbandonStep>()) CheckBoundary(step);
        foreach (var step in Enum.GetValues<RegistryStep>()) CheckRegistryBoundary(step);
        foreach (var step in Enum.GetValues<RegistryStep>()) CheckRegistrationBoundary(step);
        foreach (var step in Enum.GetValues<CatalogStep>()) CheckCatalogBoundary(step);
        CheckRetention();
        CheckObjectsOnlyRetention();
        CheckWorker();
        Console.WriteLine("PASS version abandonment preserves plans, catalogs and shared objects, resumes metadata boundaries, rejects committed points and permits a fresh verified backup with guarded retention.");
        return 0;
    }

    private static void CheckBoundary(AbandonStep boundary)
    {
        using var fixture = new Fixture(catalogs: true);
        var store = fixture.Store;
        var review = store.PreviewAbandonVersion(fixture.Pending);
        Check(review.Artifacts.Length == 2, "The pending publication's catalogs were not captured.");
        string sourceAside = Path.Combine(fixture.Root.Root, "source absent");
        Directory.Move(fixture.Source, sourceAside);
        bool reached = false;
        Reject(() => store.BeginAbandonVersion(review, step =>
        {
            if (step != boundary) return;
            reached = true;
            throw new IOException("Synthetic abandonment interruption.");
        }));
        Check(reached, "The requested abandonment boundary was not reached.");
        if (boundary != AbandonStep.Completed)
            Reject(() => store.PreviewRetention(new()));
        Check(!store.Verify(fixture.Baseline), "Interrupted abandonment damaged an earlier point.");
        store = VersionStore.OpenProduct(fixture.Job);
        store.ResumeAbandonVersion(fixture.Pending);
        store.ResumeAbandonVersion(fixture.Pending);
        Check(store.ListVersionAbandons().Single().Complete && store.ListPending().Length == 0 &&
            store.RegisteredGenerations().Abandoned.ContainsKey(fixture.Pending) && store.ListPoints().Length == 1 &&
            !store.Verify(fixture.Baseline), "Abandonment did not complete idempotently without the source.");
        var cleanup = store.PreviewRetention(new());
        Check(cleanup.Keep.Contains(fixture.Baseline), "Abandonment excluded an existing protected point.");
    }

    private static void CheckRegistryBoundary(RegistryStep boundary)
    {
        using var fixture = new Fixture(catalogs: true);
        bool reached = false;
        Reject(() => fixture.Store.BeginAbandonVersion(fixture.Store.PreviewAbandonVersion(fixture.Pending), registryCheckpoint: step =>
        {
            if (step != boundary) return;
            reached = true;
            throw new IOException("Synthetic replicated abandonment interruption.");
        }));
        Check(reached, "The replicated abandonment boundary was not reached.");
        Reject(() => fixture.Store.PreviewRetention(new()));
        fixture.Store.ResumeAbandonVersion(fixture.Pending);
        Check(fixture.Store.RegisteredGenerations().Abandoned.ContainsKey(fixture.Pending) && !fixture.Store.Verify(fixture.Baseline),
            "A split abandonment transition did not repair its exact expected event.");
    }

    private static void CheckCatalogBoundary(CatalogStep boundary)
    {
        using var fixture = new Fixture(catalogs: false, publication: boundary);
        if (boundary == CatalogStep.Committed)
        {
            Reject(() => fixture.Store.PreviewAbandonVersion(fixture.Pending));
            Check(!fixture.Store.Verify(fixture.Pending), "A committed point was abandoned or damaged.");
        }
        else
        {
            var review = fixture.Store.PreviewAbandonVersion(fixture.Pending);
            fixture.Store.BeginAbandonVersion(review);
            Check(fixture.Store.ListPending().Length == 0 && fixture.Store.PreviewRetention(new()).Keep.Contains(fixture.Baseline),
                "An interrupted catalog or receipt prevented guarded cleanup after abandonment.");
        }
    }

    private static void CheckRegistrationBoundary(RegistryStep boundary)
    {
        using var fixture = new Fixture(catalogs: false, registration: boundary);
        var review = fixture.Store.PreviewAbandonVersion(fixture.Pending);
        Directory.Move(fixture.Source, Path.Combine(fixture.Root.Root, "source absent"));
        fixture.Store.BeginAbandonVersion(review);
        Check(fixture.Store.ListPending().Length == 0 && fixture.Store.RegisteredGenerations().Abandoned.ContainsKey(fixture.Pending) &&
            !fixture.Store.Verify(fixture.Baseline), "An interrupted original registration could not be preserved and abandoned without its source.");
    }

    private static void CheckObjectsOnlyRetention()
    {
        using var fixture = new Fixture(catalogs: false);
        fixture.Store.BeginAbandonVersion(fixture.Store.PreviewAbandonVersion(fixture.Pending));
        var review = fixture.Store.PreviewRetention(new());
        Check(review.Expire.Length == 0 && review.Objects.Length > 0 && review.Keep.Contains(fixture.Baseline),
            "Abandoned-only objects require cleanup even when no recovery point expires.");
        string operation = fixture.Store.BeginRetention(review);
        Reject(() => fixture.Store.PurgeRetention(operation));
        fixture.Store.UndoRetention(operation);
        Check(!fixture.Store.Verify(fixture.Baseline) && fixture.Store.PreviewRetention(new()).Objects.Length > 0,
            "Undo did not preserve an objects-only quarantine and the existing recovery point.");
    }

    private static void CheckRetention()
    {
        using var fixture = new Fixture(catalogs: false);
        using var registry = fixture.Open();
        File.WriteAllText(fixture.Data, "latest content");
        File.Delete(Path.Combine(fixture.Source, "extra.txt"));
        Check(ProductRunner.Run(registry, fixture.Job.Id).State != ProductRunState.Confirmed, "A changed source satisfied the old pending inventory.");
        var review = fixture.Store.PreviewAbandonVersion(fixture.Pending);
        byte[] bad = (byte[])review.DocumentHash.Clone();
        bad[0] ^= 1;
        Reject(() => fixture.Store.BeginAbandonVersion(review with { DocumentHash = bad }));
        fixture.Store.BeginAbandonVersion(review);
        var fresh = ProductRunner.Run(registry, fixture.Job.Id);
        Check(fresh.State == ProductRunState.Confirmed && fresh.Generation != fixture.Pending &&
            !fixture.Store.Verify(fresh.Generation!), fresh.Detail ?? "A fresh backup reused the abandoned generation.");
        var clock = DateTimeOffset.UtcNow.AddDays(2);
        var policy = new RetentionPolicy(1, 0, 0, 1);
        var cleanup = fixture.Store.PreviewRetention(policy, clock);
        Check(cleanup.Keep.Contains(fresh.Generation!) && cleanup.Objects.Length > 0,
            "Retention did not account for the old point and abandoned partial objects.");
        string operation = fixture.Store.BeginRetention(cleanup, clock: clock);
        Reject(() => fixture.Store.PurgeRetention(operation, clock: clock));
        fixture.Store.UndoRetention(operation);
        Check(!fixture.Store.Verify(fixture.Baseline) && !fixture.Store.Verify(fresh.Generation!), "Undo lost a protected point.");
        operation = fixture.Store.BeginRetention(fixture.Store.PreviewRetention(policy, clock), clock: clock);
        fixture.Store.PurgeRetention(operation, clock: clock.AddDays(1).AddSeconds(1));
        Check(!fixture.Store.Verify(fresh.Generation!) && fixture.Store.PreviewRetention(policy, clock.AddDays(2)).Objects.Length == 0,
            "Purge damaged active data or left abandoned candidates falsely available.");
        string planFile = Path.Combine(fixture.Job.Destination.LastVolumePath, "abandoned", fixture.Pending, "plan");
        byte[] original = File.ReadAllBytes(planFile);
        byte[] corrupt = (byte[])original.Clone(); corrupt[^1] ^= 1;
        File.WriteAllBytes(planFile, corrupt);
        Reject(() => fixture.Store.PreviewRetention(policy, clock));
        File.WriteAllBytes(planFile, original);
        using var scope = new DirectoryFence(fixture.Job.Destination.LastVolumePath);
        using var events = WindowsProbeNative.OpenPlainFile(scope.Root, GenerationRegistry.PrimaryName, FileAccess.Read, FileShare.Read);
        using var copy = WindowsProbeNative.OpenPlainFile(scope.Root, GenerationRegistry.ReplicaName, FileAccess.Read, FileShare.Read);
        Check(SHA256.HashData(events).AsSpan().SequenceEqual(SHA256.HashData(copy)), "Abandonment left different registry replicas.");
    }

    internal static int HeldChild(string root, Guid job, string generation)
    {
        var fixture = LabWorkspace.Open(root);
        using var registry = new ProductJobRegistry(Path.Combine(fixture.Root, "settings"));
        var store = VersionStore.OpenProduct(registry.Get(job));
        bool held = false;
        store.BeginAbandonVersion(store.PreviewAbandonVersion(generation), step =>
        {
            if (held || step != AbandonStep.Archived) return;
            held = true;
            Console.WriteLine("READY"); Console.Out.Flush(); Console.ReadLine();
        });
        return 0;
    }

    private static void CheckWorker()
    {
        using var fixture = new Fixture(catalogs: true);
        string executable = Environment.ProcessPath ?? throw new IOException("Executable unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ProductRunner).Assembly.Location);
        foreach (string argument in new[] { "--held-abandon-version-child", fixture.Root.Root, fixture.Job.Id.ToString("N"), fixture.Pending })
            start.ArgumentList.Add(argument);
        using var worker = Process.Start(start) ?? throw new IOException("Could not start abandonment worker.");
        try
        {
            Check(worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult() == "READY",
                "The abandonment worker did not reach its archive boundary.");
            Reject(() => VersionStore.OpenProduct(fixture.Job).PreviewRetention(new()));
            worker.Kill();
            Check(worker.WaitForExit(15000), "Killed abandonment worker did not exit.");
            Directory.Move(fixture.Source, Path.Combine(fixture.Root.Root, "source absent"));
            fixture.Store.ResumeAbandonVersion(fixture.Pending);
            Check(!fixture.Store.Verify(fixture.Baseline) && fixture.Store.ListPending().Length == 0,
                "A real process interruption lost a point or could not resume without the source.");
        }
        finally { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(15000); } }
    }

    private sealed class Fixture : IDisposable
    {
        internal LabWorkspace Root { get; } = LabWorkspace.Create();
        internal string Source { get; }
        internal string Data { get; }
        internal ProductJob Job { get; }
        internal VersionStore Store { get; }
        internal string Baseline { get; }
        internal string Pending { get; }
        private readonly string settings;
        internal Fixture(bool catalogs, CatalogStep? publication = null, RegistryStep? registration = null)
        {
            Source = Directory.CreateDirectory(Path.Combine(Root.Root, "source")).FullName;
            string parent = Directory.CreateDirectory(Path.Combine(Root.Root, "backups")).FullName;
            settings = Directory.CreateDirectory(Path.Combine(Root.Root, "settings")).FullName;
            Data = Path.Combine(Source, "dato.txt");
            File.WriteAllText(Data, "baseline");
            using var registry = Open();
            Job = registry.Create(new("Version abandonment", ProductBackupMode.Versions, null), LocalJobRoots.ReviewDedicatedDestination(Source, parent));
            Baseline = ProductRunner.Run(registry, Job.Id).Generation ?? throw new IOException("Could not create baseline.");
            Store = VersionStore.OpenProduct(Job);
            File.WriteAllText(Data, "pending content");
            File.WriteAllText(Path.Combine(Source, "extra.txt"), "extra pending content");
            if (catalogs || publication is not null || registration is not null)
            {
                Pending = VersionStore.NewGenerationId();
                Reject(() => Store.CreatePoint(Job.Source.VolumePath, Pending, checkpoint: step =>
                { if (registration is null && step == (publication ?? CatalogStep.BackupPublished)) throw new IOException("Synthetic interrupted publication."); },
                    registryCheckpoint: step =>
                    { if (step == registration) throw new IOException("Synthetic interrupted original registration."); }));
            }
            else
            {
                var cancelled = ProductRunner.Run(registry, Job.Id, checkpoint: work =>
                { if (work.Phase == WorkPhase.Copying && work.Processed == 1) throw new OperationCanceledException(); });
                Check(cancelled.State == ProductRunState.Cancelled, "The partial-copy fixture did not cancel.");
                Pending = cancelled.Generation!;
            }
        }
        internal ProductJobRegistry Open() => new(settings);
        public void Dispose()
        {
            using (Root.OpenRegisteredRoot()) { }
            Directory.Delete(Root.Root, recursive: true);
        }
    }
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("An unsafe abandonment or cleanup was accepted.");
    }
}
