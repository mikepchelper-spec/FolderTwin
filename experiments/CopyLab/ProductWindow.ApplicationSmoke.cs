using System.IO;
using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private static async Task RunApplicationSmokeAsync()
    {
        var fixture = LabWorkspace.Create();
        ProductWindow? window = null;
        try
        {
            string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
            string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
            string parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "backups")).FullName;
            File.WriteAllText(Path.Combine(source, "dato.txt"), "datos que la gestión de aplicación debe conservar");
            ProductJobRegistry Open() => new(settings);
            var jobs = new List<ProductJob>();
            using (var registry = Open())
                foreach (var mode in Enum.GetValues<ProductBackupMode>())
                {
                    var job = registry.Create(new(mode.ToString(), mode, new TimeOnly(20, 30)),
                        LocalJobRoots.ReviewDedicatedDestination(source, parent));
                    jobs.Add(job);
                    Check(ProductRunner.Run(registry, job.Id).State == ProductRunState.Confirmed, "Could not prepare deployment fixture.");
                }
            int installations = 0, changes = 0;
            bool failSecondRemoval = false;
            var schedules = jobs.Select(job => job.Id).ToHashSet();
            string Prepare()
            {
                installations++;
                using var registry = Open();
                RejectMaintenance(() => { using var run = registry.StartRun(jobs[0].Id); });
                RejectMaintenance(() => registry.UpdateSettings(jobs[0].Id, "must not change", null, false, false));
                return "synthetic-verified-worker.exe";
            }
            void Configure(string _, Guid id, bool remove)
            {
                changes++;
                if (remove && failSecondRemoval && changes == 2) throw new IOException("Synthetic removal interruption.");
                if (remove) schedules.Remove(id); else schedules.Add(id);
            }
            using (var registry = Open())
            {
                var stale = registry.ReviewMaintenance();
                registry.UpdateSettings(jobs[0].Id, "Renamed", new TimeOnly(20, 30), true, false);
                RejectMaintenance(() => ProductDeployment.Install(registry, stale, Prepare, (job, path) => Configure("", job.Id, path is null)));
                Check(installations == 0, "A stale deployment review began installing.");
                var review = registry.ReviewMaintenance();
                using (var active = registry.StartRun(jobs[0].Id))
                    RejectMaintenance(() => ProductDeployment.Install(registry, review, Prepare, (_, _) => { }));
                using (var waiting = registry.AcquireScheduledLock(jobs[0].Id))
                    RejectMaintenance(() => ProductDeployment.RemoveSchedules(registry, review, (_, _) => { }));
            }
            window = new ProductWindow(Open, testSchedule: Configure, testPackageInstall: Prepare);
            await window.InitializeAsync();
            await Click(window, window.ManageApplicationButton);
            Check(window.ApplicationPanel.Visibility == Visibility.Visible && !window.InstallApplicationButton.IsEnabled &&
                !window.RemoveApplicationSchedulesButton.IsEnabled && window.ApplicationReviewText.Text.Contains("Renamed", StringComparison.Ordinal),
                "Application management lost the reviewed jobs or bypassed acceptance.");
            window.AcceptApplicationUpdate.IsChecked = true;
            await Click(window, window.InstallApplicationButton);
            Check(installations == 1 && schedules.Count == 3 && window.InstalledApplicationText.Text.Contains("verificado", StringComparison.Ordinal),
                "WPF did not install and update its reviewed schedules.");
            changes = 0; failSecondRemoval = true;
            window.AcceptRemoveSchedules.IsChecked = true;
            await Click(window, window.RemoveApplicationSchedulesButton);
            using (var registry = Open())
                Check(registry.List().Jobs.Count(job => !job.Enabled) == 1 && schedules.Count == 2 &&
                    !window.StatusText.Text.StartsWith("Horarios retirados", StringComparison.Ordinal),
                    "A partial schedule removal was reported as complete or disabled unrelated jobs.");
            failSecondRemoval = false;
            window.ReturnFromApplicationClick(window, new RoutedEventArgs());
            await window.activeTask;
            await Click(window, window.ManageApplicationButton);
            window.AcceptRemoveSchedules.IsChecked = true;
            await Click(window, window.RemoveApplicationSchedulesButton);
            using (var registry = Open())
            {
                Check(registry.List().Jobs.All(job => !job.Enabled) && schedules.Count == 0, "Retried withdrawal did not disable jobs and remove their tasks.");
                foreach (var job in registry.List().Jobs)
                {
                    var store = VersionStore.OpenProduct(job);
                    Check(store.ListPoints().Length == 1 && !store.Verify(store.ListPoints()[0].Id), "Application withdrawal lost backup data.");
                    string output = Directory.CreateDirectory(Path.Combine(fixture.Root, "restored-" + job.Id.ToString("N"))).FullName;
                    _ = store.Restore(store.ListPoints()[0].Id, output);
                    Check(File.ReadAllText(Path.Combine(output, "dato.txt")) == File.ReadAllText(Path.Combine(source, "dato.txt")),
                        "A disabled job could no longer recover its backup.");
                }
            }
            window.ReturnFromApplicationClick(window, new RoutedEventArgs());
            await window.activeTask;
            Check(window.JobList.Items.Count == 3 && window.JobListPanel.Visibility == Visibility.Visible, "Withdrawal hid the preserved recovery jobs.");
            Console.Error.WriteLine("PASS WPF application management reviews and installs, excludes active workers/retries and stale settings, retries a partial task withdrawal and preserves verified recovery for all disabled modes.");
        }
        finally
        {
            window?.Close();
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static void RejectMaintenance(Action action)
    {
        try { action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("Concurrent or stale maintenance was allowed.");
    }
}
