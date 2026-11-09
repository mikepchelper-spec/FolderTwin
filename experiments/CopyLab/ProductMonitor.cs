using System.Diagnostics;
using System.IO;

namespace CopyLab;

internal static class ProductMonitor
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

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

    internal static async Task<int> RunAsync(string location, bool scheduled = false)
    {
        using var registry = new ProductJobRegistry(location);
        if (scheduled && !ProductAutomation.CanMonitor(registry)) return 0;
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
            var jobs = new Dictionary<Guid, Watch>();
            Process? worker = null;
            Watch? active = null;
            long dispatch = 0;
            long checkedOwnerAt = Stopwatch.GetTimestamp();
            try
            {
                while (true)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (scheduled && Stopwatch.GetElapsedTime(checkedOwnerAt, now) >= TimeSpan.FromSeconds(5))
                    {
                        if (!ProductAutomation.CanMonitor(registry)) return 0;
                        checkedOwnerAt = now;
                    }
                    if (worker is not null && worker.HasExited)
                    {
                        active!.Complete(worker.ExitCode, now);
                        worker.Dispose();
                        worker = null;
                        active = null;
                    }

                    ProductJobList listing;
                    try { listing = registry.List(); }
                    catch (Exception error) when (VersionJob.SharingViolation(error))
                    {
                        await Task.Delay(Poll).ConfigureAwait(false);
                        continue;
                    }
                    var configured = listing.Jobs.Where(job => job.Enabled && !job.Retired &&
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
                            jobs.Add(job.Id, watch = new Watch(now));
                        watch.Observe(job.Source, now);
                    }
                    if (worker is null)
                    {
                        foreach (var entry in jobs.OrderBy(entry => entry.Value.LastDispatch))
                        {
                            var watch = entry.Value;
                            if (!watch.TakeDue(now)) continue;
                            if (scheduled && !ProductAutomation.CanMonitor(registry)) return 0;
                            watch.LastDispatch = ++dispatch;
                            try
                            {
                                worker = ProductWorker.StartAutomatic(registry.Location, entry.Key);
                                active = watch;
                            }
                            catch (Exception error) when (VersionJob.Expected(error)) { watch.Complete(2, now); }
                            break;
                        }
                    }
                    ProductJobRegistry.WriteMonitorHeartbeat(lease,
                        jobs.Select(entry => (entry.Key, entry.Value.Activity(active == entry.Value))));
                    await Task.Delay(Poll).ConfigureAwait(false);
                }
            }
            finally
            {
                worker?.Dispose();
                foreach (var watch in jobs.Values) watch.Dispose();
            }
        }
    }

    private sealed class Watch(long now) : IDisposable
    {
        private readonly ProductWatchSignal signal = new(TimeProvider.System);
        private FileSystemWatcher? watcher;
        private long reconcileAt = now;
        private long observeAt = now;
        private long retryAt = now;
        private int failures;
        internal long LastDispatch { get; set; }

        internal void Observe(LocalJobRoot source, long now)
        {
            if (signal.TakeWatcherFailure() || now >= reconcileAt)
            {
                Dispose();
                signal.RequireReconciliation();
                reconcileAt = After(now, ReconcileInterval);
            }
            if (watcher is not null || now < observeAt) return;
            try { watcher = signal.Observe(source); }
            catch (Exception error) when (VersionJob.Expected(error))
            {
                observeAt = After(now, TimeSpan.FromSeconds(5));
            }
        }

        internal bool TakeDue(long now) => now >= retryAt && signal.TakeDue(Quiet, MaximumWait);
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
            retryAt = After(now, exitCode == 1 ? ReconcileInterval : TimeSpan.FromSeconds(5 << (failures - 1)));
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
