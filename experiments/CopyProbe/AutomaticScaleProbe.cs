using System.Diagnostics;

internal static class AutomaticScaleProbe
{
    internal static int Run(int files, ProductBackupMode mode)
    {
        var workspace = LabWorkspace.Create();
        using var process = Process.GetCurrentProcess();
        try
        {
            using (var root = workspace.OpenRegisteredRoot())
                LabWorkspace.RequireSpace(256L * 1024 * 1024 + 20L * 1024 * files, LabWorkspace.AvailableSpace(root.Root));
            string source = Folder("source"), destinations = Folder("destinations"), settings = Folder("settings");
            Console.WriteLine($"Automatic {mode}: {files:N0} files; Windows {Environment.OSVersion.Version}; .NET {Environment.Version}.");
            string last = "";
            Measure("seed", () =>
            {
                for (int i = 0; i < files; i += 256)
                {
                    string group = Directory.CreateDirectory(Path.Combine(source, $"group-{i / 256:D4}")).FullName;
                    for (int j = i; j < Math.Min(i + 256, files); j++)
                    {
                        last = Path.Combine(group, $"file-{j:D6}.txt");
                        File.WriteAllText(last, $"synthetic {j % 32:D2}");
                    }
                }
            });
            using var registry = new ProductJobRegistry(settings);
            var job = registry.Create(new("Synthetic automatic scale", mode, null),
                LocalJobRoots.ReviewDedicatedDestination(source, destinations));
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching,
                authorizeMirror: mode == ProductBackupMode.ProtectedMirror);
            ProductRunResult? latest = null;
            Attempt("initial", ProductRunState.Confirmed);
            Attempt("unchanged", ProductRunState.Unchanged);
            DateTime previousTime = File.GetLastWriteTimeUtc(last);
            File.WriteAllText(last, "synthetic XX");
            File.SetLastWriteTimeUtc(last, previousTime);
            Attempt("single_edit", ProductRunState.Confirmed);
            string relative = Path.GetRelativePath(source, last);
            Directory.Move(source, Path.Combine(workspace.Root, "source-detached"));
            string restored = Folder("restore");
            Measure("restore_one", () => VersionStore.OpenProduct(job).Restore(latest!.Generation!, restored, relative));
            if (File.ReadAllText(Path.Combine(restored, relative)) != "synthetic XX")
                throw new InvalidOperationException("The automatic large-tree point did not recover the changed bytes.");
            Console.WriteLine("PASS automatic scale, no-op suppression and same-size same-time edit recovery.");
            return 0;

            void Attempt(string phase, ProductRunState expected) => Measure(phase, () =>
            {
                latest = ProductRunner.Run(registry, job.Id, automatic: true);
                if (latest.State != expected)
                    throw new InvalidOperationException($"{phase}: expected {expected}, got {latest.State}. {latest.Detail}");
            });
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(workspace.Root, name)).FullName;
        void Measure(string phase, Action action)
        {
            var before = VersionScaleProbe.ReadCounters(process);
            TimeSpan cpu = process.TotalProcessorTime;
            var timer = Stopwatch.StartNew();
            action();
            var after = VersionScaleProbe.ReadCounters(process);
            process.Refresh();
            Console.WriteLine(FormattableString.Invariant(
                $"{phase}: wall_s={timer.Elapsed.TotalSeconds:F3}, cpu_s={(process.TotalProcessorTime - cpu).TotalSeconds:F3}, read_bytes={after.ReadBytes - before.ReadBytes}, write_bytes={after.WriteBytes - before.WriteBytes}, peak_working_set_bytes={process.PeakWorkingSet64}"));
        }
    }
}
