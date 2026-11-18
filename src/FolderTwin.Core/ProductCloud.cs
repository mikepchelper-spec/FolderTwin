using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FolderTwin.Core;


internal enum CloudProvider { AwsS3, BackblazeB2, CompatibleS3, Filen, Mega }

internal sealed record CloudDestination(CloudProvider Provider, string Endpoint, string Region, string Bucket, string Prefix = "")
{
    internal bool UsesRclone => Provider is CloudProvider.Filen or CloudProvider.Mega;

    internal void Validate(bool lab)
    {
        if (!UsesRclone) { _ = S3Arguments(lab); return; }
        if (Endpoint != "" || Region != "" || Bucket != "" || Prefix.Length is < 2 or > 200 ||
            !Prefix.EndsWith('/') || Prefix[..^1].Split('/').Any(part => part.Length == 0 ||
                part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
            throw new InvalidDataException("Elige una carpeta dedicada a FolderTwin. Usa letras, números, guiones y barras.");
    }

    internal string[] S3Arguments(bool lab = false)
    {
        if (!Enum.IsDefined(Provider) || Provider is CloudProvider.Filen or CloudProvider.Mega ||
            !Uri.TryCreate("https://" + Endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            !Endpoint.Equals(uri.Authority, StringComparison.OrdinalIgnoreCase) || !lab && uri.IsLoopback ||
            Bucket.Length is < 3 or > 63 || !Bucket.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-') ||
            Region.Length is < 1 or > 64 || !Region.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ||
            Prefix.Length > 200 || Prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '/')) ||
            Prefix.StartsWith('/') || Prefix.Length != 0 && !Prefix.EndsWith('/') ||
            Provider == CloudProvider.AwsS3 && Endpoint != $"s3.{Region}.amazonaws.com" ||
            Provider == CloudProvider.BackblazeB2 && Endpoint != $"s3.{Region}.backblazeb2.com")
            throw new InvalidDataException("Revisa el servidor, la región, el bucket y la carpeta del repositorio.");
        return ["s3", "--bucket", Bucket, "--endpoint", Endpoint, "--region", Region, "--prefix=" + Prefix,
            "--access-key=", "--secret-access-key=", "--session-token=", .. lab ? new[] { "--disable-tls" } : []];
    }
}

internal sealed class CloudCredentials(string password, string accessKey, string secretKey, string? sessionToken = null)
{
    public string Password { get; } = password;
    public string AccessKey { get; } = accessKey;
    public string SecretKey { get; } = secretKey;
    public string? SessionToken { get; } = sessionToken;

    internal KopiaS3Credentials S3()
    {
        Validate();
        return new(AccessKey, SecretKey, SessionToken);
    }

    internal void Validate(CloudProvider? provider = null)
    {
        if (Password.Length is < 1 or > 1024 || AccessKey.Length is < 1 or > 1024 || SecretKey.Length is < 1 or > 2048 ||
            SessionToken?.Length > 16_384 || Password.Contains('\0') || AccessKey.Any(char.IsControl) ||
            SecretKey.Any(char.IsControl) || SessionToken?.Any(char.IsControl) == true ||
            provider == CloudProvider.Filen && string.IsNullOrWhiteSpace(SessionToken) ||
            provider == CloudProvider.Mega && SessionToken is not null)
            throw new InvalidDataException("Las credenciales de nube están incompletas o contienen caracteres inválidos.");
    }
}

internal sealed record CloudConnection(string Repository, CloudDestination Destination);

internal static class ProductCloudDispatch
{
    internal static bool HasWork(ProductJobRegistry registry, ProductJob job) =>
        job.Enabled && !job.Retired && registry.HasCloudConnection(job.Id) && !registry.IsCloudPaused(job.Id);

    internal static bool CanSchedule(ProductJobRegistry registry, ProductJob job)
    {
        try { return HasWork(registry, job); }
        catch (Exception error) when (VersionJob.SharingViolation(error))
        {
            // Keep a cloud-only monitor alive while a concurrent control is committed.
            return true;
        }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            // Ruta and the tray report the damaged record from the same registry.
            // It must not take down the scheduler or activate an uncertain cloud send.
            return false;
        }
    }

