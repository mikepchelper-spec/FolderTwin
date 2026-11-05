using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace CopyLab;

internal static class ProductWorker
{
    internal static Process Start(string registry, Guid id, Guid? approval = null)
        => Launch(["--run-product-job", registry, id.ToString("N"), .. approval is { } selected ? new[] { selected.ToString("N") } : []]);

    internal static Process StartVerification(string registry, Guid id, string generation) =>
        Launch(["--verify-product-job", registry, id.ToString("N"), generation]);

    internal static Process StartRestoration(string registry, Guid id, Guid request) =>
        Launch(["--restore-product-job", registry, id.ToString("N"), request.ToString("N")]);

    private static Process Launch(string[] arguments)
    {
        string executable = Environment.ProcessPath ?? throw new IOException("No se pudo localizar el ejecutor.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("No se pudo iniciar el trabajo.");
    }
}
