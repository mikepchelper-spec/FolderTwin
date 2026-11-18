using System.Diagnostics;
using System.Text.RegularExpressions;

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
        string? secondVolumeRoot = Environment.GetEnvironmentVariable("FOLDERTWIN_SECOND_VOLUME_ROOT");
        var destination = !string.IsNullOrWhiteSpace(secondVolumeRoot)
            ? LabWorkspace.CreateOnFixedVolume(secondVolumeRoot)
            : LabWorkspace.Create();
        Process? monitor = null;
        bool completed = false;
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

            string staged = NewFolder("staged-subtree");
            var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int group = 0; group < 8; group++)
            {
                string part = $"part-{group:D2}";
                Directory.CreateDirectory(Path.Combine(staged, part));
                for (int item = 0; item < 32; item++)
                {
                    string relative = Path.Combine(part, $"sample-{item:D2}.txt");
                    string content = $"synthetic descendant {relative}";
                    File.WriteAllText(Path.Combine(staged, relative), content);
                    expectedFiles.Add(relative, content);
                }
            }
            string imported = Path.Combine(source, "imported");
            Directory.Move(staged, imported);
            var arrived = await WaitFor(first.Intent.Sequence, ProductRunState.Confirmed);
            Assert(arrived.Result is { Scoped: false }, "Moving a whole subtree did not request a complete comparison.");
            Directory.Move(imported, Path.Combine(source, "renamed"));
            bool protectedMirror = mode == ProductBackupMode.ProtectedMirror;
            var renamed = await WaitFor(arrived.Intent.Sequence, protectedMirror ? ProductRunState.Review : ProductRunState.Confirmed);
            Change(true, false, ProductWatchMode.Paused);
            await WaitForStop();
            Directory.Move(source, Path.Combine(fixture.Root, "offline-source"));
            var store = VersionStore.OpenProduct(job);
            string snapshot = NewFolder("subtree-before-rename");
            store.Restore(arrived.Generation!, snapshot);
            CheckSubtree(snapshot, "imported");
            if (protectedMirror)
            {
                Assert(renamed.Generation is null && Directory.Exists(Path.Combine(store.MirrorCurrentPath, "imported")) &&
                    !Directory.Exists(Path.Combine(store.MirrorCurrentPath, "renamed")), "The protected mirror applied a mass rename without review.");
                CheckSubtree(store.MirrorCurrentPath, "imported");
            }
            else
            {
                string output = NewFolder("subtree-after-rename");
                store.Restore(renamed.Generation!, output);
                CheckSubtree(output, "renamed");
                if (mode == ProductBackupMode.Accumulative) CheckSubtree(output, "imported");
                else Assert(!Directory.Exists(Path.Combine(output, "imported")), "Versioning included the old subtree path in the new point.");
            }
            Console.WriteLine($"PASS {mode}: pause, disable and retire while waiting, continued after explicit activation and preserved prior points and incidents.");
            Console.WriteLine($"PASS {mode}: real watcher backed up {expectedFiles.Count} descendants after a directory move, verified rename policy and restored offline.");
            completed = true;

            void CheckSubtree(string root, string name)
            {
                string subtree = Path.Combine(root, name);
                string[] files = Directory.EnumerateFiles(subtree, "*", SearchOption.AllDirectories).Where(file =>
                    !Regex.IsMatch(Path.GetFileName(file), @"^\.foldertwin-[0-9A-F]{64}\.tmp\.(lock|ready)$")).ToArray();
                Assert(files.Length == expectedFiles.Count && files.All(file =>
                    expectedFiles.TryGetValue(Path.GetRelativePath(subtree, file), out var expected) && File.ReadAllText(file) == expected),
                    $"The subtree {name} had a missing or changed descendant.");
            }

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
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
                while (true)
                {
                    Assert(!monitor!.HasExited, "The packaged monitor exited before the expected state.");
                    var history = registry.RunHistory(job.Id);
                    var observed = history.Runs.FirstOrDefault(run => run.Intent.Sequence > after && !run.Running && run.Result?.State == state);
                    if (observed is not null) return observed;
                    try { await Task.Delay(150, timeout.Token); }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                    {
                        var latest = history.Runs.FirstOrDefault();
                        throw new TimeoutException($"{mode}: expected {state} after attempt {after}. Latest attempt " +
                            $"{latest?.Intent.Sequence}: {latest?.Result?.State}, {latest?.Result?.Detail ?? latest?.Progress?.Work.Phase.ToString()}.");
                    }
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
            if (completed)
            {
                using (destination.OpenRegisteredRoot()) { }
                Directory.Delete(destination.Root, recursive: true);
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
            else Console.Error.WriteLine($"Synthetic fixture preserved for diagnosis: {fixture.Root} and {destination.Root}.");
        }
        string NewFolder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