    internal static async Task RunPassAsync(ProductJobRegistry registry, string kopia, CancellationToken token, bool lab = false,
        Func<string>? rclone = null, string? labRcloneRoot = null)
    {
        using var lease = registry.AcquireCloudDispatcher();
        var history = new ProductJobRegistry.DisplayRunHistory();
        foreach (var job in registry.List().Jobs)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!HasWork(registry, job))
                {
                    registry.CleanupIdleJobCloudStage(job.Id);
                    continue;
                }
                var connection = registry.ReadCloudConnection(job.Id);
                if (connection is null) continue;
                registry.DiscoverCloudTransfers(job.Id, connection.Repository, history);
                var next = registry.CloudTransfers(job.Id, connection.Repository).FirstOrDefault(transfer =>
                    transfer.State == CloudTransferState.Pending ||
                    transfer.State == CloudTransferState.Retry && transfer.RetryUtc <= DateTimeOffset.UtcNow);
                if (next is null)
                {
                    registry.CleanupIdleJobCloudStage(job.Id);
                    continue;
                }
                (CloudConnection Connection, CloudCredentials Credentials)? saved;
                try { saved = registry.OpenCloudConnection(job.Id); }
                catch (Exception error) when (VersionJob.Expected(error))
                {
                    registry.RecordCloudConnectionFailure(next.Ticket, CloudTransferIssue.Credentials, retry: false);
                    continue;
                }
                if (saved is not { } current || current.Connection != connection) continue;
                using var session = new CloudRepositorySession(kopia, connection.Destination, current.Credentials,
                    lab, rclone, labRcloneRoot);
                using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectDeadline.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    if (await session.OpenAsync(create: false, readOnly: false, connectDeadline.Token) != connection)
                        throw new InvalidDataException("El destino remoto ya no contiene el repositorio revisado.");
                }
                catch (Exception error) when (!token.IsCancellationRequested &&
                    (VersionJob.Expected(error) || error is OperationCanceledException))
                {
                    var issue = error is CloudProviderException provider ? provider.Issue : CloudTransferIssue.Repository;
                    registry.RecordCloudConnectionFailure(next.Ticket, issue,
                        issue == CloudTransferIssue.Repository && error is not InvalidDataException);
                    continue;
                }
                await registry.ProcessCloudTransferAsync(next.Ticket, session.Repository, token);
            }
            catch (Exception error) when (VersionJob.Expected(error))
            {
                // A damaged job or a competing local operation must not block other jobs.
                Console.Error.WriteLine($"La cola de nube requiere revisión para el trabajo {job.Id:N}.");
            }
        }
    }
}

internal sealed class CloudRepositorySession : IDisposable
{
    private const string ScratchPrefix = "FolderTwin-CloudConnection-";
    private readonly DirectoryInfo scratch;
    private readonly FileStream? sessionLock;
    private KopiaRepository? repository;
    internal KopiaRepository Repository => repository ?? throw new InvalidOperationException("La conexión de nube aún no está abierta.");
    private readonly CloudDestination destination;
    private readonly bool lab;
    private readonly string kopia;
    private readonly CloudCredentials credentials;
    private readonly Func<string>? rclone;
    private readonly string? labRcloneRoot;
    private RcloneGateway? gateway;

    internal CloudRepositorySession(string kopia, CloudDestination destination, CloudCredentials credentials, bool lab = false,
        Func<string>? rclone = null, string? labRcloneRoot = null)
    {
        SweepOrphanedScratchDirectories();
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        using (var tempFence = new DirectoryFence(tempRoot))
            LabWorkspace.RequireSpace(64L * 1024 * 1024, LabWorkspace.AvailableSpace(tempFence.Root));
        scratch = Directory.CreateTempSubdirectory(ScratchPrefix);
        try
        {
            using var scratchFence = new DirectoryFence(scratch.FullName);
            sessionLock = WindowsProbeNative.OpenPlainFile(scratchFence.Root, "session.lock", FileAccess.ReadWrite, FileShare.None, FileMode.CreateNew);
        }
        catch
        {
            try { scratch.Delete(recursive: true); } catch { }
            throw;
        }
        this.kopia = kopia;
        this.destination = destination;
        this.credentials = credentials;
        this.lab = lab;
        this.rclone = rclone;
        this.labRcloneRoot = labRcloneRoot;
    }

