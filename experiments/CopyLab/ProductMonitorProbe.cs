using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace CopyLab;

internal static class ProductMonitorProbe
{
    internal static async Task RunScheduledAsync(string? previousPackage = null, bool soak = false)
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
                var openedPackage = ProductIdentity.ReadPackage(Environment.ProcessPath);
                if (openedPackage is null || ProductIdentity.ReadPackage(installed) != openedPackage)
                    throw new InvalidDataException("The opened executable and the installed worker did not identify the same verified package.");
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
                    if (soak)
                    {
                        await ReportIdleAsync(process, seconds: 120, label: "Native monitor");
                        if (registry.RunHistory(job.Id).Runs.Any(run => run.Intent.Sequence > previous))
                            throw new InvalidOperationException("The native monitor ran a backup while idle.");
                    }
                    process.Kill();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(timeout.Token);
                }
                File.WriteAllText(file, "saved while offline");
                await WaitAsync(() => registry.ReadMonitorProcess() is { } restarted && restarted != pid &&
                    registry.RunHistory(job.Id).Runs.Any(run => run.Intent.Sequence > previous &&
                        run.Result?.State == ProductRunState.Confirmed && !run.Running),
                    "Windows did not restart the failed monitor and reconcile the offline edit.", TimeSpan.FromMinutes(2));
                var last = registry.RunHistory(job.Id).Runs[0];
                string expected = "saved while offline";
                if (previousPackage is not null)
                {
                    string current = LabInstallation.EnsureWorker(product: true);
                    string previousVersion = LabInstallation.EnsureAt(previousPackage, LabInstallation.ProductInstallations);
                    if (string.Equals(previousVersion, current, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The update probe requires two different packages.");
                    var older = ProductIdentity.ReadPackage(previousVersion);
                    if (older is null || older.ManifestHash == openedPackage.ManifestHash)
                        throw new InvalidDataException("The previous worker did not identify a distinct package.");
                    scheduler.RemoveMonitor(registry);
                    await WaitAsync(() => !registry.MonitorOwnsRegistry(), "The initial monitor did not release the registry.");
                    scheduler.RegisterMonitor(registry, previousVersion, atLogon: true);
                    scheduler.RunMonitorNow(registry);
                    await WaitAsync(() => OwnsMonitor(previousVersion) && registry.RunHistory(job.Id).Runs.Any(run =>
                        run.Intent.Sequence > last.Intent.Sequence && !run.Running &&
                        run.Result?.State is ProductRunState.Confirmed or ProductRunState.Unchanged),
                        "The previous package did not start monitoring and reconcile the existing job.");
                    string updated = ProductDeployment.Install(registry, registry.ReviewMaintenance());
                    if (updated != current || scheduler.ReadMonitor(registry) is not { AtLogon: true, MeetsPolicy: true })
                        throw new InvalidOperationException("The update did not preserve the monitor policy.");
                    await WaitAsync(() => OwnsMonitor(current), "The updated package did not take over monitoring.");
                    expected = "saved after an actual package update";
                    File.WriteAllText(file, expected);
                    await WaitAsync(() => registry.RunHistory(job.Id).Runs.Any(run => run.Intent.Sequence > last.Intent.Sequence &&
                        run.Result?.State == ProductRunState.Confirmed && !run.Running && run.Generation != last.Generation),
                        "The upgraded monitor did not back up the new edit.");
                    last = registry.RunHistory(job.Id).Runs.First(run => run.Result?.State == ProductRunState.Confirmed);
                    ProductDeployment.RemoveSchedules(registry, registry.ReviewMaintenance());
                    if (registry.Get(job.Id).Enabled || scheduler.ReadMonitor(registry) is not null)
                        throw new InvalidOperationException("Withdrawal did not disable the job and remove the monitor.");
                    Console.Error.WriteLine("PASS previous portable package, native active monitor handover, preserved logon option and withdrawal.");
                }
                else scheduler.RemoveMonitor(registry);
                await WaitAsync(() => registry.ReadMonitorProcess() is null, "Removing the task did not stop future dispatch.");
                Directory.Move(source, Path.Combine(fixture.Root, "offline"));
                string restore = Directory.CreateDirectory(Path.Combine(fixture.Root, "restore")).FullName;
                VersionStore.OpenProduct(job).Restore(last.Generation!, restore);
                if (File.ReadAllText(Path.Combine(restore, "synthetic.txt")) != expected)
                    throw new InvalidOperationException("The recovered monitor did not capture the expected bytes.");

                bool OwnsMonitor(string path)
                {
                    if (registry.ReadMonitorProcess() is not { } id) return false;
                    try
                    {
                        using var process = Process.GetProcessById(id);
                        return string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase);
                    }
                    catch (ArgumentException) { return false; }
                }
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
        while (timer.Elapsed < (timeout ?? TimeSpan.FromSeconds(35)));
        throw new InvalidOperationException(failure);
    }

    private static async Task ReportIdleAsync(Process monitor, int seconds, string label)
    {
        var before = VersionScaleProbe.ReadCounters(monitor);
        TimeSpan cpu = monitor.TotalProcessorTime;
        var idle = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        monitor.Refresh();
        var after = VersionScaleProbe.ReadCounters(monitor);
        Console.Error.WriteLine(FormattableString.Invariant(
            $"{label} idle: seconds={idle.Elapsed.TotalSeconds:F2}; one_core_pct={(monitor.TotalProcessorTime - cpu).TotalSeconds * 100 / idle.Elapsed.TotalSeconds:F2}; working_set_bytes={monitor.WorkingSet64}; read_bytes={after.ReadBytes - before.ReadBytes}; write_bytes={after.WriteBytes - before.WriteBytes}."));
    }

    internal static async Task RunAsync(bool soak = false, string? destinationVolume = null)
    {
        if (destinationVolume is not null && string.Equals(Path.GetPathRoot(Path.GetTempPath()),
            Path.GetFullPath(destinationVolume), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The cross-volume probe requires a destination distinct from the temporary source volume.");
        var destination = destinationVolume is null ? null : LabWorkspace.CreateOnFixedVolume(destinationVolume);
        try
        {
            foreach (var mode in Enum.GetValues<ProductBackupMode>())
                await RunModeAsync(mode, soak, destination?.Root);
            if (!soak) await VerifyCloudIsolationAsync(destination?.Root);
        }
        finally
        {
            if (destination is not null)
            {
                using (destination.OpenRegisteredRoot()) { }
                Directory.Delete(destination.Root, recursive: true);
            }
        }
        Console.Error.WriteLine("PASS headless monitor and workers, single monitor, automatic change, destination return, durable pause and offline recovery for all modes.");
    }

    private static async Task VerifyCloudIsolationAsync(string? destinationRoot)
    {
        var fixture = LabWorkspace.Create();
        string settings = Folder(fixture.Root, "settings");
        string destinations = Folder(destinationRoot ?? fixture.Root, "cloud-isolation");
        using (var registry = new ProductJobRegistry(settings))
        {
            var damaged = CreateJob("cloud-damaged");
            var healthy = CreateJob("local-healthy");
            string repository = new('a', 64);
            registry.SaveCloudConnection(damaged.Id,
                new(repository, new(CloudProvider.CompatibleS3, "127.0.0.1:1", "us-east-1", "foldertwin")),
                new("synthetic-password", "synthetic-key", "synthetic-secret"), lab: true);
            var previous = ProductRunner.Run(registry, damaged.Id);
            if (previous is not { State: ProductRunState.Confirmed, Generation: { } generation })
                throw new InvalidOperationException("The baseline point could not be created.");
            var ticket = registry.EnqueueCloud(damaged.Id, repository, generation);
            registry.RecordCloudConnectionFailure(ticket, CloudTransferIssue.Credentials, retry: false);
            var before = CloudJobStatus.Read(registry, damaged.Id, registry.RunHistory(damaged.Id));
            var tray = TraySnapshot.ReadProduct(registry);
            if (!before.RequiresReview || before.Issue != CloudTransferIssue.Credentials ||
                !tray.Attention.Contains($"{damaged.Id:N}-cloud-Credentials"))
                throw new InvalidOperationException("The local confirmation concealed the cloud credential problem.");

            string marker = Path.Combine(settings, damaged.Id.ToString("N"), "cloud-connection", "paused");
            File.WriteAllText(marker, "damaged marker");
            File.WriteAllText(Path.Combine(fixture.Root, "cloud-damaged", "sample.txt"), "edit after the cloud failure");
            foreach (var job in new[] { damaged, healthy })
                registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching);
            using var monitor = ProductWorker.StartMonitor(settings);
            try
            {
                await WaitAsync(() => !monitor.HasExited &&
                    registry.RunHistory(damaged.Id).Runs.Any(run => run.Result is { State: ProductRunState.Confirmed } &&
                        run.Generation != generation && !run.Running) &&
                    registry.RunHistory(healthy.Id).Runs.Any(run => run.Result?.State == ProductRunState.Confirmed && !run.Running),
                    "A damaged cloud pause blocked local confirmations.", TimeSpan.FromSeconds(15));
                var after = CloudJobStatus.Read(registry, damaged.Id, registry.RunHistory(damaged.Id));
                if (!after.RecordUnavailable || !after.RequiresReview ||
                    !TraySnapshot.ReadProduct(registry).Attention.Contains($"{damaged.Id:N}-cloud-None") ||
                    File.ReadAllText(marker) != "damaged marker")
                    throw new InvalidOperationException("The damaged cloud record was hidden or modified.");
            }
            finally
            {
                if (!monitor.HasExited) monitor.Kill(entireProcessTree: true);
                await monitor.WaitForExitAsync();
            }

            ProductJob CreateJob(string name)
            {
                string source = Folder(fixture.Root, name);
                File.WriteAllText(Path.Combine(source, "sample.txt"), name);
                return registry.Create(new(name, ProductBackupMode.Versions, null),
                    LocalJobRoots.ReviewDedicatedDestination(source, destinations));
            }
        }
        using (fixture.OpenRegisteredRoot()) { }
        Directory.Delete(fixture.Root, recursive: true);
        Console.Error.WriteLine("PASS cloud credential incidence remains visible and a damaged cloud pause does not stop either local backup.");

        static string Folder(string parent, string name) => Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
    }

    internal static async Task RunReconciliationAsync()
    {
        var fixture = LabWorkspace.Create();
        string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(fixture.Root, "destinations")).FullName;
        string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
        string file = Path.Combine(source, "observed.txt");
        File.WriteAllText(file, "first synthetic point");
        string timing = Path.Combine(settings, "monitor-timing.json");
        using (var registry = new ProductJobRegistry(settings))
        {
            File.WriteAllText(timing, """{"QuietSeconds":2,"MaximumWaitSeconds":2,"ReconcileSeconds":20}""");
            try
            {
                registry.ReadWatchTiming();
                throw new InvalidOperationException("An invalid timing policy was accepted.");
            }
            catch (InvalidDataException) { }
            File.WriteAllText(timing, """{"QuietSeconds":1,"MaximumWaitSeconds":5,"ReconcileSeconds":20}""");
            var job = registry.Create(new("Synthetic reconciliation", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, destination));
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching);
            ProductWatchSignal? suppressed = null;
            int attachments = 0;
            var monitor = ProductMonitor.RunAsync(settings, observe: (signal, root) =>
            {
                var watcher = signal.Observe(root);
                watcher.EnableRaisingEvents = false;
                Volatile.Write(ref suppressed, signal);
                Interlocked.Increment(ref attachments);
                return watcher;
            });
            try
            {
                var first = await WaitForPointAsync(0, TimeSpan.FromSeconds(15));
                File.WriteAllText(file, "saved without any watcher callback");
                var periodic = await WaitForPointAsync(first.Intent.Sequence, TimeSpan.FromSeconds(35));
                if (Volatile.Read(ref attachments) < 2)
                    throw new InvalidOperationException("Periodic reconciliation did not recreate the subscription.");
                int priorAttachments = Volatile.Read(ref attachments);
                File.WriteAllText(file, "saved before synthetic overflow");
                Volatile.Read(ref suppressed)!.RequireReconciliation(failedWatcher: true);
                var overflow = await WaitForPointAsync(periodic.Intent.Sequence, TimeSpan.FromSeconds(12));
                if (Volatile.Read(ref attachments) <= priorAttachments)
                    throw new InvalidOperationException("Injected overflow did not recreate the subscription.");

                Directory.Move(source, Path.Combine(fixture.Root, "offline"));
                var store = VersionStore.OpenProduct(job);
                foreach (var (point, expected) in new[] { (periodic, "saved without any watcher callback"),
                    (overflow, "saved before synthetic overflow") })
                {
                    string restore = Directory.CreateDirectory(Path.Combine(fixture.Root, "restore-" + point.Intent.Sequence)).FullName;
                    store.Restore(point.Generation!, restore);
                    if (File.ReadAllText(Path.Combine(restore, "observed.txt")) != expected)
                        throw new InvalidOperationException("A reconciliation restored unexpected content.");
                }
            }
            finally
            {
                registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Paused);
                if (await monitor.WaitAsync(TimeSpan.FromSeconds(15)) != 0)
                    throw new InvalidOperationException("The reconciliation monitor did not exit normally.");
            }

            async Task<ProductRunView> WaitForPointAsync(long after, TimeSpan timeout)
            {
                ProductRunView? point = null;
                await WaitAsync(() => (point = registry.RunHistory(job.Id).Runs.FirstOrDefault(run =>
                    run.Intent.Sequence > after && !run.Running && run.Result?.State == ProductRunState.Confirmed)) is not null,
                    "Reconciliation did not publish a point.", timeout);
                return point!;
            }
        }
        using (fixture.OpenRegisteredRoot()) { }
        Directory.Delete(fixture.Root, recursive: true);
        Console.Error.WriteLine("PASS periodic reconciliation with watcher callbacks suppressed and injected overflow restore exact offline bytes.");
    }

    private static async Task RunModeAsync(ProductBackupMode mode, bool soak, string? destinationRoot)
    {
        var workspace = LabWorkspace.Create();
        string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "source")).FullName;
        string parent = Directory.CreateDirectory(Path.Combine(destinationRoot ?? workspace.Root, $"destinations-{mode}")).FullName;
        string settings = Directory.CreateDirectory(Path.Combine(workspace.Root, "settings")).FullName;
        string file = Path.Combine(source, "synthetic.txt");
        File.WriteAllText(file, "first");
        File.WriteAllText(Path.Combine(source, "retired.txt"), "keep this point recoverable");
        if (mode != ProductBackupMode.Versions)
            for (int index = 0; index < 10; index++)
                File.WriteAllText(Path.Combine(source, $"baseline-{index:D2}.txt"), "stable synthetic baseline");
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
                if (mode == ProductBackupMode.Versions)
                {
                    await ReportIdleAsync(monitor, soak ? 120 : 15, "Monitor");
                    if (registry.RunHistory(job.Id).Runs.Length != 1)
                        throw new InvalidOperationException("The idle monitor dispatched a backup without a change.");
                }
                var changeAt = DateTimeOffset.UtcNow;
                var latency = Stopwatch.StartNew();
                File.WriteAllText(file, "second");
                var second = await WaitForRunAsync(first.Intent.Sequence, ProductRunState.Confirmed);
                if (mode != ProductBackupMode.Versions && second.Result is not { Scoped: true })
                    throw new InvalidOperationException("The native monitor did not forward the edit hint to the worker.");
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"{mode} edit: dispatch_seconds={(second.Intent.StartedUtc - changeAt).TotalSeconds:F3}; observed_confirmed_seconds={latency.Elapsed.TotalSeconds:F3}."));
                byte[]? largeHash = null;
                if (soak)
                {
                    DateTimeOffset burstStart = DateTimeOffset.UtcNow;
                    string burstFile = Path.Combine(source, "burst.txt");
                    string temporary = Path.Combine(source, "next-write.txt");
                    int edits = 0;
                    var burstClock = Stopwatch.StartNew();
                    DateTimeOffset lastEditAt;
                    do
                    {
                        File.WriteAllText(temporary, $"save {++edits}");
                        for (int retries = 0; ; retries++)
                        {
                            try
                            {
                                lastEditAt = DateTimeOffset.UtcNow;
                                File.Move(temporary, burstFile, overwrite: true);
                                break;
                            }
                            // A worker may hold either synthetic path against
                            // delete. Windows can report access denied on replacement.
                            catch (Exception error) when (retries < 40 && error is (IOException or UnauthorizedAccessException) &&
                                (error.HResult & 0xffff) is 5 or 32 or 33)
                            { await Task.Delay(25); }
                        }
                        await Task.Delay(350);
                    }
                    while (burstClock.Elapsed < TimeSpan.FromSeconds(23));
                    var attempts = registry.RunHistory(job.Id).Runs
                        .Where(run => run.Intent.Sequence > second.Intent.Sequence).OrderBy(run => run.Intent.Sequence).ToArray();
                    if (attempts.Length == 0 || attempts[0].Intent.StartedUtc - burstStart > TimeSpan.FromSeconds(25))
                        throw new InvalidOperationException("A continuous stream of saves starved the automatic worker.");
                    var finalBurst = await WaitForRunAsync(second.Intent.Sequence, ProductRunState.Confirmed, since: lastEditAt);
                    string burstOutput = Directory.CreateDirectory(Path.Combine(workspace.Root, "burst-restore")).FullName;
                    VersionStore.OpenProduct(job).Restore(finalBurst.Generation!, burstOutput, "burst.txt");
                    if (File.ReadAllText(Path.Combine(burstOutput, "burst.txt")) != $"save {edits}")
                        throw new InvalidOperationException("The trailing save was lost during a backup.");
                    Console.Error.WriteLine(FormattableString.Invariant(
                        $"{mode} burst: edits={edits}; dispatch_seconds={(attempts[0].Intent.StartedUtc - burstStart).TotalSeconds:F3}; final_bytes_restored=true."));
                    second = finalBurst;

                    string large = Path.Combine(source, "large.bin");
                    byte[] block = new byte[1024 * 1024];
                    var random = new Random(2026);
                    using (var largeOutput = new FileStream(large, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        for (int part = 0; part < 32; part++)
                        {
                            random.NextBytes(block);
                            largeOutput.Write(block);
                        }
                    using (var input = File.OpenRead(large)) largeHash = SHA256.HashData(input);
                    second = await WaitForRunAsync(second.Intent.Sequence, ProductRunState.Confirmed, timeout: TimeSpan.FromSeconds(75));
                }
                string detached = Path.Combine(Path.GetDirectoryName(job.Destination.LastVolumePath)!, "detached");
                Directory.Move(job.Destination.LastVolumePath, detached);
                string expected = "after reconnection";
                File.WriteAllText(file, expected);
                File.Delete(Path.Combine(source, "retired.txt"));
                var absent = await WaitForRunAsync(second.Intent.Sequence, ProductRunState.WaitingForDestination);
                Directory.Move(detached, job.Destination.LastVolumePath);
                var returned = await WaitForRunAsync(absent.Intent.Sequence, ProductRunState.Confirmed);
                if (mode == ProductBackupMode.Accumulative)
                {
                    expected = "edited with a destination-only file";
                    File.WriteAllText(file, expected);
                    returned = await WaitForRunAsync(returned.Intent.Sequence, ProductRunState.Confirmed);
                    if (returned.Result is not { Scoped: true })
                        throw new InvalidOperationException("The native monitor stopped using scoped edits after retaining a destination-only file.");
                }
                if (mode == ProductBackupMode.Versions)
                {
                    string otherSource = Directory.CreateDirectory(Path.Combine(workspace.Root, "second-source")).FullName;
                    File.WriteAllText(Path.Combine(otherSource, "other.txt"), "another independent job");
                    var other = registry.Create(new("Second synthetic monitor job", ProductBackupMode.Versions, null),
                        LocalJobRoots.ReviewDedicatedDestination(otherSource, parent));
                    using (var held = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.Read))
                    {
                        held.WriteByte((byte)'X');
                        held.Flush();
                        File.WriteAllText(Path.Combine(source, "stable.txt"), "change while a different file is held open");
                        await WaitForRunAsync(returned.Intent.Sequence, ProductRunState.Busy);
                        registry.UpdateSettings(other.Id, other.Options.Name, null, true, false, ProductWatchMode.Watching);
                        var otherPoint = await WaitForRunAsync(0, ProductRunState.Confirmed, other.Id);
                        if (VersionStore.OpenProduct(other).Verify(otherPoint.Generation!))
                            throw new InvalidOperationException("An occupied job prevented a verified point for another job.");
                        registry.UpdateSettings(other.Id, other.Options.Name, null, true, false, ProductWatchMode.Paused);
                    }
                    var last = registry.RunHistory(job.Id).Runs[0];
                    byte[] marker = File.ReadAllBytes(Path.Combine(job.Destination.LastVolumePath, DestinationBinding.MarkerName));
                    Directory.Move(job.Destination.LastVolumePath, detached);
                    try
                    {
                        Directory.CreateDirectory(job.Destination.LastVolumePath);
                        string copiedMarker = Path.Combine(job.Destination.LastVolumePath, DestinationBinding.MarkerName);
                        File.WriteAllBytes(copiedMarker, marker);
                        File.WriteAllText(Path.Combine(source, "notice.txt"), "change during a destination swap");
                        await WaitForRunAsync(last.Intent.Sequence, ProductRunState.Review);
                        if (!Directory.EnumerateFileSystemEntries(job.Destination.LastVolumePath).SequenceEqual([copiedMarker]) ||
                            !File.ReadAllBytes(copiedMarker).AsSpan().SequenceEqual(marker))
                            throw new InvalidOperationException("The automatic worker wrote to the substituted destination.");
                        File.Delete(copiedMarker);
                        Directory.Delete(job.Destination.LastVolumePath);
                    }
                    finally { Directory.Move(detached, job.Destination.LastVolumePath); }
                }
                registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Paused);
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    await monitor.WaitForExitAsync(timeout.Token);
                if (monitor.ExitCode != 0) throw new InvalidOperationException("The paused monitor did not stop cleanly.");

                Directory.Move(source, Path.Combine(workspace.Root, "detached-source"));
                string output = Directory.CreateDirectory(Path.Combine(workspace.Root, "restore")).FullName;
                var store = VersionStore.OpenProduct(job);
                store.Restore(returned.Generation!, output);
                if (File.ReadAllText(Path.Combine(output, "synthetic.txt")) != expected)
                    throw new InvalidOperationException("The automatic point did not restore the expected bytes without its source.");
                if (File.Exists(Path.Combine(output, "retired.txt")) != (mode == ProductBackupMode.Accumulative))
                    throw new InvalidOperationException("The automatic point handled retirement differently from its selected mode.");
                if (largeHash is not null)
                {
                    using var input = File.OpenRead(Path.Combine(output, "large.bin"));
                    if (input.Length != 32 * 1024 * 1024 || !SHA256.HashData(input).AsSpan().SequenceEqual(largeHash))
                        throw new InvalidOperationException("The automatic point did not restore the large file with matching bytes.");
                    Console.Error.WriteLine($"{mode} large file: restored_bytes={input.Length}; hash_matches=true.");
                }
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

            async Task<ProductRunView> WaitForRunAsync(long after, ProductRunState state, Guid? jobId = null,
                DateTimeOffset? since = null, TimeSpan? timeout = null)
            {
                var timer = Stopwatch.StartNew();
                ProductRunHistory? observed = null;
                while (timer.Elapsed < (timeout ?? TimeSpan.FromSeconds(30)))
                {
                    if (monitor.HasExited) throw new InvalidOperationException($"The monitor stopped with exit code {monitor.ExitCode}.");
                    observed = registry.RunHistory(jobId ?? job.Id);
                    var completed = observed.Runs.FirstOrDefault(run =>
                        run.Intent.Sequence > after && (since is null || run.Intent.StartedUtc >= since.Value) &&
                        !run.Running && run.Result?.State == state);
                    if (completed is not null) return completed;
                    await Task.Delay(100);
                }
                throw new InvalidOperationException($"Waiting for {state} after sequence {after}. Observed: " +
                    string.Join(", ", observed?.Runs.Take(4).Select(run => $"{run.Intent.Sequence}:{run.Result?.State}/{run.Running}") ?? []));
            }
        }
        using (workspace.OpenRegisteredRoot()) { }
        Directory.Delete(workspace.Root, recursive: true);
    }
}
