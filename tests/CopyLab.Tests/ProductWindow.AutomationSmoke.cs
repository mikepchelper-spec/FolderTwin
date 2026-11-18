using System.Diagnostics;
using System.IO;

namespace FolderTwin;

public partial class ProductWindow
{
    internal static async Task RunAutomationSmokeAsync()
    {
        foreach (var mode in Enum.GetValues<ProductBackupMode>())
            await RunAutomationModeAsync(mode);
        Console.Error.WriteLine("PASS Ruta wizard in all modes, automatic first copy, changes with the window closed, durable pause and offline recovery.");
    }

    private static async Task RunAutomationModeAsync(ProductBackupMode mode)
    {
        var fixture = LabWorkspace.Create();
        string source = Folder("source"), destination = Folder("destinations"), configuration = Folder("settings");
        File.WriteAllText(Path.Combine(source, "keep.txt"), "first");
        File.WriteAllText(Path.Combine(source, "retire.txt"), "recover this");
        Process? monitor = null;
        ProductWindow? window = null;
        ProductJobRegistry Open() => new(configuration);
        void StartMonitor(ProductJobRegistry registry, bool? atLogon)
        {
            if (monitor is not null && !monitor.HasExited) return;
            monitor?.Dispose();
            monitor = ProductWorker.StartMonitor(registry.Location);
        }
        try
        {
            var picks = new Queue<string>([source, destination]);
            window = new ProductWindow(Open, _ => picks.Dequeue(), testMonitor: StartMonitor);
            window.Show();
            await window.InitializeAsync();
            await Click(window, window.NewJobButton);
            await Click(window, window.ChooseOriginButton);
            await Click(window, window.NextStepButton);
            await Click(window, window.ChooseParentButton);
            await Click(window, window.NextStepButton);
            bool protectedMirror = mode == ProductBackupMode.ProtectedMirror;
            window.JobName.Text = "Synthetic automatic " + mode;
            window.BackupMode.SelectedIndex = (int)mode;
            Check(window.step == 2 && window.WatchChanges.IsChecked == true &&
                (window.AllowWizardMirrorRetire.Visibility == System.Windows.Visibility.Visible) == protectedMirror &&
                window.AllowWizardMirrorRetire.IsChecked == false,
                $"The wizard did not disclose the automation policy for {mode}.");
            if (protectedMirror) window.AllowWizardMirrorRetire.IsChecked = true;
            await Click(window, window.NextStepButton);
            Check(window.ReviewText.Text.Contains("Retiradas ordinarias autorizadas", StringComparison.Ordinal) == protectedMirror,
                "The final review did not describe the chosen retirement policy.");
            await Click(window, window.SaveJobButton);
            using var registry = Open();
            var job = registry.List().Jobs.Single();
            Check(job.WatchMode == ProductWatchMode.Watching && job.Options.Mode == mode &&
                job.AutomaticMirrorPolicy == (protectedMirror ? AutomaticMirrorRules.Version : 0),
                "The wizard did not save the reviewed policy.");
            var first = await WaitAsync(0);
            window.Close();
            window = null;
            if (protectedMirror)
            {
                using var held = new FileStream(Path.Combine(source, "retire.txt"), FileMode.Open, FileAccess.Write, FileShare.Read);
                File.WriteAllText(Path.Combine(source, "keep.txt"), "stable while the other file is occupied");
                var partial = await WaitAsync(first.Intent.Sequence, ProductRunState.Busy);
                Check(partial.Generation is { } point && VersionStore.IsPartialPoint(point), "The resident worker did not save a partial point.");
                window = new ProductWindow(Open, testMonitor: StartMonitor);
                window.Show();
                await window.InitializeAsync();
                await Click(window, window.RecoverJobButton);
                Check(window.RecoveryDates.SelectedItem is RecoveryDate { Point.Partial: true } row &&
                    row.Label.Contains("Punto parcial", StringComparison.Ordinal) && !window.RecoveryOnlyChanges.IsEnabled,
                    "Recovery presented a partial point as a full comparison baseline.");
                window.Close();
                window = null;
            }
            else File.WriteAllText(Path.Combine(source, "keep.txt"), "stable while the window is closed");
            File.Delete(Path.Combine(source, "retire.txt"));
            var second = await WaitAsync(first.Intent.Sequence);
            var store = VersionStore.OpenProduct(job);
            if (mode != ProductBackupMode.Versions)
                Check(File.Exists(Path.Combine(store.MirrorCurrentPath, "retire.txt")) == (mode == ProductBackupMode.Accumulative),
                    "The automatic update did not respect the selected policy for the current copy.");
            window = new ProductWindow(Open, testMonitor: StartMonitor);
            window.Show();
            await window.InitializeAsync();
            window.JobSettingsPanel.IsExpanded = true;
            await Click(window, window.ToggleJobWatchButton);
            Check(registry.Get(job.Id).WatchMode == ProductWatchMode.Paused, window.StatusText.Text);
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
                await monitor!.WaitForExitAsync(timeout.Token);
            Check(monitor.ExitCode == 0 && second.Generation is not null, "The saved pause did not release the monitor.");
            Directory.Move(source, Path.Combine(fixture.Root, "source-absent"));
            string recovered = Folder("recovery");
            store.Restore(first.Generation!, recovered);
            Check(File.ReadAllText(Path.Combine(recovered, "retire.txt")) == "recover this",
                "The retired file did not recover with the source absent.");
            string latest = Folder("latest");
            store.Restore(second.Generation!, latest);
            Check(File.ReadAllText(Path.Combine(latest, "keep.txt")) == (protectedMirror
                ? "stable while the other file is occupied" : "stable while the window is closed") &&
                File.Exists(Path.Combine(latest, "retire.txt")) == (mode == ProductBackupMode.Accumulative),
                $"The latest offline point did not restore the expected contents for {mode}.");

            async Task<ProductRunView> WaitAsync(long after, ProductRunState expected = ProductRunState.Confirmed)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    if (monitor is null || monitor.HasExited) throw new InvalidOperationException("The UI did not start its test monitor.");
                    if (registry.RunHistory(job.Id).Runs.FirstOrDefault(run => !run.Running &&
                        run.Intent.Sequence > after && run.Result?.State == expected) is { } completed)
                        return completed;
                    await Task.Delay(100, timeout.Token);
                }
            }
        }
        finally
        {
            window?.Close();
            if (monitor is not null)
            {
                using (monitor)
                {
                    if (!monitor.HasExited)
                    {
                        monitor.Kill(entireProcessTree: true);
                        await monitor.WaitForExitAsync();
                    }
                }
            }
        }
        using (fixture.OpenRegisteredRoot()) { }
        Directory.Delete(fixture.Root, recursive: true);
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }
}
