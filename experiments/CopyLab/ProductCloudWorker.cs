using System.IO;
using System.Security.Cryptography;

namespace CopyLab;

internal static class ProductCloudWorker
{
    internal const string KopiaHash = "96c0946314b50dcc7c5ae9d3ec7e3763e742fcebf7d5cbebdaac3ceee5fb3bd7";
    internal const string RcloneHash = "acd80683e23151e93bbe0d6ffa74d26e542ec510332f630beecb9944f8d1a623";

    internal static string ReviewedKopia() => ReviewedTool("kopia.exe", KopiaHash);
    internal static string ReviewedRclone() => ReviewedTool("rclone.exe", RcloneHash);

    private static string ReviewedTool(string name, string hash)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "cloud-tools", name);
        if (!File.Exists(executable)) throw new FileNotFoundException("Este paquete necesita la herramienta de nube incluida para conectarse.");
        using var fence = new DirectoryFence(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), executable);
        using var file = WindowsProbeNative.OpenPlainFile(fence.Parent(executable), executable, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexStringLower(SHA256.HashData(file)).Equals(hash, StringComparison.Ordinal))
            throw new InvalidDataException("La herramienta de nube difiere de la versión verificada.");
        return executable;
    }

    internal static async Task<int> RunAsync(string location)
    {
        try
        {
            using var registry = new ProductJobRegistry(location);
            await ProductCloudDispatch.RunPassAsync(registry, ReviewedKopia(), CancellationToken.None, rclone: ReviewedRclone);
            return 0;
        }
        catch (Exception error) when (VersionJob.Expected(error)) { return 1; }
    }

    internal static async Task<int> RecoverAsync(string location, Guid id)
    {
        try
        {
            using var registry = new ProductJobRegistry(location);
            using var deadline = new CancellationTokenSource(TimeSpan.FromHours(2));
            var result = await registry.RunCloudRecoveryAsync(id, (connection, credentials) =>
                new(ReviewedKopia(), connection.Destination, credentials, rclone: ReviewedRclone), deadline.Token);
            return result.State == CloudRecoveryState.Completed ? 0 : 1;
        }
        catch (Exception error) when (VersionJob.Expected(error)) { return 1; }
    }
}
