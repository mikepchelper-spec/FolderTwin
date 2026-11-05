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
                window = new ProductWindow(Open, testWorker: (_, id, _) =>
                {
                    string executable = Environment.ProcessPath ?? throw new IOException("Executable unavailable.");
                    var start = new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                        start.ArgumentList.Add(typeof(ProductRunner).Assembly.Location);
                    foreach (string argument in new[] { "--held-product-child", workspace.Root, id.ToString("N") }) start.ArgumentList.Add(argument);
                    return Process.Start(start) ?? throw new IOException("Could not start the controlled worker.");
                });
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
}
