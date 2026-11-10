using System.IO;
using System.Security.Cryptography;

namespace CopyLab;

internal static class ProductCloudWorker
{
    internal const string KopiaHash = "96c0946314b50dcc7c5ae9d3ec7e3763e742fcebf7d5cbebdaac3ceee5fb3bd7";

    internal static string ReviewedKopia()
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "cloud-tools", "kopia.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Este paquete necesita la herramienta de nube incluida para conectarse.");
        using var fence = new DirectoryFence(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), executable);
        using var file = WindowsProbeNative.OpenPlainFile(fence.Parent(executable), executable, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexStringLower(SHA256.HashData(file)).Equals(KopiaHash, StringComparison.Ordinal))
            throw new InvalidDataException("La herramienta de nube difiere de la versión verificada.");
        return executable;
    }

    internal static async Task<int> RunAsync(string location)
    {
        try
        {
            using var registry = new ProductJobRegistry(location);
            await ProductCloudDispatch.RunPassAsync(registry, ReviewedKopia(), CancellationToken.None);
            return 0;
        }
        catch (Exception error) when (VersionJob.Expected(error)) { return 1; }
    }
}
