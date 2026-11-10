using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

internal enum CloudProvider { AwsS3, BackblazeB2, CompatibleS3, Filen, Mega }

internal sealed record CloudDestination(CloudProvider Provider, string Endpoint, string Region, string Bucket, string Prefix = "")
{
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
        if (Password.Length is < 1 or > 1024 || AccessKey.Length is < 1 or > 1024 || SecretKey.Length is < 1 or > 2048 ||
            SessionToken?.Length > 16_384 || Password.Contains('\0') || AccessKey.Any(char.IsControl) ||
            SecretKey.Any(char.IsControl) || SessionToken?.Any(char.IsControl) == true)
            throw new InvalidDataException("Las credenciales de nube están incompletas o contienen caracteres inválidos.");
        return new(AccessKey, SecretKey, SessionToken);
    }
}

internal sealed record CloudConnection(string Repository, CloudDestination Destination);

internal static class ProductCloudDispatch
{
    internal static bool HasWork(ProductJobRegistry registry, ProductJob job) =>
        job.Enabled && !job.Retired && registry.HasCloudConnection(job.Id);

    internal static async Task RunPassAsync(ProductJobRegistry registry, string kopia, CancellationToken token, bool lab = false)
    {
        using var lease = registry.AcquireCloudDispatcher();
        var history = new ProductJobRegistry.DisplayRunHistory();
        foreach (var job in registry.List().Jobs)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!HasWork(registry, job)) continue;
                var connection = registry.ReadCloudConnection(job.Id);
                if (connection is null) continue;
                registry.DiscoverCloudTransfers(job.Id, connection.Repository, history);
                var next = registry.CloudTransfers(job.Id, connection.Repository).FirstOrDefault(transfer =>
                    transfer.State == CloudTransferState.Pending ||
                    transfer.State == CloudTransferState.Retry && transfer.RetryUtc <= DateTimeOffset.UtcNow);
                if (next is null) continue;
                (CloudConnection Connection, CloudCredentials Credentials)? saved;
                try { saved = registry.OpenCloudConnection(job.Id); }
                catch (Exception error) when (VersionJob.Expected(error))
                {
                    registry.RecordCloudConnectionFailure(next.Ticket, CloudTransferIssue.Credentials, retry: false);
                    continue;
                }
                if (saved is not { } current || current.Connection != connection) continue;
                using var session = new CloudRepositorySession(kopia, connection.Destination, current.Credentials, lab);
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
                    registry.RecordCloudConnectionFailure(next.Ticket, CloudTransferIssue.Repository, error is not InvalidDataException);
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
    private readonly DirectoryInfo scratch = Directory.CreateTempSubdirectory("FolderTwin-CloudConnection-");
    internal KopiaRepository Repository { get; }
    private readonly CloudDestination destination;
    private readonly bool lab;

    internal CloudRepositorySession(string kopia, CloudDestination destination, CloudCredentials credentials, bool lab = false)
    {
        this.destination = destination;
        this.lab = lab;
        Repository = new(kopia, Path.Combine(scratch.FullName, "repository.config"), credentials.Password, credentials.S3());
    }

    internal async Task<CloudConnection> OpenAsync(bool create, bool readOnly, CancellationToken token)
    {
        if (create && readOnly) throw new ArgumentException("A new repository requires write access.");
        await Repository.RunAsync(token, ["repository", create ? "create" : "connect", .. destination.S3Arguments(lab),
            "--cache-directory", Path.Combine(scratch.FullName, "cache"), "--max-list-cache-duration=0s",
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

    public void Dispose()
    {
        // Only the fresh scratch directory allocated by this session is owned here.
        using (var fence = new DirectoryFence(scratch.FullName)) { }
        scratch.Delete(recursive: true);
    }
}

internal sealed partial class ProductJobRegistry
{
    private sealed record CloudConnectionRevision(int Version, long Sequence, byte[] Previous, CloudConnection Connection, byte[] ProtectedCredentials);
    private sealed record CloudRestoreReceipt(int Version, string Repository, string Manifest, DateTimeOffset FinishedUtc);

    internal bool HasCloudConnection(Guid jobId)
    {
        using var job = OpenJob(jobId);
        return DirectoryPresent(job, "cloud-connection");
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
        _ = connection.Destination.S3Arguments(lab);
        _ = credentials.S3();
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
            _ = credentials.S3();
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
