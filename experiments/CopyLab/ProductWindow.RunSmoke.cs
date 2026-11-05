using System.Diagnostics;
using System.IO;
using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private static async Task RunWorkerSmokeAsync()
    {
        foreach (bool close in new[] { false, true })
        {
            var workspace = LabWorkspace.Create();
            ProductWindow? window = null;
            Process? witness = null;
            try
            {
                string settings = Directory.CreateDirectory(Path.Combine(workspace.Root, "settings")).FullName;
                string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "source")).FullName;
                string parent = Directory.CreateDirectory(Path.Combine(workspace.Root, "target")).FullName;
                File.WriteAllText(Path.Combine(source, "datos.txt"), "UI-independent synthetic backup");
                ProductJobRegistry Open() => new(settings);
                ProductJob job;
                using (var registry = Open())
                    job = registry.Create(new("Trabajo controlado", ProductBackupMode.Versions, null),
                        LocalJobRoots.ReviewDedicatedDestination(source, parent));
                window = new ProductWindow(Open, testWorker: (_, id, _) => StartHeldWorker(workspace.Root, id));
                await window.InitializeAsync();
                window.SelectJob(job.Id);
                window.BackupJobButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await window.activeTask;
                var worker = window.workerProcesses[job.Id];
                witness = Process.GetProcessById(worker.Id);
                _ = witness.Handle;
                string? ready = await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Check(ready == "READY", "The controlled UI worker did not reach its copying boundary.");
                await window.RefreshRuntimeAsync();
                Check(!window.BackupJobButton.IsEnabled && window.PauseRunButton.IsEnabled,
                    "The UI did not distinguish an active worker from a new execution.");
                await Click(window, window.PauseRunButton);
                worker.StandardInput.WriteLine();
                worker.StandardInput.Flush();
                var deadline = Stopwatch.StartNew();
                while (window.runSnapshots[job.Id].Runs[0].Progress?.Paused != true && deadline.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(50);
                    await window.RefreshRuntimeAsync();
                }
                var paused = window.runSnapshots[job.Id].Runs[0];
                Check(paused.Progress?.Paused == true && window.PauseRunButton.Content.ToString() == "_Continuar",
                    "The UI did not observe the worker's actual paused state.");
                if (close)
                {
                    window.Close();
                    window = null;
                    Check(!witness.HasExited, "Closing the UI terminated its worker.");
                    using var registry = Open();
                    registry.RequestRun(job.Id, paused.Intent.Id, ProductRunAction.Resume);
                }
                else await Click(window, window.PauseRunButton);
                await witness.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Check(witness.ExitCode == 0, "The UI-controlled worker did not finish successfully.");
                if (window is not null) window.Close();
                window = new ProductWindow(Open);
                await window.InitializeAsync();
                window.SelectJob(job.Id);
                await window.RefreshRuntimeAsync();
                Check(window.RuntimeStatusText.Text.Contains("Último respaldo completo", StringComparison.Ordinal) &&
                    window.runSnapshots[job.Id].Runs[0].Result?.State == ProductRunState.Confirmed &&
                    !VersionStore.OpenProduct(job).Verify(window.runSnapshots[job.Id].Runs[0].Generation!),
                    "The reopened UI did not recover the worker's completed result.");
            }
            finally
            {
                window?.Close();
                if (witness is not null)
                {
                    if (!witness.HasExited) { witness.Kill(); witness.WaitForExit(15000); }
                    witness.Dispose();
                }
                using (workspace.OpenRegisteredRoot()) { }
                Directory.Delete(workspace.Root, recursive: true);
            }
        }
        Console.Error.WriteLine("PASS WPF controls real worker pause/resume; closing the UI preserves its process and reopening reads its verified result.");
    }

    private static Process StartHeldWorker(string root, Guid id, Guid? request = null, bool mirrorReview = false, bool abandon = false)
    {
        string executable = Environment.ProcessPath ?? throw new IOException("Executable unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(ProductRunner).Assembly.Location);
        foreach (string argument in new[] { abandon ? "--held-product-abandon-child" : mirrorReview ? "--held-mirror-review-child" : request is null ? "--held-product-child" : "--held-restore-child", root, id.ToString("N") })
            start.ArgumentList.Add(argument);
        if (request is { } selected) start.ArgumentList.Add(selected.ToString("N"));
        return Process.Start(start) ?? throw new IOException("Could not start the controlled worker.");
    }

    private static async Task RunRestoreWorkerSmokeAsync()
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
                string output = Directory.CreateDirectory(Path.Combine(fixture.Root, "recovery")).FullName;
                File.WriteAllText(Path.Combine(source, "datos.txt"), "restauración controlada");
                ProductJobRegistry Open() => new(settings);
                ProductJob job;
                ProductRunResult backup;
                using (var registry = Open())
                {
                    job = registry.Create(new("Restauración controlada", ProductBackupMode.Versions, null),
                        LocalJobRoots.ReviewDedicatedDestination(source, parent));
                    backup = ProductRunner.Run(registry, job.Id);
                }
                window = new ProductWindow(Open, _ => output, testRestoreWorker: (_, id, request) => StartHeldWorker(fixture.Root, id, request));
                await window.InitializeAsync();
                window.SelectJob(job.Id);
                await Click(window, window.RecoverJobButton);
                await Click(window, window.ReviewRestoreButton);
                var target = window.restoreChoice!.Destination;
                window.RestorePointButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await window.activeTask;
                var worker = window.workerProcesses[job.Id];
                witness = Process.GetProcessById(worker.Id);
                _ = witness.Handle;
                Check(await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "READY",
                    "The WPF restoration worker did not reach its copy boundary.");
                await window.RefreshRuntimeAsync();
                Check(window.PauseRunButton.IsEnabled && !window.ReviewRestoreButton.IsEnabled &&
                    !window.PauseRunButton.IsDescendantOf(window.JobListPanel) && !window.PauseRunButton.IsDescendantOf(window.RecoveryPanel),
                    "Restoration did not expose shared controls or allowed another restoration while running.");
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
                Check(paused is { Intent.Operation: ProductRunOperation.Restore, Progress.Paused: true }, "WPF did not observe the restore pause.");
                if (action == "close")
                {
                    window.Close();
                    window = null;
                    Check(!witness.HasExited, "Closing WPF killed its restoration worker.");
                    using var registry = Open();
                    registry.RequestRun(job.Id, paused.Intent.Id, ProductRunAction.Resume);
                }
                else await Click(window, action == "cancel" ? window.CancelRunButton : window.PauseRunButton);
                await witness.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Check(witness.ExitCode == 0, "The controlled restore worker did not finish.");
                window?.Close();
                window = new ProductWindow(Open);
                await window.InitializeAsync();
                window.SelectJob(job.Id);
                await window.RefreshRuntimeAsync();
                var history = window.runSnapshots[job.Id];
                Check(history.Runs[0].Result?.State == (action == "cancel" ? ProductRunState.Cancelled : ProductRunState.Confirmed) &&
                    history.Runs.Single(run => run.Intent.Operation == ProductRunOperation.Backup).Result?.FinishedUtc == backup.FinishedUtc &&
                    window.RuntimeStatusText.Text.Contains("Último respaldo completo", StringComparison.Ordinal),
                    "Reopening lost restoration state or changed backup age.");
                if (action != "cancel") Check(File.ReadAllText(Path.Combine(target.Parent.VolumePath, target.FolderName, "datos.txt")) ==
                    "restauración controlada", "The WPF-controlled restore lost its verified content.");
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
        Console.Error.WriteLine("PASS WPF restoration exposes shared pause, resume and cancellation, survives UI closure and restores its own result without changing backup age.");
    }
}
