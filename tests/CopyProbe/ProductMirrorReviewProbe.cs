using System.Diagnostics;

internal static class ProductMirrorReviewProbe
{
    internal static int Run()
    {
        using (var fixture = new Fixture())
        using (var registry = fixture.Open())
        {
            var store = VersionStore.OpenProduct(fixture.Job, initialize: true);
            var first = Review(registry, fixture.Job.Id);
            Check(first.Mirror is { Added: 1, Conflicts: 0, ResumePending: false } && store.ListPoints().Length == 0 &&
                !Directory.EnumerateFileSystemEntries(store.MirrorCurrentPath).Any(), "Analysis published or changed user data.");
            Guid approved = registry.ApproveMirror(fixture.Job.Id, first.Intent.Id, true, true);
            Check(ProductRunner.Run(registry, fixture.Job.Id, approved).State == ProductRunState.Confirmed, "Reviewed initial mirror failed.");
            var before = registry.RunHistory(fixture.Job.Id).Runs[0];
            File.WriteAllText(fixture.Data, "second");
            var second = Review(registry, fixture.Job.Id);
            Check(second.Mirror is { Replaced: 1, RequiresMassReview: true } &&
                registry.RunHistory(fixture.Job.Id).Runs.Single(run => run.Intent.Id == before.Intent.Id).Result!.FinishedUtc == before.Result!.FinishedUtc,
                "Analysis lost counts or changed backup age.");
            Reject(() => registry.ApproveMirror(fixture.Job.Id, second.Intent.Id, false, false));
            Guid stale = registry.ApproveMirror(fixture.Job.Id, second.Intent.Id, true, true);
            File.WriteAllText(fixture.Data, "third");
            Check(ProductRunner.Run(registry, fixture.Job.Id, stale).State == ProductRunState.Review &&
                File.ReadAllText(Path.Combine(store.MirrorCurrentPath, "dato.txt")) == "initial", "A stale analysis authorized changed content.");
            var latest = Review(registry, fixture.Job.Id);
            Check(ProductRunner.Run(registry, fixture.Job.Id, registry.ApproveMirror(fixture.Job.Id, latest.Intent.Id, true, true)).State ==
                ProductRunState.Confirmed, "Fresh reviewed mirror failed.");
            File.Delete(fixture.Data);
            var empty = Review(registry, fixture.Job.Id);
            Check(empty.Mirror is { Removed: 1, RequiresEmptyReview: true }, "Empty source was not disclosed.");
            Reject(() => registry.ApproveMirror(fixture.Job.Id, empty.Intent.Id, false, true));
            Check(File.Exists(Path.Combine(store.MirrorCurrentPath, "dato.txt")), "Empty analysis retired data without approval.");
            Check(ProductRunner.Run(registry, fixture.Job.Id, registry.ApproveMirror(fixture.Job.Id, empty.Intent.Id, true, true)).State ==
                ProductRunState.Confirmed, "Explicit empty approval failed.");
            File.WriteAllText(Path.Combine(store.MirrorCurrentPath, "external.txt"), "preserve external edit");
            var conflict = Review(registry, fixture.Job.Id);
            Check(conflict.Mirror is { Conflicts: > 0 }, "An external conflict was omitted from the durable review.");
            Reject(() => registry.ApproveMirror(fixture.Job.Id, conflict.Intent.Id, true, true));
        }
        using (var fixture = new Fixture())
        using (var registry = fixture.Open())
        {
            Check(ProductRunner.Run(registry, fixture.Job.Id).State == ProductRunState.Confirmed, "Pending review fixture failed.");
            File.WriteAllText(fixture.Data, "pending");
            var store = VersionStore.OpenProduct(fixture.Job);
            var preview = store.PreviewMirror(fixture.Job.Source.VolumePath);
            Reject(() => store.BeginMirror(preview, true, true, checkpoint: step =>
            { if (step == MirrorStep.Planned) throw new IOException("Synthetic interrupted mirror."); }));
            var pending = Review(registry, fixture.Job.Id);
            Check(pending.Mirror is { ResumePending: true, Proof: null } && pending.Generation == preview.Generation,
                "Review lost the already authorized pending operation.");
            Reject(() => registry.ApproveMirror(fixture.Job.Id, pending.Intent.Id, true, true));
            Check(ProductRunner.Run(registry, fixture.Job.Id).State == ProductRunState.Confirmed &&
                File.ReadAllText(Path.Combine(store.MirrorCurrentPath, "dato.txt")) == "pending", "Pending work did not resume from its journal.");
        }
        CheckWorkers();
        Console.WriteLine("PASS durable mirror review preserves data and backup dates, enforces empty/mass/conflict approvals, refuses stale summaries and identifies resumable journals.");
        return 0;
    }