    private static void SweepOrphanedScratchDirectories()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-6);
            foreach (string candidate in Directory.EnumerateDirectories(Path.GetTempPath(), ScratchPrefix + "*"))
            {
                try
                {
                    var info = new DirectoryInfo(candidate);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.CreationTimeUtc > cutoff) continue;
                    using (var fence = new DirectoryFence(candidate))
                    {
                        if (WindowsProbeNative.PlainFileExists(fence.Root, "session.lock"))
                        {
                            using var probe = WindowsProbeNative.OpenPlainFile(fence.Root, "session.lock", FileAccess.ReadWrite, FileShare.None);
                        }
                    }
                    Directory.Delete(candidate, recursive: true);
                }
                catch { /* Active or inaccessible temp session; leave intact. */ }
            }
        }
        catch { }
    }

    internal async Task<CloudConnection> OpenAsync(bool create, bool readOnly, CancellationToken token)
    {
        if (create && readOnly) throw new ArgumentException("A new repository requires write access.");
        if (repository is not null) throw new InvalidOperationException("La conexión ya se abrió.");
        destination.Validate(lab);
        credentials.Validate(destination.Provider);
        var s3 = destination;
        if (destination.UsesRclone)
        {
            gateway = await RcloneGateway.OpenAsync(rclone?.Invoke() ?? throw new FileNotFoundException("Este paquete necesita rclone."),
                destination, credentials, create, readOnly, token, lab ? labRcloneRoot : null);
            s3 = new(CloudProvider.CompatibleS3, gateway.Endpoint, "us-east-1", "foldertwin");
        }
        repository = new(kopia, Path.Combine(scratch.FullName, "repository.config"), credentials.Password,
            gateway?.Credentials ?? credentials.S3());
        await Repository.RunAsync(token, ["repository", create ? "create" : "connect", .. s3.S3Arguments(lab || gateway is not null),
            "--cache-directory", Path.Combine(scratch.FullName, "cache"), "--content-cache-size-mb=64", "--metadata-cache-size-mb=64",
            "--max-list-cache-duration=0s",
            .. create ? new[] { "--no-enable-actions" } : readOnly ? new[] { "--readonly" } : []]);
        if (create)
        {
            await Repository.RunAsync(token, "maintenance", "set", "--enable-quick=false", "--enable-full=false");
            await Repository.RunAsync(token, "policy", "set", "--global", "--add-dot-ignore", "FolderTwin:disabled",
                "--clear-ignore", "--ignore-cache-dirs=false", "--ignore-unknown-types=false");
            await Repository.RunAsync(token, "policy", "set", "--global", "--remove-dot-ignore", ".kopiaignore");
        }
        return new(await Repository.IdentityAsync(token), destination);
    }

    internal async Task<CatalogSnapshot> ReadRecoveryCatalogAsync(KopiaPoint point, CancellationToken token)
    {
        using var root = new DirectoryFence(scratch.FullName);
        using var catalog = WindowsProbeNative.CreatePlainDirectory(root.Root, "catalog");
        return await Repository.ReadCatalogAsync(point, WindowsProbeNative.VolumePath(catalog), token);
    }

    public void Dispose()
    {
        gateway?.Dispose();
        sessionLock?.Dispose();
        // Only the fresh scratch directory allocated by this session is owned here.
        using (var fence = new DirectoryFence(scratch.FullName)) { }
        scratch.Delete(recursive: true);
    }
}

internal sealed partial class ProductJobRegistry
{
    private const string CloudPauseName = "paused";
    private sealed record CloudConnectionRevision(int Version, long Sequence, byte[] Previous, CloudConnection Connection, byte[] ProtectedCredentials);
    private sealed record CloudRestoreReceipt(int Version, string Repository, string Manifest, DateTimeOffset FinishedUtc);

    internal bool HasCloudConnection(Guid jobId)
    {
        using var job = OpenJob(jobId);
        return DirectoryPresent(job, "cloud-connection");
    }

