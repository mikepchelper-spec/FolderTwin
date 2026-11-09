using System.Diagnostics;

internal static class DailyPerformanceProbe
{
    internal static async Task RunAsync(string executable, int files, ProductBackupMode mode, bool prepared)
    {
        var fixture = LabWorkspace.Create();
        var destination = LabWorkspace.CreateOnFixedVolume(@"D:\");
        Process? monitor = null;
        bool completed = false;
        try
        {
            foreach (var workspace in new[] { fixture, destination })
            {
                using var root = workspace.OpenRegisteredRoot();
                LabWorkspace.RequireSpace(512L * 1024 * 1024 + 20L * 1024 * files, LabWorkspace.AvailableSpace(root.Root));
            }
            string source = Folder("source"), settings = Folder("settings");
            if (Path.GetPathRoot(source) != @"C:\") throw new InvalidOperationException("The source fixture must be on C:.");
            Console.WriteLine($"Seeding {files} synthetic files on C: for {mode}.");
            var seed = Stopwatch.StartNew();
            for (int i = 0; i < files; i += 256)
            {
                string group = Directory.CreateDirectory(Path.Combine(source, $"group-{i / 256:D4}")).FullName;
                for (int j = i; j < Math.Min(i + 256, files); j++)
                    File.WriteAllText(Path.Combine(group, $"file-{j:D6}.txt"), $"synthetic {j % 32:D2}");
            }
            Console.WriteLine(FormattableString.Invariant($"seed_s={seed.Elapsed.TotalSeconds:F3}"));
            using var registry = new ProductJobRegistry(settings);
            var job = registry.Create(new($"Synthetic performance {mode}", mode, null),
                LocalJobRoots.ReviewDedicatedDestination(source, destination.Root));
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching,
                authorizeMirror: mode == ProductBackupMode.ProtectedMirror);
            // Keep the production debounce and leave time for isolated measurements at large scale.
            File.WriteAllText(Path.Combine(settings, "monitor-timing.json"), """{"QuietSeconds":2,"MaximumWaitSeconds":20,"ReconcileSeconds":3600}""");
            if (prepared && mode != ProductBackupMode.Versions)
            {
                Console.WriteLine("Preparing and verifying the starting tree on D:.");
                // Call the fixture builder from the same core, including earlier packages where it is private.
                var prepare = typeof(AutomaticScaleProbe).GetMethod("PrepareCurrentTree",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                var setup = Stopwatch.StartNew();
                prepare.Invoke(null, [job, source]);
                Console.WriteLine(FormattableString.Invariant($"prepared_s={setup.Elapsed.TotalSeconds:F3}"));
            }
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--watch-product-jobs");
            start.ArgumentList.Add(settings);
            monitor = Process.Start(start) ?? throw new InvalidOperationException("The monitor did not start.");
            Console.WriteLine($"C: to D:, {mode}, {files} files, Windows {Environment.OSVersion.Version}; monitor PID {monitor.Id}.");
            var (last, _) = await WaitFor(0);
            if (last.Result?.State is not (ProductRunState.Confirmed or ProductRunState.Unchanged))
                throw new InvalidDataException($"Startup did not settle: {last.Result}");
            Console.WriteLine(FormattableString.Invariant($"startup_state={last.Result.State}, worker_s={(last.Result.FinishedUtc - last.Intent.StartedUtc).TotalSeconds:F3}"));

            string relative(string name) => Path.Combine("group-0000", name);
            string at(string name) => Path.Combine(source, relative(name));
            var restores = new List<(string Phase, string Generation, string Path, string Bytes)>();
            var store = VersionStore.OpenProduct(job);
            string current = last.Generation ?? store.RegisteredGenerations().MirrorGeneration!;
            await Change("edit", () =>
            {
                DateTime stamp = File.GetLastWriteTimeUtc(at("file-000000.txt"));
                File.WriteAllText(at("file-000000.txt"), "synthetic XX");
                File.SetLastWriteTimeUtc(at("file-000000.txt"), stamp);
            }, "file-000000.txt", "synthetic XX");
            await Change("temporary-save", () =>
            {
                File.WriteAllText(at("saving.tmp"), "saved through temporary file");
                File.Move(at("saving.tmp"), at("file-000001.txt"), overwrite: true);
            }, "file-000001.txt", "saved through temporary file");
            await Change("create", () => File.WriteAllText(at("new.txt"), "new synthetic data"), "new.txt", "new synthetic data");
            restores.Add(("before-rename", current, relative("file-000002.txt"), "synthetic 02"));
            await Change("rename", () => File.Move(at("file-000002.txt"), at("renamed.txt")), "renamed.txt", "synthetic 02");
            restores.Add(("before-delete", current, relative("file-000003.txt"), "synthetic 03"));
            await Change("delete", () => File.Delete(at("file-000003.txt")), "file-000003.txt", "synthetic 03");

            await WaitForWatching();
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Paused);
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15))) await monitor.WaitForExitAsync(deadline.Token);
            if (monitor.ExitCode != 0) throw new InvalidDataException("The monitor did not stop normally.");
            Directory.Move(source, Path.Combine(fixture.Root, "offline-source"));
            foreach (var (phase, generation, path, bytes) in restores)
            {
                string output = Folder("restore-" + phase);
                var timer = Stopwatch.StartNew();
                store.Restore(generation, output, path);
                if (File.ReadAllText(Path.Combine(output, path)) != bytes) throw new InvalidDataException($"Wrong bytes for {phase}.");
                Console.WriteLine(FormattableString.Invariant($"{phase}: restore_offline_s={timer.Elapsed.TotalSeconds:F3}"));
            }
            completed = true;

