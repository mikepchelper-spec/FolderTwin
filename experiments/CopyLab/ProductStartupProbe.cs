using System.Diagnostics;
using System.IO;

namespace CopyLab;

internal static class ProductStartupProbe
{
    internal static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("The default-startup probe requires a disposable hosted Windows runner.");
        using var registry = ProductJobRegistry.OpenLocal();
        var before = registry.List();
        if (before.Jobs.Length != 0 || before.Pending.Length != 0 || before.Unreadable != 0)
            throw new InvalidOperationException("Default startup requires an empty disposable product profile.");
        string executable = Path.Combine(AppContext.BaseDirectory, "FolderTwin.exe");
        using var process = Process.Start(new ProcessStartInfo(executable)
        { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory }) ?? throw new IOException("Could not start the packaged product.");
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(30))
            {
                process.Refresh();
                if (process.MainWindowTitle == "FolderTwin · Trabajos") break;
                await Task.Delay(100);
            }
            if (process.HasExited || process.MainWindowTitle != "FolderTwin · Trabajos")
                throw new InvalidOperationException("Launching FolderTwin.exe without arguments did not open the product dashboard.");
            // Initialization may temporarily defer closing while it reads the registry.
            timeout.Restart();
            while (!process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(15))
            {
                process.CloseMainWindow();
                await Task.Delay(100);
            }
            if (!process.HasExited) throw new InvalidOperationException("The default dashboard could not close normally.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || registry.List() is not { Jobs.Length: 0, Pending.Length: 0, Unreadable: 0 })
                throw new InvalidOperationException("Default startup created a job or did not exit successfully.");
            Console.Error.WriteLine("PASS FolderTwin.exe launches the product dashboard without arguments from the packaged runtime on a clean profile and closes without creating jobs.");
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
}
