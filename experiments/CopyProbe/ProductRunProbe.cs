using System.Diagnostics;
using System.Reflection;

internal static class ProductRunProbe
{
    internal static int Run()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            string source = Folder("Origen de ejecución");
            string settings = Folder("Configuración de ejecución");
            string parent = Folder("Destinos de ejecución");
            File.WriteAllText(Path.Combine(source, "datos.txt"), "contenido sintético");
            using var registry = new ProductJobRegistry(settings);
            var jobs = Enum.GetValues<ProductBackupMode>().Select(mode => registry.Create(new(mode.ToString(), mode, null),
                LocalJobRoots.ReviewDedicatedDestination(source, parent))).ToArray();
            foreach (var job in jobs)
            {
                var result = ProductRunner.Run(registry, job.Id);
                Check(result.State == ProductRunState.Confirmed && result.Generation is not null, result.Detail ?? "The worker failed.");
                var history = registry.RunHistory(job.Id);
                Check(history.Unreadable == 0 && history.Runs is [{ Running: false, Result.State: ProductRunState.Confirmed }] &&
                    history.Runs[0].Intent.Sequence == 1, "The durable completed attempt was not readable.");
                var store = VersionStore.OpenProduct(job);
                Check(!store.Verify(result.Generation!), "The worker confirmed unverified data.");
                string output = Folder("Restauración " + job.Options.Mode);
                store.Restore(result.Generation!, output);
                Check(File.ReadAllText(Path.Combine(output, "datos.txt")) == "contenido sintético", "The worker data did not restore.");
                Check(ProductRunner.Verify(registry, job.Id, result.Generation!).State == ProductRunState.Confirmed,
                    "The explicit verification did not persist its result.");
                bool requested = false;
                var cancelled = ProductRunner.Verify(registry, job.Id, result.Generation!, checkpoint: _ =>
                {
                    if (requested) return;
                    requested = true;
                    registry.RequestRun(job.Id, registry.RunHistory(job.Id).Runs[0].Intent.Id, ProductRunAction.Cancel);
                });
                var separated = registry.RunHistory(job.Id);
                Check(cancelled.State == ProductRunState.Cancelled &&
                    separated.Runs.Any(run => run.Intent.Operation == ProductRunOperation.Backup && run.Result?.State == ProductRunState.Confirmed) &&
                    separated.Runs.Any(run => run.Intent.Operation == ProductRunOperation.Verify && run.Result?.State == ProductRunState.Confirmed),
                    "Verification attempts hid the completed backup or the earlier successful verification.");
            }
            var versioned = jobs.Single(job => job.Options.Mode == ProductBackupMode.Versions);
            using (var lease = registry.StartRun(versioned.Id))
            {
                Check(registry.RunHistory(versioned.Id).Runs[0].Running, "The active attempt was not detected through its lock.");
                Reject(() => registry.StartRun(versioned.Id).Dispose());
                registry.RequestRun(versioned.Id, lease.Intent.Id, ProductRunAction.Pause);
                Check(lease.ReadAction() == ProductRunAction.Pause, "The pause command was not bound to the active attempt.");
                lease.Progress(new(WorkPhase.Analyzing, 0, 1, "datos.txt"), paused: true);
                Check(registry.RunHistory(versioned.Id).Runs[0].Progress?.Paused == true, "The paused snapshot was lost.");
                registry.RequestRun(versioned.Id, lease.Intent.Id, ProductRunAction.Resume);
                Check(lease.ReadAction() == ProductRunAction.Resume, "The resume command was lost.");
            }
            Check(registry.RunHistory(versioned.Id).Runs[0] is { Running: false, Result: null },
                "An interrupted intent was presented as a completed backup.");
            var earlier = DateTimeOffset.UtcNow.AddYears(-1);
            var retried = ProductRunner.Run(registry, versioned.Id, clock: () => earlier);
            var ordered = registry.RunHistory(versioned.Id);
            Check(retried.State == ProductRunState.Confirmed && ordered.Runs[0].Intent.Sequence == 5 &&
                ordered.Runs[0].Intent.StartedUtc < ordered.Runs[1].Intent.StartedUtc,
                "Clock rollback changed the durable attempt order.");