    private static ProductRunView Review(ProductJobRegistry registry, Guid id)
    {
        var result = ProductRunner.ReviewMirror(registry, id);
        Check(result.State == ProductRunState.Confirmed, result.Detail ?? "Mirror review failed.");
        var run = registry.RunHistory(id).Runs[0];
        Check(run.Mirror is not null && run.Intent.Operation == ProductRunOperation.ReviewMirror, "Durable review disappeared.");
        return run;
    }

    internal static int HeldChild(string root, Guid id)
    {
        var fixture = LabWorkspace.Open(root);
        using var registry = new ProductJobRegistry(Path.Combine(fixture.Root, "settings"));
        bool held = false;
        var result = ProductRunner.ReviewMirror(registry, id, work =>
        {
            if (held || work.Phase != WorkPhase.Analyzing) return;
            held = true;
            Console.WriteLine("READY");
            Console.Out.Flush();
            Console.ReadLine();
        });
        return result.State is ProductRunState.Confirmed or ProductRunState.Cancelled ? 0 : 1;
    }

    private static void CheckWorkers()
    {
        foreach (string action in new[] { "resume", "cancel", "terminate" })
        {
            using var fixture = new Fixture();
            using var registry = fixture.Open();
            Check(ProductRunner.Run(registry, fixture.Job.Id).State == ProductRunState.Confirmed, "Review worker fixture failed.");
            string executable = Environment.ProcessPath ?? throw new IOException("Executable unavailable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(ProductRunner).Assembly.Location);
            foreach (string argument in new[] { "--held-mirror-review-child", fixture.Root.Root, fixture.Job.Id.ToString("N") })
                start.ArgumentList.Add(argument);
            using var worker = Process.Start(start) ?? throw new IOException("Could not start mirror review worker.");
            var errors = worker.StandardError.ReadToEndAsync();
            try
            {
                Check(worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult() == "READY",
                    "Review worker did not reach its analysis boundary.");
                var run = registry.RunHistory(fixture.Job.Id).Runs[0];
                registry.RequestRun(fixture.Job.Id, run.Intent.Id, ProductRunAction.Pause);
                worker.StandardInput.WriteLine();
                worker.StandardInput.Flush();
                Wait(() => registry.RunHistory(fixture.Job.Id).Runs[0].Progress?.Paused == true, "Review worker did not pause.");
                Reject(() => ProductRunner.Run(registry, fixture.Job.Id));
                Reject(() => VersionStore.OpenProduct(fixture.Job).PreviewRetention(new()));
                if (action == "terminate")
                {
                    worker.Kill();
                    Check(worker.WaitForExit(15000), "Killed review worker did not exit.");
                    Check(registry.RunHistory(fixture.Job.Id).Runs[0] is { Running: false, Result: null, Mirror: null },
                        "An interrupted analysis was reported ready to apply.");
                    _ = Review(registry, fixture.Job.Id);
                }
                else
                {
                    registry.RequestRun(fixture.Job.Id, run.Intent.Id, action == "resume" ? ProductRunAction.Resume : ProductRunAction.Cancel);
                    Check(worker.WaitForExit(15000) && worker.ExitCode == 0, "Review worker failed. " + errors.GetAwaiter().GetResult());
                    var after = registry.RunHistory(fixture.Job.Id).Runs[0];
                    Check(after.Result?.State == (action == "resume" ? ProductRunState.Confirmed : ProductRunState.Cancelled) &&
                        (after.Mirror is not null) == (action == "resume"), "Review control produced a false approval state.");
                }
                Check(VersionStore.OpenProduct(fixture.Job).ListPoints().Length == 1 &&
                    File.ReadAllText(Path.Combine(VersionStore.OpenProduct(fixture.Job).MirrorCurrentPath, "dato.txt")) == "initial",
                    "Review worker published data or changed the existing mirror.");
            }
            finally { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(15000); } }
        }
        Console.WriteLine("PASS real mirror-analysis workers pause, resume, cancel and recover after termination; data operations remain excluded during analysis.");
    }

    private sealed class Fixture : IDisposable
    {
        internal LabWorkspace Root { get; } = LabWorkspace.Create();
        internal ProductJob Job { get; }
        internal string Data { get; }
        private readonly string settings;
        internal Fixture()
        {
            string source = Directory.CreateDirectory(Path.Combine(Root.Root, "source")).FullName;
            string parent = Directory.CreateDirectory(Path.Combine(Root.Root, "backups")).FullName;
            settings = Directory.CreateDirectory(Path.Combine(Root.Root, "settings")).FullName;
            Data = Path.Combine(source, "dato.txt");
            File.WriteAllText(Data, "initial");
            using var registry = Open();
            Job = registry.Create(new("Mirror review", ProductBackupMode.ProtectedMirror, null), LocalJobRoots.ReviewDedicatedDestination(source, parent));
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
        throw new InvalidOperationException("An unreviewed mirror operation was accepted.");
    }
    private static void Wait(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition()) return;
            Thread.Sleep(50);
        }
        throw new InvalidOperationException(message);
    }
}