    internal bool IsCloudPaused(Guid jobId)
    {
        using var job = OpenJob(jobId);
        if (!DirectoryPresent(job, "cloud-connection")) return false;
        using var settings = WindowsProbeNative.OpenPlainDirectory(job, "cloud-connection");
        if (!WindowsProbeNative.PlainFileExists(settings, CloudPauseName)) return false;
        try
        {
            if (Decode<Guid>(settings, CloudPauseName) != jobId)
                throw new InvalidDataException("La pausa de nube corresponde a otro trabajo.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
    }

    internal void SetCloudPaused(Guid jobId, bool paused)
    {
        using var job = OpenJob(jobId);
        using var settings = WindowsProbeNative.OpenPlainDirectory(job, "cloud-connection");
        using var locked = CloudStateLock(settings);
        if (paused)
            WriteCloudRecord(settings, CloudPauseName, Encode(jobId));
        else if (WindowsProbeNative.PlainFileExists(settings, CloudPauseName))
        {
            if (Decode<Guid>(settings, CloudPauseName) != jobId)
                throw new InvalidDataException("La pausa de nube corresponde a otro trabajo.");
            using var marker = WindowsProbeNative.OpenPlainFile(settings, CloudPauseName, FileAccess.Read, FileShare.Delete, rename: true);
            WindowsProbeNative.DeletePlainFile(marker.SafeFileHandle);
        }
    }

    internal CloudConnection? ReadCloudConnection(Guid jobId)
    {
        using var job = OpenJob(jobId);
        if (!DirectoryPresent(job, "cloud-connection")) return null;
        using var settings = WindowsProbeNative.OpenPlainDirectory(job, "cloud-connection");
        return ReadCloudConnection(settings).Revision?.Connection;
    }

    internal void SaveCloudConnection(Guid jobId, CloudConnection connection, CloudCredentials credentials, bool lab = false)
    {
        connection.Destination.Validate(lab);
        credentials.Validate(connection.Destination.Provider);
        if (!RetentionJournal.IsDigest(connection.Repository)) throw new InvalidDataException("El repositorio no tiene una identidad válida.");
        using var job = OpenJob(jobId);
        _ = ReadConfirmed(job, ReadIntent(job, jobId));
        using var settings = WindowsProbeNative.OpenOrCreatePlainDirectory(job, "cloud-connection");
        using var locked = CloudStateLock(settings);
        var (latest, hash) = ReadCloudConnection(settings);
        long sequence = checked((latest?.Sequence ?? 0) + 1);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            byte[] encrypted = CloudSecret.Transform(clear, CloudPurpose(job, connection, sequence), encrypt: true);
            WriteCloudRecord(settings, CloudConnectionName(sequence), Encode(new CloudConnectionRevision(1, sequence, hash, connection, encrypted)));
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    internal (CloudConnection Connection, CloudCredentials Credentials)? OpenCloudConnection(Guid jobId)
    {
        using var job = OpenJob(jobId);
        if (!DirectoryPresent(job, "cloud-connection")) return null;
        using var settings = WindowsProbeNative.OpenPlainDirectory(job, "cloud-connection");
        var (latest, _) = ReadCloudConnection(settings);
        if (latest is null) return null;
        byte[] clear = CloudSecret.Transform(latest.ProtectedCredentials, CloudPurpose(job, latest.Connection, latest.Sequence), encrypt: false);
        try
        {
            var credentials = JsonSerializer.Deserialize<CloudCredentials>(clear) ??
                throw new InvalidDataException("Las credenciales de nube están vacías.");
            credentials.Validate(latest.Connection.Destination.Provider);
            return (latest.Connection, credentials);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    internal FileStream AcquireCloudDispatcher() => CloudLock(directory.Root, ".foldertwin-cloud-dispatch.lock");

    internal void RecordCloudRestore(string repository, KopiaPoint point)
    {
        if (!RetentionJournal.IsDigest(repository)) throw new InvalidDataException("El repositorio recuperado no tiene una identidad válida.");
        using var root = WindowsProbeNative.OpenOrCreatePlainDirectory(directory.Root, ".cloud-recoveries");
        using var records = WindowsProbeNative.OpenOrCreatePlainDirectory(root, repository);
        var now = DateTimeOffset.UtcNow;
        string name = $"{now.UtcTicks:D19}-{Guid.NewGuid():N}.restore";
        RetentionJournal.WriteFile(records, name, Encode(new CloudRestoreReceipt(1, repository, point.ManifestId, now)));
    }

    internal DateTimeOffset? LastCloudRestore(string repository)
    {
        if (!RetentionJournal.IsDigest(repository)) throw new ArgumentException("Identidad inválida.", nameof(repository));
        if (!DirectoryPresent(directory.Root, ".cloud-recoveries")) return null;
        using var root = WindowsProbeNative.OpenPlainDirectory(directory.Root, ".cloud-recoveries");
        if (!DirectoryPresent(root, repository)) return null;
        using var records = WindowsProbeNative.OpenPlainDirectory(root, repository);
        string? name = WindowsProbeNative.ReadDirectory(records).Select(item => item.Name)
            .Where(value => value.EndsWith(".restore", StringComparison.Ordinal)).Order(StringComparer.Ordinal).LastOrDefault();
        if (name is null) return null;
        var receipt = Decode<CloudRestoreReceipt>(records, name);
        if (receipt.Version != 1 || receipt.Repository != repository || receipt.FinishedUtc.Offset != TimeSpan.Zero ||
            !name.StartsWith($"{receipt.FinishedUtc.UtcTicks:D19}-", StringComparison.Ordinal))
            throw new InvalidDataException("El historial de recuperación en nube está dañado.");
        return receipt.FinishedUtc;
    }

    internal CloudTicket[] DiscoverCloudTransfers(Guid jobId, string repository, DisplayRunHistory history)
    {
        var local = history.Read(this, jobId);
        if (local.Unreadable != 0) throw new InvalidDataException("Hay intentos locales sin leer. Revisa el historial antes de enviar.");
        var existing = CloudTransfers(jobId, repository).Select(transfer => transfer.Ticket.Generation).ToHashSet(StringComparer.Ordinal);
        var discovered = new List<CloudTicket>();
        foreach (var run in local.Runs.Reverse())
        {
            if (run.Intent.Operation != ProductRunOperation.Backup ||
                run.Result is not { State: ProductRunState.Confirmed, Generation: { } generation } ||
                VersionStore.IsPartialPoint(generation) || !existing.Add(generation))
                continue;
            var ticket = CloudTicket.For(jobId, repository, generation);
            using var entry = OpenCloudEntry(ticket, create: true);
            using var state = CloudStateLock(entry);
            WriteCloudRecord(entry, "intent", Encode(ticket));
            discovered.Add(ticket);
        }
        return discovered.ToArray();
    }

    private static (CloudConnectionRevision? Revision, byte[] Hash) ReadCloudConnection(SafeFileHandle settings)
    {
        CloudConnectionRevision? last = null;
        byte[] hash = new byte[32];
        foreach (var item in WindowsProbeNative.ReadDirectory(settings).Where(item => item.Name.EndsWith(".connection", StringComparison.Ordinal))
            .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var next = Decode<CloudConnectionRevision>(settings, item.Name);
            if (next.Version != 1 || next.Sequence != (last?.Sequence ?? 0) + 1 || item.Name != CloudConnectionName(next.Sequence) ||
                !hash.AsSpan().SequenceEqual(next.Previous) || !RetentionJournal.IsDigest(next.Connection.Repository) ||
                next.ProtectedCredentials.Length is < 1 or > 60_000)
                throw new InvalidDataException("El registro de conexión de nube está incompleto o dañado.");
            last = next;
            hash = SHA256.HashData(Encode(next));
        }
        return (last, hash);
    }

    private static byte[] CloudPurpose(SafeFileHandle job, CloudConnection connection, long sequence) =>
        SHA256.HashData([.. JobStamp(job), .. Encode(connection), .. Encoding.UTF8.GetBytes(CloudConnectionName(sequence))]);

    private static string CloudConnectionName(long sequence) => sequence.ToString("D20", CultureInfo.InvariantCulture) + ".connection";
}
