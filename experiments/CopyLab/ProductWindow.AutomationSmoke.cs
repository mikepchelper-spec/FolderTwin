using System.Diagnostics;
using System.IO;

namespace CopyLab;

public partial class ProductWindow
{
    internal static async Task RunAutomationSmokeAsync()
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
            window.JobName.Text = "Synthetic automatic mirror";
            window.BackupMode.SelectedIndex = (int)ProductBackupMode.ProtectedMirror;
            Check(window.step == 2 && window.WatchChanges.IsChecked == true &&
                window.AllowWizardMirrorRetire.Visibility == System.Windows.Visibility.Visible && window.AllowWizardMirrorRetire.IsChecked == false,
                $"The wizard did not disclose separate retirement consent (step {window.step}, watch {window.WatchChanges.IsChecked}, consent {window.AllowWizardMirrorRetire.Visibility}/{window.AllowWizardMirrorRetire.IsChecked}).");
            window.AllowWizardMirrorRetire.IsChecked = true;
            await Click(window, window.NextStepButton);
            Check(window.ReviewText.Text.Contains("Retiradas ordinarias autorizadas", StringComparison.Ordinal), "The wizard omitted consent in its final review.");
            await Click(window, window.SaveJobButton);
            using var registry = Open();
            var job = registry.List().Jobs.Single();
            Check(job is { WatchMode: ProductWatchMode.Watching, AutomaticMirrorPolicy: AutomaticMirrorRules.Version },
                "The wizard did not save the reviewed policy.");
            var first = await WaitAsync(0);
            window.Close();
            window = null;
            File.Delete(Path.Combine(source, "retire.txt"));
            var second = await WaitAsync(first.Intent.Sequence);
            Check(!File.Exists(Path.Combine(VersionStore.OpenProduct(job).MirrorCurrentPath, "retire.txt")),
                "Closing the window stopped the automatic retirement.");
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
            VersionStore.OpenProduct(job).Restore(first.Generation!, recovered);
            Check(File.ReadAllText(Path.Combine(recovered, "retire.txt")) == "recover this",
                "The retired file did not recover with the source absent.");
            Console.Error.WriteLine("PASS Ruta wizard consent, automatic first backup, retirement with the window closed, durable pause and offline recovery.");

            async Task<ProductRunView> WaitAsync(long after)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    if (monitor is null || monitor.HasExited) throw new InvalidOperationException("The UI did not start its test monitor.");
                    if (registry.RunHistory(job.Id).Runs.FirstOrDefault(run => !run.Running &&
                        run.Intent.Sequence > after && run.Result?.State == ProductRunState.Confirmed) is { } completed)
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
