using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace FolderTwin;

public partial class ProductWindow
{
    internal Action<string>? TestOpenRestoredFolder { get; set; }

    private void OpenRestoredFolderClick(object sender, RoutedEventArgs e)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { } job ||
            !runSnapshots.TryGetValue(job.Id, out var history)) return;
        var restored = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Restore &&
            run.Result?.State == ProductRunState.Confirmed && run.Restore is not null);
        if (restored?.Restore is not { } request) return;
        var testCallback = TestOpenRestoredFolder;
        Start(async () =>
        {
            try
            {
                string shellPath = await Task.Run(() =>
                {
                    using var parent = new DirectoryFence(request.Target.Parent.VolumePath);
                    if (!WindowsProbeNative.DirectoryIdentity(parent.Root).AsSpan().SequenceEqual(request.Target.Parent.Identity))
                        throw new InvalidDataException("La carpeta padre de recuperación cambió de identidad.");
                    using var directory = WindowsProbeNative.OpenPlainDirectory(parent.Root, request.Target.FolderName);
                    var name = new StringBuilder(32768);
                    uint length = GetFinalPathNameByHandle(directory, name, (uint)name.Capacity, 0);
                    if (length == 0 || length >= name.Capacity)
                        throw new IOException("La carpeta recuperada requiere una letra o punto de montaje accesible.");
                    string resolved = name.ToString();
                    if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
                    if (!Path.IsPathFullyQualified(resolved)) throw new IOException("No se encontró una ruta accesible para el Explorador.");
                    if (testCallback is null)
                    {
                        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                            { UseShellExecute = false };
                        start.ArgumentList.Add(resolved);
                        using var process = Process.Start(start);
                    }
                    return resolved;
                });
                testCallback?.Invoke(shellPath);
            }
            catch (Exception error) when (ProductAutomation.Expected(error))
            { SetStatus("No se pudo abrir la carpeta recuperada. Comprueba que siga disponible. " + error.Message); }
        });
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file,
        StringBuilder path, uint size, uint flags);
}
