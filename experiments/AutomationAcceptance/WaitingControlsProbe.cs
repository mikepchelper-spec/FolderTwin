using System.Diagnostics;

internal static class WaitingControlsProbe
{
    internal static async Task RunAsync(string path)
    {
        string executable = Path.GetFullPath(path);
        foreach (var mode in Enum.GetValues<ProductBackupMode>()) await CheckMode(executable, mode);
        Console.WriteLine("PASS all modes preserve changes and incidents while paused, disabled or retired during destination wait.");
    }

    private static async Task CheckMode(string executable, ProductBackupMode mode)
    {
        var fixture = LabWorkspace.Create();
        var destination = LabWorkspace.CreateOnFixedVolume(@"D:\");
        Process? monitor = null;
        try
        {
            string source = NewFolder("source");
            string settings = NewFolder("settings");
            string path = Path.Combine(source, "sample.txt");
            File.WriteAllText(path, "initial synthetic bytes");
            using var registry = new ProductJobRegistry(settings);
            var job = registry.Create(new($"Synthetic waiting control {mode}", mode, null),
                LocalJobRoots.ReviewDedicatedDestination(source, destination.Root));
            Change(true, false, ProductWatchMode.Watching);
            StartMonitor();
            var first = await WaitFor(0, ProductRunState.Confirmed);
            string target = job.Destination.LastVolumePath, detached = target + "-synthetic-offline";
            foreach (string control in new[] { "pause", "disable", "retire" })
            {
                Directory.Move(target, detached);
                File.WriteAllText(path, $"edited during {control}");
                var absent = await WaitFor(first.Intent.Sequence, ProductRunState.WaitingForDestination);
                switch (control)
                {
                    case "pause": Change(true, false, ProductWatchMode.Paused); break;
                    case "disable": Change(false, false, ProductWatchMode.Off); break;
                    case "retire": Change(false, true, ProductWatchMode.Off); break;
                }
                await WaitForStop();
                Directory.Move(detached, target);
                StartMonitor();
                await WaitForStop();
                var stopped = registry.RunHistory(job.Id);
                Assert(stopped.Runs[0].Intent.Sequence == absent.Intent.Sequence, "The stopped job dispatched after the registered disk returned.");
                string previous = NewFolder("prior-" + control);
                VersionStore.OpenProduct(job).Restore(first.Generation!, previous, "sample.txt");
                Assert(File.ReadAllText(Path.Combine(previous, "sample.txt")) != $"edited during {control}", "A stopped job mutated its prior generation.");
                Change(true, false, ProductWatchMode.Watching);
                StartMonitor();
                first = await WaitFor(absent.Intent.Sequence, ProductRunState.Confirmed);
                string current = NewFolder("restored-" + control);
                VersionStore.OpenProduct(job).Restore(first.Generation!, current, "sample.txt");
                Assert(File.ReadAllText(Path.Combine(current, "sample.txt")) == $"edited during {control}", "The resumed job lost its pending edit.");
                Assert(registry.RunHistory(job.Id).Runs.Any(run => run.Intent.Sequence == absent.Intent.Sequence &&
                    run.Result?.State == ProductRunState.WaitingForDestination), "Resuming erased the prior incident.");
            }
            Change(true, false, ProductWatchMode.Paused);
            await WaitForStop();
            Console.WriteLine($"PASS {mode}: pause, disable and retire while waiting, continued after explicit activation and preserved prior points and incidents.");

            void Change(bool enabled, bool retired, ProductWatchMode watch) =>
                registry.UpdateSettings(job.Id, job.Options.Name, null, enabled, retired, watch,
                    authorizeMirror: mode == ProductBackupMode.ProtectedMirror && watch == ProductWatchMode.Watching);

            void StartMonitor()
            {
                monitor?.Dispose();
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--watch-product-jobs");
                start.ArgumentList.Add(settings);
                monitor = Process.Start(start) ?? throw new InvalidOperationException("Failed to launch the packaged monitor.");
            }

            async Task WaitForStop()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await monitor!.WaitForExitAsync(timeout.Token);
                Assert(monitor.ExitCode == 0, "The packaged monitor did not exit voluntarily.");
            }

            async Task<ProductRunView> WaitFor(long after, ProductRunState state)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                while (true)
                {
                    Assert(!monitor!.HasExited, "The packaged monitor exited before the expected state.");
                    var history = registry.RunHistory(job.Id);
                    var observed = history.Runs.FirstOrDefault(run => run.Intent.Sequence > after && !run.Running && run.Result?.State == state);
                    if (observed is not null) return observed;
                    await Task.Delay(150, timeout.Token);
                }
            }
        }
        finally
        {
            if (monitor is not null)
            {
                if (!monitor.HasExited) { monitor.Kill(entireProcessTree: true); await monitor.WaitForExitAsync(); }
                monitor.Dispose();
            }
            using (destination.OpenRegisteredRoot()) { }
            Directory.Delete(destination.Root, recursive: true);
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }
        string NewFolder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
