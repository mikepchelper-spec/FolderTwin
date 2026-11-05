using System.Diagnostics;

internal static class ProductRestoreProbe
{
    internal static int Run()
    {
        foreach (var mode in Enum.GetValues<ProductBackupMode>())
        {
            using var fixture = new Fixture(mode);
            using var registry = fixture.Open();
            var job = fixture.Job;
            var backup = registry.RunHistory(job.Id).Runs.Single();
            string absent = Path.Combine(fixture.Workspace.Root, "source absent");
            Directory.Move(fixture.Source, absent);
            var reviewed = fixture.Review();
            Guid request = registry.ApproveRestore(job.Id, fixture.Generation, reviewed);
            var restored = ProductRunner.Restore(registry, job.Id, request);
            Check(restored.State == ProductRunState.Confirmed && fixture.Content(reviewed), restored.Detail ?? "Restore failed without source.");
            Check(registry.RunHistory(job.Id).Runs[0] is { Intent.Operation: ProductRunOperation.Restore, Restore: not null } &&
                registry.RunHistory(job.Id).Runs.Single(run => run.Intent.Operation == ProductRunOperation.Backup).Result is { } preserved &&
                preserved.IntentHash.AsSpan().SequenceEqual(backup.Result!.IntentHash) && preserved.FinishedUtc == backup.Result.FinishedUtc &&
                preserved.State == backup.Result.State,
                "Restore changed the backup result or lost its reviewed location.");
            Check(ProductRunner.Restore(registry, job.Id, request).State == ProductRunState.Review && fixture.Content(reviewed),
                "A reused request overwrote a previously recovered directory.");

            var fileTarget = fixture.Review();
            Guid fileRequest = registry.ApproveRestore(job.Id, fixture.Generation, fileTarget, "a.txt");
            Check(ProductRunner.Restore(registry, job.Id, fileRequest).State == ProductRunState.Confirmed &&
                File.Exists(Path.Combine(fixture.PathFor(fileTarget), "a.txt")) && !File.Exists(Path.Combine(fixture.PathFor(fileTarget), "b.txt")),
                "Single-file restoration recovered the wrong scope.");
            Reject(() => registry.ApproveRestore(job.Id, fixture.Generation, fixture.Review(), "..\\escape.txt"));

            var cancelled = fixture.Review();
            Guid cancelRequest = registry.ApproveRestore(job.Id, fixture.Generation, cancelled);
            var result = ProductRunner.Restore(registry, job.Id, cancelRequest, work =>
            {
                if (work.Phase == WorkPhase.Restoring && work.Processed == 1) throw new OperationCanceledException();
            });
            Check(result.State == ProductRunState.Cancelled && Directory.EnumerateFiles(fixture.PathFor(cancelled), "*.txt").Count() == 1,
                "Cancellation reported completion or discarded the partial directory.");

            var substituted = fixture.Review();
            Guid stale = registry.ApproveRestore(job.Id, fixture.Generation, substituted);
            string moved = Path.Combine(fixture.Workspace.Root, "parent aside");
            Directory.Move(fixture.Output, moved);
            Directory.CreateDirectory(fixture.Output);
            Check(ProductRunner.Restore(registry, job.Id, stale).State == ProductRunState.Review &&
                !Directory.EnumerateFileSystemEntries(fixture.Output).Any(), "A replaced output parent accepted restoration.");
            Directory.Delete(fixture.Output);
            Directory.Move(moved, fixture.Output);

            Directory.Move(absent, fixture.Source);
            var insideSource = LocalJobRoots.ReviewDedicatedDestination(job.Destination.LastVolumePath, fixture.Source);
            Reject(() => registry.ApproveRestore(job.Id, fixture.Generation, insideSource));
            var valid = fixture.Review();
            Guid validRequest = registry.ApproveRestore(job.Id, fixture.Generation, valid);
            registry.UpdateSettings(job.Id, job.Options.Name, null, false, true);
            Check(ProductRunner.Restore(registry, job.Id, validRequest).State == ProductRunState.Confirmed,
                "Retiring a job disabled recovery of its preserved backup.");
        }
        CheckWorkers();
        Console.WriteLine("PASS product restoration covers all modes, absent source, scoped files, immutable outputs, cancellation, substitution, retired jobs and persistent operation history.");
        return 0;
    }

    internal static int HeldChild(string root, Guid id, Guid request)
    {
        var fixture = LabWorkspace.Open(root);
        using var registry = new ProductJobRegistry(Path.Combine(fixture.Root, "settings"));
        bool held = false;
        var result = ProductRunner.Restore(registry, id, request, work =>
        {
            if (held || work.Phase != WorkPhase.Restoring || work.RelativePath is null) return;
            held = true;
            Console.WriteLine("READY");
            Console.Out.Flush();
            Console.ReadLine();
        });
        return result.State is ProductRunState.Confirmed or ProductRunState.Cancelled ? 0 : 1;
    }

