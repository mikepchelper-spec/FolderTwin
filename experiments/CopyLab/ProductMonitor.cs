using System.Diagnostics;
using System.IO;
using System.Threading.Channels;

namespace CopyLab;

internal static class ProductMonitor
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConfigurationPoll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OwnershipPoll = TimeSpan.FromMinutes(1);

    internal static async Task<int> SuperviseAsync(string location)
    {
        using var registry = new ProductJobRegistry(location);
        for (int failures = 0; ; failures++)
        {
            if (!ProductAutomation.CanMonitor(registry)) return 0;
            long started = Stopwatch.GetTimestamp();
            using var monitor = ProductWorker.StartScheduledMonitor(registry.Location);
            await monitor.WaitForExitAsync().ConfigureAwait(false);
            if (monitor.ExitCode == 0 || !ProductAutomation.CanMonitor(registry)) return 0;
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMinutes(10)) failures = 0;
            if (failures == 3) return 1;
            await Task.Delay(TimeSpan.FromSeconds(5 << failures)).ConfigureAwait(false);
        }
    }

    internal static async Task<int> RunAsync(string location, bool scheduled = false,
        Func<ProductWatchSignal, LocalJobRoot, FileSystemWatcher>? observe = null)
    {
        using var registry = new ProductJobRegistry(location);
        using var scheduler = scheduled ? new WindowsSchedule() : null;
        string? verifiedDefinition = null;
        if (scheduler is not null && !scheduler.CanMonitor(registry, ref verifiedDefinition)) return 0;
        FileStream lease;
        try { lease = registry.AcquireMonitorLock(); }
        catch (Exception error) when (VersionJob.SharingViolation(error))
        {
            // A health reader can briefly probe the lease just as the task starts.
            await Task.Delay(100).ConfigureAwait(false);
            try { lease = registry.AcquireMonitorLock(); }
            catch (Exception collision) when (VersionJob.SharingViolation(collision)) { return 0; }
        }
        using (lease)
        {
            var timing = registry.ReadWatchTiming();
            var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite
            });
            void Wake() => wake.Writer.TryWrite(true);
            int configurationChanged = 1;
            int registrationChanged = 0;
            void ConfigurationChanged(string? name)
            {
                if (name == ProductJobRegistry.MonitorRegistrationSignal)
                    Interlocked.Exchange(ref registrationChanged, 1);
                else
                    Interlocked.Exchange(ref configurationChanged, 1);
                Wake();
            }
            using var configurationWatch = new FileSystemWatcher(registry.Location, "*.json")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
            };
            configurationWatch.Created += (_, change) => ConfigurationChanged(change.Name);
            configurationWatch.Changed += (_, change) => ConfigurationChanged(change.Name);
            configurationWatch.Deleted += (_, change) => ConfigurationChanged(change.Name);
            configurationWatch.Renamed += (_, change) => ConfigurationChanged(change.Name);
            configurationWatch.Error += (_, _) =>
            {
                Interlocked.Exchange(ref registrationChanged, 1);
                ConfigurationChanged(null);
            };
            configurationWatch.EnableRaisingEvents = true;
            var jobs = new Dictionary<Guid, Watch>();
            Process? worker = null;
            Watch? active = null;
            long dispatch = 0;
            long checkedOwnerAt = Stopwatch.GetTimestamp();
            long? loadedAt = null;
            long? lastHeartbeat = null;
            (Guid Job, ProductWatchActivity Activity)[] priorActivity = [];
            ProductJobList? listing = null;
            try
            {
                while (true)
                {
                    wake.Reader.TryRead(out _);
                    long now = Stopwatch.GetTimestamp();
                    if (scheduler is not null && (Interlocked.Exchange(ref registrationChanged, 0) != 0 ||
                        Stopwatch.GetElapsedTime(checkedOwnerAt, now) >= OwnershipPoll))
                    {
                        if (!scheduler.CanMonitor(registry, ref verifiedDefinition)) return 0;
                        checkedOwnerAt = now;
                    }
                    if (worker is not null && worker.HasExited)
                    {
                        active!.Complete(worker.ExitCode, now);
                        worker.Dispose();
                        worker = null;
                        active = null;
                    }

                    if (Interlocked.Exchange(ref configurationChanged, 0) != 0 || loadedAt is null ||
                        Stopwatch.GetElapsedTime(loadedAt.Value, now) >= ConfigurationPoll)
                    {
                        try
                        {
                            listing = registry.List();
                            loadedAt = now;
                        }
                        catch (Exception error) when (VersionJob.SharingViolation(error))
                        {
                            Interlocked.Exchange(ref configurationChanged, 1);
                            await Task.Delay(Poll).ConfigureAwait(false);
                            continue;
                        }
                    }
                    var configured = listing!.Jobs.Where(job => job.Enabled && !job.Retired &&
                        job.WatchMode == ProductWatchMode.Watching).ToDictionary(job => job.Id);
                    foreach (var id in jobs.Keys.Where(id => !configured.ContainsKey(id)).ToArray())
                    {
                        jobs[id].Dispose();
                        jobs.Remove(id);
                    }
                    if (configured.Count == 0 && worker is null) return listing.Unreadable == 0 ? 0 : 1;
                    foreach (var job in configured.Values)
                    {
                        if (!jobs.TryGetValue(job.Id, out var watch))
                            jobs.Add(job.Id, watch = new Watch(now, Wake, timing, observe));
                        watch.Observe(job.Source, now);
                    }
                    if (worker is null)
                    {
                        foreach (var entry in jobs.OrderBy(entry => entry.Value.LastDispatch))
                        {
                            var watch = entry.Value;
                            if (!watch.TakeDue(now, out var editHints)) continue;
                            if (scheduler is not null && !scheduler.CanMonitor(registry, ref verifiedDefinition, verifyInstallation: true))
                                return 0;
                            watch.LastDispatch = ++dispatch;
                            try
                            {
                                worker = ProductWorker.StartAutomatic(registry.Location, entry.Key, editHints);
                                active = watch;
                            }
                            catch (Exception error) when (VersionJob.Expected(error)) { watch.Complete(2, now); }
                            break;
                        }
                    }
                    var activity = jobs.Select(entry => (entry.Key, entry.Value.Activity(active == entry.Value))).ToArray();
                    if (lastHeartbeat is null || Stopwatch.GetElapsedTime(lastHeartbeat.Value, now) >= HeartbeatInterval ||
                        !activity.AsSpan().SequenceEqual(priorActivity))
                    {
                        ProductJobRegistry.WriteMonitorHeartbeat(lease, activity);
                        lastHeartbeat = now;
                        priorActivity = activity;
                    }
                    if (worker is not null || jobs.Values.Any(watch => watch.NeedsPolling))
                        await Task.Delay(Poll).ConfigureAwait(false);
                    else
                    {
                        using var timeout = new CancellationTokenSource(HeartbeatInterval);
                        try { await wake.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
                    }
                }
            }
            finally
            {
                worker?.Dispose();
                foreach (var watch in jobs.Values) watch.Dispose();
            }
        }
    }

    private sealed class Watch(long now, Action wake, ProductWatchTiming timing,
        Func<ProductWatchSignal, LocalJobRoot, FileSystemWatcher>? observe) : IDisposable
    {
        private readonly ProductWatchSignal signal = new(TimeProvider.System, wake);
        private FileSystemWatcher? watcher;
        private long reconcileAt = now;
        private long observeAt = now;
        private long retryAt = now;
        private int failures;
        private readonly TimeSpan quiet = TimeSpan.FromSeconds(timing.QuietSeconds);
        private readonly TimeSpan maximumWait = TimeSpan.FromSeconds(timing.MaximumWaitSeconds);
        private readonly TimeSpan reconcileInterval = TimeSpan.FromSeconds(timing.ReconcileSeconds);
        internal long LastDispatch { get; set; }
        internal bool NeedsPolling => watcher is null || signal.Pending;

        internal void Observe(LocalJobRoot source, long now)
        {
            if (signal.TakeWatcherFailure() || now >= reconcileAt)
            {
                Dispose();
                signal.RequireReconciliation();
                reconcileAt = After(now, reconcileInterval);
            }
            if (watcher is not null || now < observeAt) return;
            try { watcher = observe is null ? signal.Observe(source) : observe(signal, source); }
            catch (Exception error) when (VersionJob.Expected(error))
            {
                observeAt = After(now, TimeSpan.FromSeconds(5));
            }
        }

        internal bool TakeDue(long now, out string[]? editHints)
        {
            editHints = null;
            return now >= retryAt && signal.TakeDue(quiet, maximumWait, out editHints);
        }
        internal ProductWatchActivity Activity(bool running) => running ? ProductWatchActivity.Running :
            watcher is null ? ProductWatchActivity.SourceUnavailable :
            signal.Pending ? ProductWatchActivity.Pending : ProductWatchActivity.Watching;

        internal void Complete(int exitCode, long now)
        {
            if (exitCode == 0)
            {
                failures = 0;
                return;
            }
            if (exitCode == 3) Dispose();
            failures = Math.Min(failures + 1, 5);
            retryAt = After(now, exitCode == 1 ? reconcileInterval : TimeSpan.FromSeconds(5 << (failures - 1)));
            signal.RequireReconciliation();
        }

        public void Dispose()
        {
            watcher?.Dispose();
            watcher = null;
        }

        private static long After(long timestamp, TimeSpan delay) =>
            timestamp + (long)(delay.TotalSeconds * Stopwatch.Frequency);
    }
}
