using System.Diagnostics;
using System.IO;

namespace CopyLab;

internal static class ProductTrayProbe
{
    internal static async Task RunAsync()
    {
        var fixture = LabWorkspace.Create();
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
            string parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "backups")).FullName;
            string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
            File.WriteAllText(Path.Combine(source, "dato.txt"), "contenido para bandeja");
            ProductJobRegistry Open() => new(settings);
            using var registry = Open();
            var jobs = Enum.GetValues<ProductBackupMode>().Select(mode => registry.Create(new(mode.ToString(), mode, null),
                LocalJobRoots.ReviewDedicatedDestination(source, parent))).ToArray();
            foreach (var job in jobs) Check(ProductRunner.Run(registry, job.Id).State == ProductRunState.Confirmed, "Tray fixture failed.");
            using var tray = new LabTray(Open, () => { }, () => { }, notifications: false);
            var timer = Stopwatch.StartNew();
            await tray.RefreshAsync();
            timer.Stop();
            Check(tray.StatusText.Contains("3 trabajos", StringComparison.Ordinal) && tray.CanRun && tray.NotificationCount == 0 &&
                tray.AgeText.Contains("más antiguo", StringComparison.Ordinal), "Product tray lost a mode or its oldest backup date.");
            Console.Error.WriteLine($"Product tray query for three modes; seconds={timer.Elapsed.TotalSeconds:F3}.");
            var versioned = jobs.Single(job => job.Options.Mode == ProductBackupMode.Versions);
            string detached = Path.Combine(fixture.Root, "detached");
            Directory.Move(source, detached);
            try
            {
                _ = ProductRunner.Run(registry, versioned.Id);
                await tray.RefreshAsync();
                await tray.RefreshAsync();
                _ = ProductRunner.Run(registry, versioned.Id);
                await tray.RefreshAsync();
                Check(tray.NotificationCount == 1 && tray.StatusText.Contains("requieren atención", StringComparison.Ordinal),
                    "Healthy jobs hid an incident or the same retry repeated its notification.");
                string output = Directory.CreateDirectory(Path.Combine(fixture.Root, "recovered")).FullName;
                string generation = registry.RunHistory(versioned.Id).Runs.First(run => run.Result?.State == ProductRunState.Confirmed).Generation!;
                Guid request = registry.ApproveRestore(versioned.Id, generation,
                    LocalJobRoots.ReviewDedicatedDestination(versioned.Destination.LastVolumePath, output));
                Check(ProductRunner.Restore(registry, versioned.Id, request).State == ProductRunState.Confirmed, "Tray fixture could not restore.");
                await tray.RefreshAsync();
                Check(tray.NotificationCount == 1 && tray.StatusText.Contains("requieren atención", StringComparison.Ordinal),
                    "A successful restoration hid an unresolved backup incident.");
            }
            finally { Directory.Move(detached, source); }
            _ = ProductRunner.Run(registry, versioned.Id);
            await tray.RefreshAsync();
            long[] before = jobs.Select(job => registry.RunHistory(job.Id).Runs[0].Intent.Sequence).ToArray();
            tray.RunNow();
            timer.Restart();
            while (timer.Elapsed < TimeSpan.FromSeconds(30) && jobs.Where((job, index) =>
                registry.RunHistory(job.Id).Runs.FirstOrDefault() is not { Result.State: ProductRunState.Confirmed } latest ||
                latest.Intent.Sequence <= before[index] || latest.Running).Any()) await Task.Delay(100);
            Check(jobs.Where((job, index) => registry.RunHistory(job.Id).Runs[0].Intent.Sequence <= before[index] ||
                registry.RunHistory(job.Id).Runs[0].Result?.State != ProductRunState.Confirmed).Count() == 0,
                "Product tray did not run all three independent workers.");
            registry.UpdateSettings(versioned.Id, versioned.Options.Name, null, false, true);
            Check(!TraySnapshot.ReadProduct(registry).Jobs.Contains(versioned.Id), "The tray offered backup for a retired job.");
            foreach (var job in jobs.Where(job => job.Id != versioned.Id)) registry.UpdateSettings(job.Id, job.Options.Name, null, false, false);
            await tray.RefreshAsync();
            Check(!tray.CanRun && tray.StatusText == "Sin trabajos habilitados.", "The disabled tray still offered backup.");
            Console.Error.WriteLine("PASS product tray covers three modes, retains cross-job incidents, suppresses duplicate notices, runs real workers and excludes disabled or retired jobs.");
        }
        finally
        {
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