    private static void CheckWorkers()
    {
        foreach (var mode in Enum.GetValues<ProductBackupMode>())
        foreach (string action in new[] { "resume", "cancel", "terminate" })
        {
            using var fixture = new Fixture(mode);
            using var registry = fixture.Open();
            var target = fixture.Review();
            Guid request = registry.ApproveRestore(fixture.Job.Id, fixture.Generation, target);
            string executable = Environment.ProcessPath ?? throw new IOException("Executable unavailable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(ProductRunner).Assembly.Location);
            foreach (string argument in new[] { "--held-restore-child", fixture.Workspace.Root, fixture.Job.Id.ToString("N"), request.ToString("N") })
                start.ArgumentList.Add(argument);
            using var worker = Process.Start(start) ?? throw new IOException("Could not start restore worker.");
            var errors = worker.StandardError.ReadToEndAsync();
            try
            {
                string? ready = worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
                Check(ready == "READY", "Restore worker did not reach its copying boundary.");
                var attempt = registry.RunHistory(fixture.Job.Id).Runs[0];
                Check(attempt.Running && attempt.Restore?.Id == request, "The live restore worker lost its request.");
                registry.RequestRun(fixture.Job.Id, attempt.Intent.Id, ProductRunAction.Pause);
                worker.StandardInput.WriteLine();
                worker.StandardInput.Flush();
                Wait(() => registry.RunHistory(fixture.Job.Id).Runs[0].Progress?.Paused == true, "Restore did not pause.");
                Reject(() => ProductRunner.Run(registry, fixture.Job.Id));
                var claim = LocalJobRoots.ReviewDedicatedDestination(fixture.PathFor(target), fixture.Parent);
                Reject(() => registry.Create(new("Conflicting registration", ProductBackupMode.Versions, null), claim));
                Reject(() => VersionStore.OpenProduct(fixture.Job).PreviewRetention(new()));
                if (action == "terminate")
                {
                    worker.Kill();
                    Check(worker.WaitForExit(15000), "Killed restore worker did not exit.");
                    Check(registry.RunHistory(fixture.Job.Id).Runs[0] is { Running: false, Result: null }, "Interrupted restore was reported complete.");
                    var fresh = fixture.Review();
                    Guid freshRequest = registry.ApproveRestore(fixture.Job.Id, fixture.Generation, fresh);
                    Check(ProductRunner.Restore(registry, fixture.Job.Id, freshRequest).State == ProductRunState.Confirmed &&
                        fixture.Content(fresh) && Directory.Exists(fixture.PathFor(target)),
                        "Retry did not recover to a fresh directory while preserving the interrupted output.");
                }
                else
                {
                    registry.RequestRun(fixture.Job.Id, attempt.Intent.Id,
                        action == "resume" ? ProductRunAction.Resume : ProductRunAction.Cancel);
                    Check(worker.WaitForExit(15000) && worker.ExitCode == 0, "Controlled restore worker failed. " + errors.GetAwaiter().GetResult());
                    Check(registry.RunHistory(fixture.Job.Id).Runs[0].Result?.State ==
                        (action == "resume" ? ProductRunState.Confirmed : ProductRunState.Cancelled), "Restore control produced the wrong result.");
                    if (action == "resume") Check(fixture.Content(target), "Resumed worker lost recovered content.");
                }
            }
            finally
            {
                if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(15000); }
            }
        }
        Console.WriteLine("PASS real restore workers pause, resume, cancel and survive termination recovery in all modes; backup, cleanup and conflicting registrations are excluded.");
    }

    private sealed class Fixture : IDisposable
    {
        internal LabWorkspace Workspace { get; } = LabWorkspace.Create();
        internal string Source { get; }
        internal string Parent { get; }
        internal string Output { get; }
        internal ProductJob Job { get; }
        internal string Generation { get; }
        private readonly string settings;
        internal Fixture(ProductBackupMode mode)
        {
            Source = Directory.CreateDirectory(Path.Combine(Workspace.Root, "source")).FullName;
            Parent = Directory.CreateDirectory(Path.Combine(Workspace.Root, "backups")).FullName;
            Output = Directory.CreateDirectory(Path.Combine(Workspace.Root, "recovered")).FullName;
            settings = Directory.CreateDirectory(Path.Combine(Workspace.Root, "settings")).FullName;
            foreach (string name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(Source, name), name);
            using var registry = Open();
            Job = registry.Create(new("Restoration fixture", mode, null), LocalJobRoots.ReviewDedicatedDestination(Source, Parent));
            var result = ProductRunner.Run(registry, Job.Id);
            Check(result.State == ProductRunState.Confirmed, result.Detail ?? "Could not create restore fixture.");
            Generation = result.Generation!;
        }
        internal ProductJobRegistry Open() => new(settings);
        internal DedicatedDestinationReview Review() => LocalJobRoots.ReviewDedicatedDestination(Job.Destination.LastVolumePath, Output);
        internal string PathFor(DedicatedDestinationReview target) => Path.Combine(target.Parent.VolumePath, target.FolderName);
        internal bool Content(DedicatedDestinationReview target) => new[] { "a.txt", "b.txt", "c.txt" }
            .All(name => File.ReadAllText(Path.Combine(PathFor(target), name)) == name);
        public void Dispose()
        {
            using (Workspace.OpenRegisteredRoot()) { }
            Directory.Delete(Workspace.Root, recursive: true);
        }
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("An unsafe restoration action was accepted.");
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
