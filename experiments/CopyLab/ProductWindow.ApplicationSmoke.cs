using System.IO;
using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private static async Task RunApplicationSmokeAsync()
    {
        var fixture = LabWorkspace.Create();
        using var package = new InstallationProbe.Fixture();
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
                return LabInstallation.EnsureAt(package.Package, package.Installs);
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
            window = new ProductWindow(Open, testSchedule: Configure, testPackageInstall: Prepare, testInstallations: package.Installs);
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
            await Click(window, window.ReviewInstalledVersionsButton);
            Check(window.InstalledVersionChoice.Items.Count == 1 && !window.RemoveInstalledVersionButton.IsEnabled &&
                window.InstalledVersionChoice.SelectedItem is InstallRemovalChoice { Current: false }, "WPF bypassed installed-version review or acceptance.");
            var selectedVersion = ((InstallRemovalChoice)window.InstalledVersionChoice.SelectedItem).Review;
            using (var registry = Open())
                RejectMaintenance(() => ProductDeployment.RemoveVersion(registry, registry.ReviewMaintenance(), package.Installs, selectedVersion,
                    static () => { }, step => { if (step == InstallRemovalStep.FileRemoved) throw new IOException("Synthetic interrupted removal."); }));
            await Click(window, window.ReviewInstalledVersionsButton);
            Check(window.InstalledVersionChoice.SelectedItem is InstallRemovalChoice { InProgress: true },
                "WPF did not reopen the interrupted version-removal journal.");
            window.AcceptInstalledVersionRemoval.IsChecked = true;
            await Click(window, window.RemoveInstalledVersionButton);
            Check(window.InstalledVersionChoice.Items.Count == 0 && !Directory.Exists(Path.Combine(package.Installs, selectedVersion.Fingerprint)),
                "WPF did not finish the reviewed package removal.");
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
            await Click(window, window.ManageApplicationButton);
            window.AcceptApplicationUpdate.IsChecked = true;
            await Click(window, window.InstallApplicationButton);
            await Click(window, window.ReviewInstalledVersionsButton);
            Check(window.InstalledVersionChoice.Items.Count == 1 && installations == 2 && schedules.Count == 0 &&
                !((InstallRemovalChoice)window.InstalledVersionChoice.SelectedItem).Review.Identity.AsSpan().SequenceEqual(selectedVersion.Identity),
                "WPF could not reinstall the same package or re-enabled withdrawn schedules.");
            Console.Error.WriteLine("PASS WPF application management reviews and installs, excludes active workers/retries and stale settings, retries a partial task withdrawal and preserves verified recovery for all disabled modes.");
            Console.Error.WriteLine("PASS WPF reviews a concrete installed version, requires acceptance, reopens an interrupted removal, preserves all backup modes and reinstalls without re-enabling withdrawn jobs.");
        }
        finally
        {
            window?.Close();
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static async Task RunRefreshOverlapSmokeAsync()
    {
        var fixture = LabWorkspace.Create();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int hold = 0;
        ProductWindow? window = null;
        try
        {
            string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
            ProductJobRegistry Open()
            {
                if (Interlocked.Exchange(ref hold, 0) == 1)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("The synthetic query was not released.");
                }
                return new(settings);
            }
            window = new ProductWindow(Open);
            await window.InitializeAsync();
            hold = 1;
            Task first = window.RefreshRuntimeAsync();
            Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "The synthetic refresh did not enter its query.");
            Task second = window.RefreshRuntimeAsync();
            Check(!second.IsCompleted, "An explicit refresh returned a stale snapshot while another query was active.");
            release.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Check(!window.polling, "Queued refresh did not finish its fresh query.");
            Console.Error.WriteLine("PASS an explicit runtime refresh waits for an in-flight query and obtains its own fresh snapshot.");
        }
        finally
        {
            release.Set();
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