            async Task Change(string phase, Action change, string name, string bytes)
            {
                await WaitForWatching();
                DateTimeOffset changed = DateTimeOffset.UtcNow;
                change();
                bool noChange = mode == ProductBackupMode.Accumulative && phase == "delete";
                ProductRunState expected = noChange ? ProductRunState.Unchanged : ProductRunState.Confirmed;
                long after = last.Intent.Sequence;
                ProductRunView observed;
                DateTimeOffset? detection = null;
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(files == 4096 ? 3 : 15));
                while (true)
                {
                    var (attempt, pendingObservation) = await WaitFor(after, deadline.Token);
                    detection ??= pendingObservation;
                    if (attempt.Result!.State == expected && attempt.Intent.StartedUtc >= changed)
                    {
                        observed = attempt;
                        break;
                    }
                    if (attempt.Result.State != ProductRunState.Unchanged)
                        throw new InvalidDataException($"{phase}: {attempt.Result}");
                    Console.WriteLine(FormattableString.Invariant($"{phase}: intervening unchanged attempt {attempt.Intent.Sequence}, dispatch_s={(attempt.Intent.StartedUtc - changed).TotalSeconds:F3}"));
                    after = attempt.Intent.Sequence;
                }
                last = observed;
                var result = observed.Result!;
                current = observed.Generation ?? current;
                if (phase != "delete" || noChange) restores.Add((phase, current, relative(name), bytes));
                if (phase is "rename" or "delete" && mode != ProductBackupMode.Versions)
                {
                    string old = relative(phase == "rename" ? "file-000002.txt" : "file-000003.txt");
                    if (File.Exists(Path.Combine(store.MirrorCurrentPath, old)) != (mode == ProductBackupMode.Accumulative))
                        throw new InvalidDataException($"{phase}: wrong policy for the old path.");
                }
                string pending = detection is { } seen ? (seen - changed).TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "unobserved";
                Console.WriteLine(FormattableString.Invariant($"{phase}: pending_s={pending}, dispatch_s={(observed.Intent.StartedUtc - changed).TotalSeconds:F3}, worker_s={(result.FinishedUtc - observed.Intent.StartedUtc).TotalSeconds:F3}, confirm_s={(result.FinishedUtc - changed).TotalSeconds:F3}, scoped={result.Scoped}"));
            }

            async Task<(ProductRunView Run, DateTimeOffset? Pending)> WaitFor(long after, CancellationToken cancel = default)
            {
                DateTimeOffset? pending = null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromMinutes(30));
                while (true)
                {
                    if (monitor.HasExited) throw new InvalidOperationException("The monitor exited before the attempt completed.");
                    registry.ReadMonitorProcess(job.Id, out var activity);
                    if (activity == ProductWatchActivity.Pending) pending ??= DateTimeOffset.UtcNow;
                    var latest = registry.RunHistory(job.Id).Runs.FirstOrDefault(run => run.Intent.Sequence > after && !run.Running && run.Result is not null);
                    if (latest is not null) return (latest, pending);
                    await Task.Delay(100, timeout.Token);
                }
            }

            async Task WaitForWatching()
            {
                using var timeout = new CancellationTokenSource(files == 4096 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5));
                while (true)
                {
                    if (monitor.HasExited) throw new InvalidOperationException("The monitor exited before returning to idle.");
                    registry.ReadMonitorProcess(job.Id, out var activity);
                    if (activity == ProductWatchActivity.Watching) return;
                    await Task.Delay(100, timeout.Token);
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
                foreach (var workspace in new[] { destination, fixture })
                {
                    string root = Path.GetFullPath(workspace.Root);
                    string parent = workspace == destination ? @"D:\" : Path.TrimEndingDirectorySeparator(Path.GetTempPath());
                    if (!string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase) ||
                        !Path.GetFileName(root).StartsWith(LabWorkspace.Prefix, StringComparison.Ordinal))
                        throw new IOException("The cleanup target left the owned workspace parent.");
                    using (workspace.OpenRegisteredRoot()) { }
                    Directory.Delete(root, recursive: true);
                }
            }
            else Console.Error.WriteLine($"Synthetic fixture preserved: {fixture.Root}, {destination.Root}.");
        }

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }
}
