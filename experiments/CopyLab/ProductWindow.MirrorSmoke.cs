using System.Diagnostics;
using System.IO;
using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private static async Task RunMirrorReviewSmokeAsync()
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
                File.WriteAllText(Path.Combine(source, "datos.txt"), "análisis del espejo WPF");
                ProductJobRegistry Open() => new(settings);
                ProductJob job;
                ProductRunResult backup;
                using (var registry = Open())
                {
                    job = registry.Create(new("Espejo controlado", ProductBackupMode.ProtectedMirror, null),
                        LocalJobRoots.ReviewDedicatedDestination(source, parent));
                    backup = ProductRunner.Run(registry, job.Id);
                }
                window = new ProductWindow(Open, testMirrorReviewWorker: (_, id) => StartHeldWorker(fixture.Root, id, mirrorReview: true));
                await window.InitializeAsync();
                window.SelectJob(job.Id);
                window.BackupJobButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await window.activeTask;
                var worker = window.workerProcesses[job.Id];
                witness = Process.GetProcessById(worker.Id);
                _ = witness.Handle;
                Check(await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "READY",
                    "The WPF mirror worker did not reach its analysis boundary.");
                await window.RefreshRuntimeAsync();
                Check(window.PauseRunButton.IsEnabled && window.MirrorReviewPanel.Visibility == Visibility.Collapsed,
                    "WPF offered an approval before analysis finished or omitted its controls.");
                await Click(window, window.PauseRunButton);
                worker.StandardInput.WriteLine();
                worker.StandardInput.Flush();
                var timer = Stopwatch.StartNew();
                while (window.runSnapshots[job.Id].Runs[0].Progress?.Paused != true && timer.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(50);
                    await window.RefreshRuntimeAsync();
                }
                var paused = window.runSnapshots[job.Id].Runs[0];
                Check(paused is { Intent.Operation: ProductRunOperation.ReviewMirror, Progress.Paused: true },
                    "WPF did not observe actual mirror-analysis pause.");
                if (action == "close")
                {
                    window.Close();
                    window = null;
                    Check(!witness.HasExited, "Closing WPF killed its mirror analysis.");
                    using var registry = Open();
                    registry.RequestRun(job.Id, paused.Intent.Id, ProductRunAction.Resume);
                }
                else await Click(window, action == "cancel" ? window.CancelRunButton : window.PauseRunButton);
                await witness.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Check(witness.ExitCode == 0, "The controlled mirror analysis did not finish.");
                window?.Close();
                window = new ProductWindow(Open);
                await window.InitializeAsync();
                window.SelectJob(job.Id);
                await window.RefreshRuntimeAsync();
                var history = window.runSnapshots[job.Id];
                Check(history.Runs.Single(run => run.Intent.Operation == ProductRunOperation.Backup).Result?.FinishedUtc == backup.FinishedUtc &&
                    VersionStore.OpenProduct(job).ListPoints().Length == 1, "Mirror analysis changed backup age or published a point.");
                if (action == "cancel")
                {
                    Check(window.MirrorReviewPanel.Visibility == Visibility.Collapsed && window.mirrorReview is null,
                        "Cancelled analysis left an applicable review.");
                    using var registry = Open();
                    Check(TraySnapshot.ReadProduct(registry).Attention.Contains($"{job.Id:N}-review"),
                        "The tray hid cancelled mirror analysis.");
                }
                else
                {
                    Check(window.MirrorReviewPanel.Visibility == Visibility.Visible && window.ApplyMirrorButton.IsEnabled &&
                        window.mirrorReviewRun == paused.Intent.Id && window.mirrorReview is { Conflicts: 0 },
                        "The reopened window lost its durable review or approval controls.");
                    await Click(window, window.ApplyMirrorButton);
                    Check(VersionStore.OpenProduct(job).ListPoints().Length == 2 &&
                        window.MirrorReviewPanel.Visibility == Visibility.Collapsed, "The reopened durable review did not apply as a real backup.");
                    using var registry = Open();
                    File.Delete(Path.Combine(source, "datos.txt"));
                    Check(ProductRunner.Run(registry, job.Id).State == ProductRunState.Review &&
                        ProductRunner.ReviewMirror(registry, job.Id).State == ProductRunState.Confirmed &&
                        TraySnapshot.ReadProduct(registry).Attention.Any(key => key.StartsWith(job.Id.ToString("N"), StringComparison.Ordinal)),
                        "A completed analysis hid pending destructive mirror changes.");
                }
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
        Console.Error.WriteLine("PASS WPF mirror analysis pauses, resumes, cancels and survives UI closure; reopening recovers the exact review and requires a separate apply action.");
    }
}
