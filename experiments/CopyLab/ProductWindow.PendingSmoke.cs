using System.Diagnostics;
using System.IO;
using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private static async Task RunPendingVersionSmokeAsync()
    {
        foreach (string action in new[] { "resume", "cancel", "close" })
        {
            var fixture = LabWorkspace.Create();
            ProductWindow? window = null;
            Process? witness = null;
            try
            {
                string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
                string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
                string parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "backups")).FullName;
                string data = Path.Combine(source, "dato.txt");
                File.WriteAllText(data, "baseline");
                ProductJobRegistry Open() => new(settings);
                ProductJob job;
                ProductRunResult baseline;
                using (var registry = Open())
                {
                    job = registry.Create(new("Plan pendiente", ProductBackupMode.Versions, null), LocalJobRoots.ReviewDedicatedDestination(source, parent));
                    baseline = ProductRunner.Run(registry, job.Id);
                }
                var store = VersionStore.OpenProduct(job);
                File.WriteAllText(data, "pending content");
                string pending = VersionStore.NewGenerationId();
                bool reached = false;
                try
                {
                    store.CreatePoint(job.Source.VolumePath, pending, checkpoint: step =>
                    {
                        if (step != CatalogStep.BackupPublished) return;
                        reached = true;
                        throw new IOException("Synthetic interrupted publication.");
                    });
                }
                catch (IOException) when (reached) { }
                Check(reached, "The pending WPF fixture did not reach publication.");
                File.WriteAllText(data, "source changed after interrupted point");
                window = new ProductWindow(Open, testAbandonWorker: (_, id, review) => StartHeldWorker(fixture.Root, id, review, abandon: true));
                await window.InitializeAsync();
                window.SelectJob(job.Id);
                await Click(window, window.ReviewPendingButton);
                Check(window.pendingReviewRun is not null && !window.ApplyPendingButton.IsEnabled &&
                    window.PendingReviewText.Text.Contains("1 archivos", StringComparison.Ordinal),
                    "WPF lost the pending review or did not require explicit consent.");
                window.AcceptPendingPlan.IsChecked = true;
                window.ApplyPendingButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await window.activeTask;
                var worker = window.workerProcesses[job.Id];
                witness = Process.GetProcessById(worker.Id);
                _ = witness.Handle;
                Check(await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "READY",
                    "The pending-plan WPF worker did not reach its archive boundary.");
                await window.RefreshRuntimeAsync();
                Check(window.PauseRunButton.IsEnabled && !window.ReviewPendingButton.IsEnabled,
                    "WPF omitted controls or allowed overlapping pending review.");
                await Click(window, window.PauseRunButton);
                worker.StandardInput.WriteLine(); worker.StandardInput.Flush();
                var timer = Stopwatch.StartNew();
                while (window.runSnapshots[job.Id].Runs[0].Progress?.Paused != true && timer.Elapsed < TimeSpan.FromSeconds(10))
                { await Task.Delay(50); await window.RefreshRuntimeAsync(); }
                var paused = window.runSnapshots[job.Id].Runs[0];
                Check(paused.Progress?.Paused == true, "WPF did not observe the paused conservation worker.");
                if (action == "close")
                {
                    window.Close(); window = null;
                    Check(!witness.HasExited, "Closing WPF killed pending-plan conservation.");
                    using var registry = Open();
                    registry.RequestRun(job.Id, paused.Intent.Id, ProductRunAction.Resume);
                }
                else await Click(window, action == "cancel" ? window.CancelRunButton : window.PauseRunButton);
                await witness.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Check(witness.ExitCode == 0, "The pending-plan worker did not finish.");
                window?.Close();
                window = new ProductWindow(Open);
                await window.InitializeAsync();
                window.SelectJob(job.Id);
                string aside = Path.Combine(fixture.Root, "source absent");
                Directory.Move(source, aside);
                if (action == "cancel")
                {
                    await Click(window, window.ReviewPendingButton);
                    Check(window.PendingReviewText.Text.Contains("Reanúdala", StringComparison.Ordinal) &&
                        window.AcceptPendingPlan.Visibility == Visibility.Collapsed && window.ApplyPendingButton.IsEnabled,
                        "Reopened WPF did not offer resumption of the interrupted journal.");
                    await Click(window, window.ApplyPendingButton);
                }
                await window.RefreshRuntimeAsync();
                var history = window.runSnapshots[job.Id];
                Check(history.Runs[0] is { Intent.Operation: ProductRunOperation.AbandonVersion, Result.State: ProductRunState.Confirmed } &&
                    history.Runs.Single(run => run.Intent.Operation == ProductRunOperation.Backup).Result!.FinishedUtc == baseline.FinishedUtc &&
                    window.RuntimeStatusText.Text.Contains("Plan anterior conservado", StringComparison.Ordinal) && !store.Verify(baseline.Generation!),
                    "WPF mislabeled the partial plan as a backup or lost the existing recovery point.");
                using (var registry = Open())
                    Check(TraySnapshot.ReadProduct(registry).Attention.Contains($"{job.Id:N}-pending-plan"),
                        "The tray treated plan conservation as a complete backup.");
                Directory.Move(aside, source);
                await Click(window, window.BackupJobButton);
                var fresh = window.runSnapshots[job.Id].Runs[0];
                Check(fresh is { Intent.Operation: ProductRunOperation.Backup, Result.State: ProductRunState.Confirmed } &&
                    fresh.Generation != pending && !store.Verify(fresh.Generation!), "WPF could not back up the current source after preserving the old plan.");
            }
            finally
            {
                window?.Close();
                if (witness is not null)
                {
                    if (!witness.HasExited) { witness.Kill(); witness.WaitForExit(15000); }
                    witness.Dispose();
                }
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
        }
        Console.Error.WriteLine("PASS WPF explicitly reviews and conserves incomplete version plans with real pause, cancel and UI-independent workers; reopening resumes without source and permits a fresh verified backup without changing prior protection dates.");
    }
}
