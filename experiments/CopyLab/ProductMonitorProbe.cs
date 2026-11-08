using System.IO;

namespace CopyLab;

internal static class ProductMonitorProbe
{
    internal static async Task RunAsync()
    {
        var workspace = LabWorkspace.Create();
        string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "source")).FullName;
        string parent = Directory.CreateDirectory(Path.Combine(workspace.Root, "destinations")).FullName;
        string settings = Directory.CreateDirectory(Path.Combine(workspace.Root, "settings")).FullName;
        string file = Path.Combine(source, "synthetic.txt");
        File.WriteAllText(file, "first");
        using (var registry = new ProductJobRegistry(settings))
        {
            var job = registry.Create(new("Synthetic monitor", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, parent));
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching);
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
        Console.WriteLine("PASS headless monitor and workers, single monitor, automatic change, destination return, durable pause and restore without origin.");
    }
}
