using System.Diagnostics;
using System.IO;

namespace CopyLab;

internal static class ProductMonitorProbe
{
    internal static async Task RunScheduledAsync()
    {
        var fixture = LabWorkspace.Create();
        string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
        string parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "destinations")).FullName;
        string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
        string file = Path.Combine(source, "synthetic.txt");
        File.WriteAllText(file, "before crash");
        using (var registry = new ProductJobRegistry(settings))
        using (var scheduler = new WindowsSchedule())
        {
            var job = registry.Create(new("Synthetic native monitor", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, parent));
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching);
            try
            {
                string installed = LabInstallation.EnsureWorker(product: true);
                scheduler.RegisterMonitor(registry, installed, atLogon: true);
                if (scheduler.ReadMonitor(registry) is not { Enabled: true, MeetsPolicy: true, AtLogon: true })
                    throw new InvalidOperationException("The native logon monitor policy did not verify.");
                ProductAutomation.StartMonitor(registry, atLogon: false);
                if (scheduler.ReadMonitor(registry) is not { Enabled: true, MeetsPolicy: true, AtLogon: false })
                    throw new InvalidOperationException("The native on-demand monitor policy did not verify.");
                await WaitAsync(() => registry.ReadMonitorProcess() is not null &&
                    registry.RunHistory(job.Id).Runs.Any(run => run.Result?.State == ProductRunState.Confirmed && !run.Running),
                    "The registered task did not launch its monitor and back up the source.");
                long previous = registry.RunHistory(job.Id).Runs[0].Intent.Sequence;
                int pid = registry.ReadMonitorProcess()!.Value;
                using (var process = Process.GetProcessById(pid))
                {
                    if (!string.Equals(process.MainModule?.FileName, installed, StringComparison.OrdinalIgnoreCase) ||
                        scheduler.ReadMonitor(registry) is not { Running: true })
                        throw new InvalidOperationException("The observed process is not the installed synthetic monitor.");
                    process.Kill();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(timeout.Token);
                }
                File.WriteAllText(file, "saved while offline");
                await WaitAsync(() => registry.ReadMonitorProcess() is { } restarted && restarted != pid &&
                    registry.RunHistory(job.Id).Runs.Any(run => run.Intent.Sequence > previous &&
                        run.Result?.State == ProductRunState.Confirmed && !run.Running),
                    "Windows did not restart the failed monitor and reconcile the offline edit.", TimeSpan.FromMinutes(2));
                string generation = registry.RunHistory(job.Id).Runs[0].Generation!;
                scheduler.RemoveMonitor(registry);
                await WaitAsync(() => registry.ReadMonitorProcess() is null, "Removing the task did not stop future dispatch.");
                Directory.Move(source, Path.Combine(fixture.Root, "offline"));
                string restore = Directory.CreateDirectory(Path.Combine(fixture.Root, "restore")).FullName;
                VersionStore.OpenProduct(job).Restore(generation, restore);
                if (File.ReadAllText(Path.Combine(restore, "synthetic.txt")) != "saved while offline")
                    throw new InvalidOperationException("The recovered monitor did not capture the offline bytes.");
            }
            finally
            {
                scheduler.RemoveMonitor(registry);
                await WaitAsync(() => registry.ReadMonitorProcess() is null, "The synthetic monitor still owns the registry.");
            }
        }
        using (fixture.OpenRegisteredRoot()) { }
        Directory.Delete(fixture.Root, recursive: true);
        Console.Error.WriteLine("PASS native monitor task validates optional logon, restarts after real termination, restores an offline edit and withdraws.");
    }

    private static async Task WaitAsync(Func<bool> predicate, string failure, TimeSpan? timeout = null)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            if (predicate()) return;
            await Task.Delay(250);
        }
        while (timer.Elapsed < (timeout ?? TimeSpan.FromSeconds(20)));
        throw new InvalidOperationException(failure);
    }

    internal static async Task RunAsync()
    {
        foreach (var mode in Enum.GetValues<ProductBackupMode>())
            await RunModeAsync(mode);
        Console.Error.WriteLine("PASS headless monitor and workers, single monitor, automatic change, destination return, durable pause and offline recovery for all modes.");
    }

    private static async Task RunModeAsync(ProductBackupMode mode)
    {
        var workspace = LabWorkspace.Create();
        string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "source")).FullName;
        string parent = Directory.CreateDirectory(Path.Combine(workspace.Root, "destinations")).FullName;
        string settings = Directory.CreateDirectory(Path.Combine(workspace.Root, "settings")).FullName;
        string file = Path.Combine(source, "synthetic.txt");
        File.WriteAllText(file, "first");
        File.WriteAllText(Path.Combine(source, "retired.txt"), "keep this point recoverable");
        using (var registry = new ProductJobRegistry(settings))
        {
            var job = registry.Create(new("Synthetic monitor", mode, null),
                LocalJobRoots.ReviewDedicatedDestination(source, parent));
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching,
                authorizeMirror: mode == ProductBackupMode.ProtectedMirror);
            using var monitor = ProductWorker.StartMonitor(settings);
            try
            {
                var first = await WaitForRunAsync(0, ProductRunState.Confirmed);
                using (var duplicate = ProductWorker.StartMonitor(settings))
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await duplicate.WaitForExitAsync(timeout.Token);
                    if (duplicate.ExitCode != 0 || registry.RunHistory(job.Id).Runs.Length != 1)
                        throw new InvalidOperationException("A second monitor was admitted.");
                }
                File.WriteAllText(file, "second");
                var second = await WaitForRunAsync(first.Intent.Sequence, ProductRunState.Confirmed);
                string detached = Path.Combine(Path.GetDirectoryName(job.Destination.LastVolumePath)!, "detached");
                Directory.Move(job.Destination.LastVolumePath, detached);
                File.WriteAllText(file, "after reconnection");
                File.Delete(Path.Combine(source, "retired.txt"));
                var absent = await WaitForRunAsync(second.Intent.Sequence, ProductRunState.WaitingForDestination);
                Directory.Move(detached, job.Destination.LastVolumePath);
                var returned = await WaitForRunAsync(absent.Intent.Sequence, ProductRunState.Confirmed);
                registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Paused);
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    await monitor.WaitForExitAsync(timeout.Token);
                if (monitor.ExitCode != 0) throw new InvalidOperationException("The paused monitor did not stop cleanly.");

                Directory.Move(source, Path.Combine(workspace.Root, "detached-source"));
                string output = Directory.CreateDirectory(Path.Combine(workspace.Root, "restore")).FullName;
                var store = VersionStore.OpenProduct(job);
                store.Restore(returned.Generation!, output);
                if (File.ReadAllText(Path.Combine(output, "synthetic.txt")) != "after reconnection")
                    throw new InvalidOperationException("The automatic point did not restore the expected bytes without its source.");
                if (File.Exists(Path.Combine(output, "retired.txt")) != (mode == ProductBackupMode.Accumulative))
                    throw new InvalidOperationException("The automatic point handled retirement differently from its selected mode.");
                string earlier = Directory.CreateDirectory(Path.Combine(workspace.Root, "restore-earlier")).FullName;
                store.Restore(first.Generation!, earlier);
                if (File.ReadAllText(Path.Combine(earlier, "retired.txt")) != "keep this point recoverable")
                    throw new InvalidOperationException("The old automatic point did not retain the removed file.");
            }
            finally
            {
                if (!monitor.HasExited)
                {
                    monitor.Kill();
                    await monitor.WaitForExitAsync();
                }
            }

            async Task<ProductRunView> WaitForRunAsync(long after, ProductRunState state)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    if (monitor.HasExited) throw new InvalidOperationException($"The monitor stopped with exit code {monitor.ExitCode}.");
                    var completed = registry.RunHistory(job.Id).Runs.FirstOrDefault(run =>
                        run.Intent.Sequence > after && !run.Running && run.Result?.State == state);
                    if (completed is not null) return completed;
                    await Task.Delay(100, timeout.Token);
                }
            }
        }
        using (workspace.OpenRegisteredRoot()) { }
        Directory.Delete(workspace.Root, recursive: true);
    }
}
