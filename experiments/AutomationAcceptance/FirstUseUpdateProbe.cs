using System.Diagnostics;
using System.Security.Cryptography;
using CopyLab;

internal static class FirstUseUpdateProbe
{
    internal static async Task RunAsync(string previous, string current, bool withdrawNew)
    {
        var fixture = LabWorkspace.Create();
        bool completed = false;
        try
        {
            string applications = LabInstallation.ProductInstallations;
            string oldFingerprint = Fingerprint(previous), newFingerprint = Fingerprint(current);
            string oldWorker = Path.Combine(applications, oldFingerprint, "CopyLab.exe");
            string newWorker = Path.Combine(applications, newFingerprint, "CopyLab.exe");
            if (oldFingerprint == newFingerprint || !LabInstallation.IsInstalledWorker(oldWorker, product: true) ||
                Fingerprint(Path.GetDirectoryName(oldWorker)!) != oldFingerprint)
                throw new InvalidDataException("The previous verified package must already be installed.");
            if (withdrawNew && Directory.Exists(Path.GetDirectoryName(newWorker)))
                throw new InvalidOperationException("Withdrawal requires a new installation created exclusively by this probe.");
            using (var expected = File.OpenRead(Path.Combine(current, "CopyProbe.dll")))
            using (var observed = File.OpenRead(typeof(LabWorkspace).Assembly.Location))
                if (!SHA256.HashData(expected).AsSpan().SequenceEqual(SHA256.HashData(observed)))
                    throw new InvalidDataException("The harness core must come from the current package.");
            string source = Folder("source"), targets = Folder("destinations");
            const string filename = "muestra.txt";
            string input = Path.Combine(source, filename);
            File.WriteAllText(input, "primer punto sintético");
            byte[] firstHash = SHA256.HashData(File.ReadAllBytes(input));
            using var registry = new ProductJobRegistry(Folder("registry"));
            using var scheduler = new WindowsSchedule();
            var job = registry.Create(new("Actualización sintética", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, targets));
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching);
            try
            {
                scheduler.RegisterMonitor(registry, oldWorker, atLogon: true);
                scheduler.RunMonitorNow(registry);
                var (first, oldPid) = await WaitFor(oldWorker, 0);
                string installed = ProductDeployment.Install(registry, registry.ReviewMaintenance(),
                    () => LabInstallation.EnsureAt(current, applications));
                if (installed != newWorker || !LabInstallation.IsInstalledWorker(newWorker, product: true) ||
                    scheduler.ReadMonitor(registry) is not { AtLogon: true, MeetsPolicy: true })
                    throw new InvalidDataException("The update did not transfer the reviewed task and logon option.");
                var after = registry.Get(job.Id);
                if (!after.Enabled || after.WatchMode != ProductWatchMode.Watching || after.Options != job.Options ||
                    !after.Source.Identity.AsSpan().SequenceEqual(job.Source.Identity) || after.Destination.Marker != job.Destination.Marker ||
                    !registry.RunHistory(job.Id).Runs.Any(run => run.Intent.Id == first.Intent.Id && run.Generation == first.Generation))
                    throw new InvalidDataException("The upgrade did not preserve the job identity, settings and previous history.");
                File.WriteAllText(input, "cambio sintético después de actualizar");
                byte[] lastHash = SHA256.HashData(File.ReadAllBytes(input));
                var (last, newPid) = await WaitFor(newWorker, first.Intent.Sequence);
                if (first.Generation == last.Generation || oldPid == newPid || IsRunning(oldPid))
                    throw new InvalidDataException("The upgrade did not replace the old monitor with a worker that confirmed a new point.");
                ProductDeployment.RemoveSchedules(registry, registry.ReviewMaintenance());
                if (registry.Get(job.Id).Enabled || registry.MonitorOwnsRegistry() || scheduler.ReadMonitor(registry) is not null)
                    throw new InvalidDataException("Withdrawal left the synthetic job or monitor enabled.");
                try
                {
                    using var monitor = Process.GetProcessById(newPid);
                    await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
                catch (ArgumentException) { /* The stopped monitor has already exited. */ }
                if (withdrawNew)
                {
                    ProductDeployment.RemoveVersion(registry, registry.ReviewMaintenance(), applications,
                        LabInstallation.ReviewRemovalAt(applications, newFingerprint));
                    if (Directory.Exists(Path.GetDirectoryName(newWorker)) || !File.Exists(oldWorker))
                        throw new InvalidDataException("Withdrawal did not remove only the selected test installation.");
                    Console.WriteLine("PASS selected test installation removed through the reviewed product flow; previous installation preserved.");
                }
                Directory.Move(source, Path.Combine(fixture.Root, "offline"));
                var store = VersionStore.OpenProduct(job);
                Restore(first.Generation!, firstHash, "before");
                Restore(last.Generation!, lastHash, "after");
                Console.WriteLine("PASS real previous worker, native deployment handover, preserved job/history/logon, new backup and both offline restores after withdrawal.");
                completed = true;

                void Restore(string generation, byte[] expected, string folder)
                {
                    string output = Folder(folder);
                    store.Restore(generation, output, filename);
                    byte[] hash = SHA256.HashData(File.ReadAllBytes(Path.Combine(output, filename)));
                    if (!hash.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Restored bytes differ.");
                    Console.WriteLine($"{folder} sha256={Convert.ToHexStringLower(hash)}");
                }
            }
            finally { scheduler.RemoveMonitor(registry); }

            async Task<(ProductRunView Run, int Pid)> WaitFor(string worker, long after)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
                while (true)
                {
                    var task = scheduler.ReadMonitor(registry);
                    if (task is { Running: true } && string.Equals(task.Executable, worker, StringComparison.OrdinalIgnoreCase) &&
                        registry.ReadMonitorProcess() is { } pid)
                    {
                        try
                        {
                            using var monitor = Process.GetProcessById(pid);
                            if (string.Equals(monitor.MainModule?.FileName, worker, StringComparison.OrdinalIgnoreCase) &&
                                registry.RunHistory(job.Id).Runs.FirstOrDefault(run => run.Intent.Sequence > after &&
                                    run.Result?.State == ProductRunState.Confirmed) is { } observed)
                            {
                                Console.WriteLine($"Monitor PID {pid}, package {Path.GetFileName(Path.GetDirectoryName(worker))![..12]}, sequence {observed.Intent.Sequence}");
                                return (observed, pid);
                            }
                        }
                        catch (ArgumentException) { /* Heartbeat can still refer to the outgoing process. */ }
                    }
                    await Task.Delay(150, timeout.Token);
                }
            }
        }
        finally
        {
            if (completed) StorageGrowthProbe.RemoveWorkspace(fixture, Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
            else Console.Error.WriteLine($"Synthetic update fixture preserved at {fixture.Root}.");
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    private static string Fingerprint(string package) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(package, "SHA256SUMS.txt"))));

    private static bool IsRunning(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
