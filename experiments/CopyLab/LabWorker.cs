using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace CopyLab;

internal static class LabWorker
{
    internal static void Start(LabWorkspace workspace, Guid id)
    {
        string executable = Environment.ProcessPath ?? throw new IOException("No se pudo localizar el ejecutor.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in new[] { "--run-version-job", workspace.Root, id.ToString("N") })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("No se pudo iniciar el trabajo.");
    }
}
