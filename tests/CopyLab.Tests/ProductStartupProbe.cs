using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FolderTwin;

internal static class ProductStartupProbe
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static IntPtr FindVisibleWindowByTitle(int processId, string expectedTitle)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != (uint)processId) return true;
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (string.Equals(sb.ToString(), expectedTitle, StringComparison.Ordinal))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    internal static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("The default-startup probe requires a disposable hosted Windows runner.");
        using var registry = ProductJobRegistry.OpenLocal();
        var before = registry.List();
        if (before.Jobs.Length != 0 || before.Pending.Length != 0 || before.Unreadable != 0)
            throw new InvalidOperationException("Default startup requires an empty disposable product profile.");
        string packageDir = LabInstallation.PackageSourceDirectory;
        string executable = LabInstallation.PackageSourceExecutable;
        using var process = Process.Start(new ProcessStartInfo(executable)
        { UseShellExecute = false, WorkingDirectory = packageDir }) ?? throw new IOException("Could not start the packaged product.");
        try
        {
            var timeout = Stopwatch.StartNew();
            IntPtr routeWindow = IntPtr.Zero;
            while (!process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(30))
            {
                process.Refresh();
                routeWindow = FindVisibleWindowByTitle(process.Id, "FolderTwin · Trabajos");
                if (routeWindow != IntPtr.Zero) break;
                await Task.Delay(100);
            }
            if (process.HasExited || routeWindow == IntPtr.Zero)
                throw new InvalidOperationException("Launching FolderTwin.exe without arguments did not open the product dashboard.");
            // Initialization may temporarily defer closing while it reads the registry.
            timeout.Restart();
            while (!process.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(15))
            {
                IntPtr targetWindow = FindVisibleWindowByTitle(process.Id, "FolderTwin · Trabajos");
                if (targetWindow != IntPtr.Zero)
                    PostMessage(targetWindow, 0x0010, IntPtr.Zero, IntPtr.Zero);
                else
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