            using (var writer = new FileStream(Path.Combine(source, "datos.txt"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                var failed = ProductRunner.Run(registry, versioned.Id);
                Check(failed.State == ProductRunState.Busy && failed.RelativePath == "datos.txt", "An occupied source lost its path diagnosis.");
            }
            Check(ProductRunner.Run(registry, versioned.Id).State == ProductRunState.Confirmed,
                "The occupied-file attempt did not retry safely.");
            var mirrorJob = jobs.Single(job => job.Options.Mode == ProductBackupMode.ProtectedMirror);
            var mirrored = VersionStore.OpenProduct(mirrorJob);
            File.Delete(Path.Combine(source, "datos.txt"));
            var withheld = ProductRunner.Run(registry, mirrorJob.Id);
            Check(withheld.State == ProductRunState.Review && File.Exists(Path.Combine(mirrored.MirrorCurrentPath, "datos.txt")),
                "An unattended mirror retired content that required review.");
            var review = mirrored.PreviewMirror(mirrorJob.Source.VolumePath);
            Guid approved = registry.ApproveMirror(mirrorJob.Id, review, emptyReviewed: true, massReviewed: true);
            var applied = ProductRunner.Run(registry, mirrorJob.Id, approved);
            Check(applied.State == ProductRunState.Confirmed && !Directory.EnumerateFileSystemEntries(mirrored.MirrorCurrentPath).Any(),
                applied.Detail ?? "The approved mirror did not complete.");
            File.WriteAllText(Path.Combine(source, "datos.txt"), "contenido sintético");
            string moved = Path.Combine(workspace.Root, "Origen apartado");
            Directory.Move(source, moved);
            Check(ProductRunner.Run(registry, versioned.Id).State == ProductRunState.WaitingForSource,
                "An absent source was reported as complete.");
            Directory.Move(moved, source);
            string movedDestination = Path.Combine(Path.GetDirectoryName(versioned.Destination.LastVolumePath)!, "Destino apartado");
            Directory.Move(versioned.Destination.LastVolumePath, movedDestination);
            Check(ProductRunner.Run(registry, versioned.Id).State == ProductRunState.WaitingForDestination,
                "An absent destination was reported as complete.");
            Directory.Move(movedDestination, versioned.Destination.LastVolumePath);
            Check(ProductRunner.Run(registry, versioned.Id).State == ProductRunState.Confirmed,
                "Reconnection did not allow the original job to continue.");
            string runs = Path.Combine(settings, versioned.Id.ToString("N"), "runs");
            string torn = Path.Combine(runs, 5000.ToString("D20") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(torn);
            File.WriteAllText(Path.Combine(torn, "intent.tmp"), "partial synthetic intent");
            Check(ProductRunner.Run(registry, versioned.Id).State == ProductRunState.Confirmed &&
                registry.RunHistory(versioned.Id) is { Unreadable: 1, Runs: [{ Intent.Sequence: 5001 }, ..] } &&
                File.ReadAllText(Path.Combine(torn, "intent.tmp")) == "partial synthetic intent",
                "A torn history record was removed, reused its sequence or stopped independent backup work.");
            CheckScheduledRetries(registry, versioned, settings).GetAwaiter().GetResult();
            CheckWorkers();
            Console.WriteLine("PASS persistent product attempts execute all three modes, retain issues, order independently of the clock and require mirror approval.");
            return 0;
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(workspace.Root, name)).FullName;
    }

    private static async Task CheckScheduledRetries(ProductJobRegistry registry, ProductJob job, string location)
    {
        registry.UpdateSettings(job.Id, "Trabajo actualizado", new TimeOnly(19, 5), enabled: true, retired: false);
        string detached = Path.Combine(Path.GetDirectoryName(job.Destination.LastVolumePath)!, "Destino para reintento");
        Directory.Move(job.Destination.LastVolumePath, detached);
        int delays = 0;
        try
        {
            int exit = await ProductRunner.RunScheduledAsync(location, job.Id, () => true, delay: _ =>
            {
                if (delays++ == 0)
                {
                    var waiting = registry.RunHistory(job.Id).Runs[0];
                    Check(waiting.RetryRunning && waiting.Retry?.Number == 1, "The deferred retry was not tied to its live lease.");
                    Directory.Move(detached, job.Destination.LastVolumePath);
                }
                return Task.CompletedTask;
            });
            var history = registry.RunHistory(job.Id);
            Check(exit == 0 && delays == 60 && history.Runs[0].Result?.State == ProductRunState.Confirmed &&
                history.Runs[1].Result?.State == ProductRunState.WaitingForDestination && !history.Runs[1].RetryRunning,
                "Deferred retry lost the missing destination incident or did not release its waiting lease.");
            Directory.Move(job.Destination.LastVolumePath, detached);
            int cancelled = await ProductRunner.RunScheduledAsync(location, job.Id, () => true, delay: _ =>
            {
                registry.UpdateSettings(job.Id, "Trabajo detenido", new TimeOnly(19, 5), enabled: false, retired: false);
                return Task.CompletedTask;
            });
            Check(cancelled == 0 && registry.RunHistory(job.Id).Runs[0].Result?.State == ProductRunState.WaitingForDestination,
                "Disabling a job did not stop its deferred retry.");
        }
        finally { if (Directory.Exists(detached)) Directory.Move(detached, job.Destination.LastVolumePath); }
        Reject(() => ProductRunner.Run(registry, job.Id));
        var current = registry.Get(job.Id);
        registry.UpdateSettings(job.Id, current.Options.Name, current.Options.DailyAt, enabled: false, retired: true);
        Check(registry.Get(job.Id) is { Retired: true, Enabled: false } &&
            ProductRunner.Verify(registry, job.Id, registry.RunHistory(job.Id).Runs.First(run => run.Result?.State == ProductRunState.Confirmed).Generation!).State == ProductRunState.Confirmed,
            "Retirement prevented verification or changed the preserved backup.");
        registry.UpdateSettings(job.Id, "Trabajo reactivado", new TimeOnly(20, 30), enabled: true, retired: false);
        Check(registry.Get(job.Id).Destination.Marker == job.Destination.Marker && registry.Get(job.Id).Options.Mode == job.Options.Mode,
            "Editing or reactivation changed the data identity or mode.");
    }

    internal static int HeldChild(string root, Guid id)
    {
        var fixture = LabWorkspace.Open(root);
        using var registry = new ProductJobRegistry(Path.Combine(fixture.Root, "settings"));
        bool held = false;
        var result = ProductRunner.Run(registry, id, checkpoint: work =>
        {
            if (held || work.Phase != WorkPhase.Copying) return;
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
            var fixture = LabWorkspace.Create();
            try
            {
                string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
                string parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "target")).FullName;
                string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
                File.WriteAllText(Path.Combine(source, "contenido.txt"), "worker-controlled synthetic data");
                using var registry = new ProductJobRegistry(settings);
                var job = registry.Create(new(mode.ToString(), mode, null), LocalJobRoots.ReviewDedicatedDestination(source, parent));
                string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable unavailable.");
                var start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                foreach (string argument in new[] { "--held-product-child", fixture.Root, job.Id.ToString("N") }) start.ArgumentList.Add(argument);
                using var worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start product worker.");
                var errors = worker.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try
                {
                    string? line = worker.StandardOutput.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
                    Check(line == "READY", "The product worker did not reach its safe copying boundary.");
                    var attempt = registry.RunHistory(job.Id).Runs.Single();
                    Check(attempt.Running, "The real worker was not reported as active.");
                    registry.RequestRun(job.Id, attempt.Intent.Id, ProductRunAction.Pause);
                    worker.StandardInput.WriteLine();
                    worker.StandardInput.Flush();
                    Wait(() => registry.RunHistory(job.Id).Runs[0].Progress?.Paused == true, "The real worker did not pause.");
                    Check(registry.RunHistory(job.Id).Runs[0].Running, "A paused worker released its execution lease.");
                    if (action == "terminate") worker.Kill();
                    else registry.RequestRun(job.Id, attempt.Intent.Id, action == "resume" ? ProductRunAction.Resume : ProductRunAction.Cancel);
                    Check(worker.WaitForExit(15000), "The product worker did not finish.");
                    if (action != "terminate") Check(worker.ExitCode == 0, errors.GetAwaiter().GetResult());
                    var finished = registry.RunHistory(job.Id).Runs[0];
                    Check(!finished.Running && (action switch
                    {
                        "resume" => finished.Result?.State == ProductRunState.Confirmed,
                        "cancel" => finished.Result?.State == ProductRunState.Cancelled,
                        _ => finished.Result is null
                    }), "The completed, cancelled or terminated worker was misreported.");
                    if (action != "resume")
                    {
                        var retry = ProductRunner.Run(registry, job.Id);
                        Check(retry.State == ProductRunState.Confirmed && retry.Generation == finished.Generation,
                            retry.Detail ?? "The interrupted attempt did not resume the same generation.");
                    }
                    var store = VersionStore.OpenProduct(job);
                    string current = registry.RunHistory(job.Id).Runs[0].Generation!;
                    Check(!store.Verify(current), "The controlled product worker lost recoverable data.");
                    Reject(() => registry.RequestRun(job.Id, attempt.Intent.Id, ProductRunAction.Cancel));
                }
                finally { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(15000); } }
            }
            finally
            {
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
        }
        Console.WriteLine("PASS real product workers pause, resume, cancel and recover after termination in all three modes; stale controls are rejected.");
    }

    private static void Wait(Func<bool> condition, string error)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition()) return;
            Thread.Sleep(50);
        }
        throw new InvalidOperationException(error);
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("A concurrent execution was accepted.");
    }
}
