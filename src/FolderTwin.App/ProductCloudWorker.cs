using System.IO;
using System.Security.Cryptography;

namespace FolderTwin;

internal static class ProductCloudWorker
{
    internal const string KopiaHash = "96c0946314b50dcc7c5ae9d3ec7e3763e742fcebf7d5cbebdaac3ceee5fb3bd7";
    internal const string RcloneHash = "acd80683e23151e93bbe0d6ffa74d26e542ec510332f630beecb9944f8d1a623";

    internal static string ReviewedKopia()
    {
        using var pinned = OpenPinnedKopia();
        return pinned.Executable;
    }

    internal static string ReviewedRclone()
    {
        using var pinned = OpenPinnedRclone();
        return pinned.Executable;
    }

    internal static PinnedTool OpenPinnedKopia() => OpenPinnedTool("kopia.exe", KopiaHash);
    internal static PinnedTool OpenPinnedRclone() => OpenPinnedTool("rclone.exe", RcloneHash);

    internal sealed class PinnedTool(string executable, DirectoryFence fence, FileStream stream) : IDisposable
    {
        internal string Executable { get; } = executable;
        public void Dispose()
        {
            stream.Dispose();
            fence.Dispose();
        }
    }

    private static PinnedTool OpenPinnedTool(string name, string hash)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "cloud-tools", name);
        if (!File.Exists(executable)) throw new FileNotFoundException("Este paquete necesita la herramienta de nube incluida para conectarse.");
        var fence = new DirectoryFence(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), executable);
        FileStream? file = null;
        try
        {
            // Keep FileShare.Read open during the worker session so the binary cannot be replaced or modified after verification (TOCTOU).
            file = WindowsProbeNative.OpenPlainFile(fence.Parent(executable), executable, FileAccess.Read, FileShare.Read);
            if (!Convert.ToHexStringLower(SHA256.HashData(file)).Equals(hash, StringComparison.Ordinal))
                throw new InvalidDataException("La herramienta de nube difiere de la versión verificada.");
            return new PinnedTool(executable, fence, file);
        }
        catch
        {
            file?.Dispose();
            fence.Dispose();
            throw;
        }
    }

    internal static async Task<int> RunAsync(string location)
    {
        try
        {
            using var registry = new ProductJobRegistry(location);
            PinnedTool kopia;
            try { kopia = OpenPinnedKopia(); }
            catch (Exception toolError) when (VersionJob.Expected(toolError))
            {
                Console.Error.WriteLine("Cloud worker tool verification failed: " + toolError.Message);
                foreach (var job in registry.List().Jobs)
                    if (ProductCloudDispatch.CanSchedule(registry, job) && registry.ReadCloudConnection(job.Id) is { } conn)
                        try
                        {
                            if (registry.CloudTransfers(job.Id, conn.Repository).FirstOrDefault(item =>
                                item.State is CloudTransferState.Pending or CloudTransferState.Retry) is { } pending)
                                registry.RecordCloudConnectionFailure(pending.Ticket, CloudTransferIssue.Repository, retry: false);
                        }
                        catch (Exception recordError) when (VersionJob.Expected(recordError)) { }
                return 1;
            }
            using (kopia)
            {
                PinnedTool? rclone = null;
                try
                {
                    await ProductCloudDispatch.RunPassAsync(registry, kopia.Executable, CancellationToken.None,
                        rclone: () => (rclone ??= OpenPinnedRclone()).Executable);
                    return 0;
                }
                finally { rclone?.Dispose(); }
            }
        }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            Console.Error.WriteLine("Cloud worker pass failed: " + error.Message);
            return 1;
        }
    }

    internal static async Task<int> RecoverAsync(string location, Guid id)
    {
        try
        {
            using var registry = new ProductJobRegistry(location);
            using var deadline = new CancellationTokenSource(TimeSpan.FromHours(2));
            using var kopia = OpenPinnedKopia();
            PinnedTool? rclone = null;
            try
            {
                var result = await registry.RunCloudRecoveryAsync(id, (connection, credentials) =>
                    new(kopia.Executable, connection.Destination, credentials, rclone: () => (rclone ??= OpenPinnedRclone()).Executable),
                    deadline.Token);
                return result.State == CloudRecoveryState.Completed ? 0 : 1;
            }
            finally { rclone?.Dispose(); }
        }
        catch (Exception error) when (VersionJob.Expected(error)) { return 1; }
    }
}
