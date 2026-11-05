using System.Diagnostics;
using System.IO;

namespace CopyLab;

internal static class ProductScheduleProbe
{
    internal static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("The schedule probe requires a disposable hosted Windows runner.");
        var fixture = LabWorkspace.Create();
        string? executable = null;
        var jobs = new List<ProductJob>();
        string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
            string parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "backups")).FullName;
            File.WriteAllText(Path.Combine(source, "dato ñ.txt"), "contenido del horario de producto");
            using var registry = new ProductJobRegistry(settings);
            using var scheduler = new WindowsSchedule();
            executable = LabInstallation.EnsureWorker(product: true);
            Check(LabInstallation.EnsureWorker(product: true) == executable, "The verified installation was not reused.");
            foreach (var mode in Enum.GetValues<ProductBackupMode>())
            {
                var job = registry.Create(new(mode.ToString(), mode, new TimeOnly(20, 30)),
                    LocalJobRoots.ReviewDedicatedDestination(source, parent));
                jobs.Add(job);
                scheduler.RegisterProduct(registry, job.Id, executable, DateTime.Now.AddSeconds(5), daily: false);
                Check(scheduler.ReadProduct(registry, job.Id) is { Enabled: true, MeetsPolicy: true }, "The native task policy did not verify.");
                var attempt = await WaitAsync(job, 0);
                Check(attempt.Result?.State == ProductRunState.Confirmed &&
                    !VersionStore.OpenProduct(job).Verify(attempt.Generation!), "A real product time trigger failed to verify its backup.");
                scheduler.RegisterProduct(registry, job.Id, executable, DateTime.Today.AddDays(1).AddHours(20).AddMinutes(30));
                registry.UpdateSettings(job.Id, job.Options.Name, new TimeOnly(19, 30), true, false);
                Check(scheduler.ReadProduct(registry, job.Id) is { MeetsPolicy: false }, "A changed hour left a stale task runnable.");
                registry.UpdateSettings(job.Id, job.Options.Name, new TimeOnly(20, 30), true, false);
            }
            var versioned = jobs.Single(job => job.Options.Mode == ProductBackupMode.Versions);
            long sequence = registry.RunHistory(versioned.Id).Runs[0].Intent.Sequence;
            // Keep the same volume-GUID prefix: Directory.Move compares textual roots.
            string detached = Path.Combine(Path.GetDirectoryName(versioned.Destination.LastVolumePath)!, "detached");
            Directory.Move(versioned.Destination.LastVolumePath, detached);
            ProductRunView failed;
            try
            {
                scheduler.RegisterProduct(registry, versioned.Id, executable, DateTime.Now.AddSeconds(5), daily: false);
                failed = await WaitAsync(versioned, sequence, waiting: true);
                Check(failed.Result?.State == ProductRunState.WaitingForDestination && failed.RetryRunning,
                    "An absent target did not preserve its incident and live retry.");
                scheduler.RunProductNow(registry, versioned.Id);
                await Task.Delay(1000);
                Check(registry.RunHistory(versioned.Id).Runs[0].Intent.Sequence == failed.Intent.Sequence,
                    "IgnoreNew started another worker during a deferred retry.");
            }
            finally { Directory.Move(detached, versioned.Destination.LastVolumePath); }
            var retried = await WaitAsync(versioned, failed.Intent.Sequence, limit: TimeSpan.FromMinutes(7));
            Check(retried.Result?.State == ProductRunState.Confirmed &&
                retried.Result.FinishedUtc - failed.Result!.FinishedUtc >= TimeSpan.FromMinutes(4.5) &&
                registry.RunHistory(versioned.Id).Runs.Any(run => run.Intent.Id == failed.Intent.Id && run.Result == failed.Result && !run.RetryRunning),
                "The deferred retry lost its incident or did not run after the configured wait.");
            Console.Error.WriteLine($"PASS product time triggers run all three modes; missing target retries after {(retried.Result!.FinishedUtc - failed.Result!.FinishedUtc).TotalSeconds:F1} seconds and preserves the incident.");

            Directory.Move(versioned.Destination.LastVolumePath, detached);
            try
            {
                scheduler.RegisterProduct(registry, versioned.Id, executable, DateTime.Now.AddSeconds(5), daily: false);
                var waiting = await WaitAsync(versioned, retried.Intent.Sequence, waiting: true);
                // Remove first: disabling must not leave a native schedule that can reactivate work.
                scheduler.RemoveProduct(registry, versioned.Id);
                registry.UpdateSettings(versioned.Id, "Deshabilitado", new TimeOnly(20, 30), false, false);
                await WaitReleasedAsync(registry, versioned.Id);
                Check(registry.RunHistory(versioned.Id).Runs[0].Intent.Id == waiting.Intent.Id &&
                    !registry.RunHistory(versioned.Id).Runs[0].RetryRunning && scheduler.ReadProduct(registry, versioned.Id) is null,
                    "Disabling a job failed to stop its deferred retry.");
            }
            finally { Directory.Move(detached, versioned.Destination.LastVolumePath); }
            Console.Error.WriteLine("PASS changing the hour detects stale policy; removing and disabling stops a live retry without deleting backup data.");

            async Task<ProductRunView> WaitAsync(ProductJob job, long after, bool waiting = false, TimeSpan? limit = null)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < (limit ?? TimeSpan.FromMinutes(2)))
                {
                    var latest = registry.RunHistory(job.Id).Runs.FirstOrDefault();
                    if (latest is { Result: not null } && latest.Intent.Sequence > after &&
                        (waiting ? latest.RetryRunning : scheduler.ReadProduct(registry, job.Id) is { Running: false })) return latest;
                    await Task.Delay(250);
                }
                throw new InvalidOperationException("The product task did not reach its expected result.");
            }
        }
        finally
        {
            using (var registry = new ProductJobRegistry(settings))
            using (var scheduler = new WindowsSchedule())
            {
                foreach (var job in jobs)
                {
                    scheduler.RemoveProduct(registry, job.Id);
                    await WaitReleasedAsync(registry, job.Id);
                }
            }
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
            if (executable is not null && LabInstallation.IsInstalledWorker(executable, product: true))
                Directory.Delete(Path.GetDirectoryName(executable)!, recursive: true);
        }
    }

    private static async Task WaitReleasedAsync(ProductJobRegistry registry, Guid id)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { using var released = registry.AcquireScheduledLock(id); return; }
            catch (Exception error) when (VersionJob.SharingViolation(error) && timer.Elapsed < TimeSpan.FromSeconds(20))
            { await Task.Delay(250); }
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
