using System.Security.Cryptography;
using System.Net;
using System.Diagnostics;
using System.Reflection;

internal static class CloudPointProbe
{
    internal static async Task<int> RunAsync(string kopia, string? endpoint = null)
    {
        if (endpoint is not null && (!IPEndPoint.TryParse(endpoint, out var address) ||
            !address.Address.Equals(IPAddress.Loopback) || address.Port == 0))
            throw new ArgumentException("The synthetic S3 endpoint must bind to IPv4 loopback.", nameof(endpoint));
        var fixture = LabWorkspace.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        bool completed = false;
        try
        {
            string[] backend = endpoint is null ? ["filesystem", "--path", Create("repository")] :
                ["s3", "--bucket", "foldertwin", "--endpoint", endpoint, "--region", "us-east-1", "--disable-tls",
                    "--access-key=", "--secret-access-key="];
            string password = Guid.NewGuid().ToString("N");
            string writerConfig = Path.Combine(Create("writer"), "repository.config");
            var s3 = endpoint is null ? null : SyntheticS3Credentials();
            var writer = new KopiaRepository(kopia, writerConfig, password, s3);
            await writer.RunAsync(token, ["repository", "create", .. backend, "--no-enable-actions"]);
            string writerJson = File.ReadAllText(writerConfig);
            Check(!writerJson.Contains(password, StringComparison.Ordinal), "The repository password was persisted in plain text.");
            if (s3 is not null)
            {
                Check(!writerJson.Contains(s3.SecretKey, StringComparison.Ordinal) &&
                    !writerJson.Contains(s3.AccessKey, StringComparison.Ordinal), "The S3 credential was persisted in plain text.");
                string reconnectConfig = Path.Combine(Create("reconnect"), "repository.config");
                var revoked = new KopiaRepository(kopia, reconnectConfig, password,
                    new KopiaS3Credentials(s3.AccessKey, Guid.NewGuid().ToString("N")));
                bool rejected = false;
                using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                authTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                try { await revoked.RunAsync(authTimeout.Token, ["repository", "connect", .. backend, "--readonly"]); }
                catch (IOException) { rejected = true; }
                Check(rejected, "S3 accepted the invalid connection key.");
                var renewed = new KopiaRepository(kopia, reconnectConfig, password, s3);
                await renewed.RunAsync(token, ["repository", "connect", .. backend, "--readonly"]);
                Check(await renewed.IdentityAsync(token) == await writer.IdentityAsync(token), "Reconnect selected another repository.");
            }
            await writer.RunAsync(token, "maintenance", "set", "--enable-quick=false", "--enable-full=false");
            // An empty list inherits .kopiaignore. Use a name that cannot be a
            // file entry in the verified Windows tree.
            await writer.RunAsync(token, "policy", "set", "--global", "--add-dot-ignore", "FolderTwin:disabled",
                "--clear-ignore", "--ignore-cache-dirs=false", "--ignore-unknown-types=false");
            await writer.RunAsync(token, "policy", "set", "--global", "--remove-dot-ignore", ".kopiaignore");
            string repositoryId = await writer.IdentityAsync(token);
            var different = new KopiaRepository(kopia, Path.Combine(Create("other-writer"), "repository.config"), password);
            await different.RunAsync(token, "repository", "create", "filesystem", "--path", Create("other-repository"), "--no-enable-actions");
            foreach (var mode in Enum.GetValues<ProductBackupMode>())
            {
                string source = Create($"{mode}-source");
                string targets = Create($"{mode}-targets");
                string jobs = Create($"{mode}-jobs");
                using var registry = new ProductJobRegistry(jobs);
                var job = registry.Create(new($"Synthetic {mode}", mode, null),
                    LocalJobRoots.ReviewDedicatedDestination(source, targets));
                var published = new List<(Guid Operation, KopiaPoint Point, byte[] Hash, bool KeepsRemovedFile)>();
                for (int number = 1; number <= 2; number++)
                {
                    string sample = Path.Combine(source, "muestra.txt");
                    File.WriteAllText(sample, $"Synthetic point {number}");
                    File.WriteAllText(Path.Combine(source, ".kopiaignore"), "*.txt");
                    Directory.CreateDirectory(Path.Combine(source, "empty"));
                    string removed = Path.Combine(source, "retirar.txt");
                    if (number == 1) File.WriteAllText(removed, "Previous synthetic content");
                    else File.Delete(removed);
                    byte[] hash = SHA256.HashData(File.ReadAllBytes(sample));
                    Guid? approval = null;
                    if (mode == ProductBackupMode.ProtectedMirror && number == 2)
                    {
                        var preview = VersionStore.OpenProduct(job).PreviewMirror(job.Source.VolumePath);
                        approval = registry.ApproveMirror(job.Id, preview, emptyReviewed: false, massReviewed: true);
                    }
                    var backup = ProductRunner.Run(registry, job.Id, approval);
                    Check(backup is { State: ProductRunState.Confirmed, Generation: not null }, backup.Detail ?? "Local backup failed.");
                    var ticket = registry.EnqueueCloud(job.Id, repositoryId, backup.Generation!);
                    Check(registry.EnqueueCloud(job.Id, repositoryId, backup.Generation!) == ticket &&
                        registry.CloudTransfers(job.Id, repositoryId).Length == number, "Enqueue is not idempotent.");
                    if (number == 1 && mode == ProductBackupMode.Accumulative)
                    {
                        var rejected = await registry.ProcessCloudTransferAsync(ticket, different, token);
                        Check(rejected is { State: CloudTransferState.Review, Issue: CloudTransferIssue.Repository } &&
                            await different.FindAsync(ticket.Operation, token) is null, "A different repository accepted the queued point.");
                    }
                    registry.ControlCloudTransfer(ticket, CloudTransferAction.Pause);
                    Check((await registry.ProcessCloudTransferAsync(ticket, writer, token)).State == CloudTransferState.Paused &&
                        await writer.FindAsync(ticket.Operation, token) is null, "A paused item was sent.");
                    registry.ControlCloudTransfer(ticket, CloudTransferAction.Retry);
                    bool terminateWorker = number == 2 && mode == ProductBackupMode.Versions;
                    CloudTransfer interrupted;
                    if (terminateWorker)
                    {
                        await TerminateAfterUploadAsync(fixture.Root, ticket, kopia, password, s3, registry, writer, token);
                        interrupted = registry.ReadCloudTransfer(ticket);
                        Check(interrupted.State == CloudTransferState.Pending, "A terminated worker recorded an outcome.");
                    }
                    else
                    {
                        interrupted = await registry.ProcessCloudTransferAsync(ticket, writer, token, () =>
                        {
                            var local = VersionStore.OpenProduct(job).Verify(backup.Generation!);
                            Check(!local, "The cloud upload kept the local store locked or degraded.");
                            throw new IOException("Synthetic lost receipt.");
                        });
                        Check(interrupted is { State: CloudTransferState.Retry, Attempts: 1, RetryUtc: not null },
                            "A lost receipt did not persist a retry.");
                    }
                    Check(await writer.FindAsync(ticket.Operation, token) is not null, "The worker did not publish remotely.");
                    if (number == 2)
                    {
                        Directory.Move(source, Path.Combine(fixture.Root, $"{mode}-source-aside"));
                        Directory.Move(targets, Path.Combine(fixture.Root, $"{mode}-targets-aside"));
                    }
                    using var reopened = new ProductJobRegistry(jobs);
                    Check(reopened.ReadCloudTransfer(ticket) == interrupted, "Retry state was not durable.");
                    Check(terminateWorker || (await reopened.ProcessCloudTransferAsync(ticket, writer, token)).State == CloudTransferState.Retry,
                        "Backoff was not respected.");
                    reopened.ControlCloudTransfer(ticket, CloudTransferAction.Cancel);
                    Check((await reopened.ProcessCloudTransferAsync(ticket, writer, token)).State == CloudTransferState.Cancelled,
                        "A cancelled item ran without a reattempt.");
                    reopened.ControlCloudTransfer(ticket, CloudTransferAction.Retry);
                    var remote = await reopened.ProcessCloudTransferAsync(ticket, writer, token,
                        () => throw new InvalidOperationException("The retry created a second snapshot."));
                    Check(remote is { State: CloudTransferState.Available, Point: not null } &&
                        reopened.ReadCloudTransfer(ticket) == remote, "A reopened queue lost the remote point.");
                    published.Add((ticket.Operation, remote.Point!, hash, number == 1 || mode == ProductBackupMode.Accumulative));
                }
                var reader = new KopiaRepository(kopia, Path.Combine(Create($"{mode}-reader"), "repository.config"), password, s3);
                await reader.RunAsync(token, ["repository", "connect", .. backend, "--readonly",
                    "--cache-directory", Create($"{mode}-reader-cache")]);
                foreach (var (operation, expected, hash, keepsRemoved) in published)
                {
                    var observed = await reader.FindAsync(operation, token);
                    Check(observed?.ManifestId == expected.ManifestId, "A fresh reader found a different manifest.");
                    string output = Create($"{mode}-recovered-{operation:N}");
                    await reader.RestoreAsync(observed!, output, token);
                    byte[] restored = SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "muestra.txt")));
                    string removed = Path.Combine(output, "retirar.txt");
                    Check(restored.AsSpan().SequenceEqual(hash) && File.ReadAllText(Path.Combine(output, ".kopiaignore")) == "*.txt" &&
                        (keepsRemoved ? File.ReadAllText(removed) == "Previous synthetic content" : !File.Exists(removed)) &&
                        Directory.Exists(Path.Combine(output, "empty")), "The recovered tree differs from the confirmed local tree.");
                    Console.WriteLine($"{mode} restored {expected.ManifestId}, SHA-256 {Convert.ToHexStringLower(restored)}");
                }
            }
            completed = true;
            Console.WriteLine("PASS all local modes published two complete points and recovered them from a separate Kopia connection.");
            return 0;
        }
        finally
        {
            if (completed)
            {
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
            else Console.Error.WriteLine($"Synthetic cloud fixture preserved at {fixture.Root}.");
        }

        string Create(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    internal static async Task<int> HeldChildAsync(string root, CloudTicket ticket, string kopia)
    {
        _ = LabWorkspace.Open(root);
        using var registry = new ProductJobRegistry(Path.Combine(root, "Versions-jobs"));
        string password = Environment.GetEnvironmentVariable("FOLDERTWIN_CLOUD_LAB_PASSWORD")
            ?? throw new InvalidDataException("The synthetic child has no repository password.");
        Environment.SetEnvironmentVariable("FOLDERTWIN_CLOUD_LAB_PASSWORD", null);
        bool s3 = Environment.GetEnvironmentVariable("FOLDERTWIN_CLOUD_LAB_S3") == "true";
        var writer = new KopiaRepository(kopia, Path.Combine(root, "writer", "repository.config"), password,
            s3 ? SyntheticS3Credentials() : null);
        await registry.ProcessCloudTransferAsync(ticket, writer, CancellationToken.None, () =>
        {
            Console.WriteLine("PUBLISHED");
            Console.Out.Flush();
            Console.ReadLine();
        });
        return 0;
    }

    private static async Task TerminateAfterUploadAsync(string root, CloudTicket ticket, string kopia, string password, KopiaS3Credentials? s3,
        ProductJobRegistry registry, KopiaRepository repository, CancellationToken token)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in new[] { "--held-cloud-child", root, ticket.Job.ToString("N"),
            ticket.Repository, ticket.Generation, kopia })
            start.ArgumentList.Add(argument);
        start.Environment["FOLDERTWIN_CLOUD_LAB_PASSWORD"] = password;
        start.Environment["FOLDERTWIN_CLOUD_LAB_S3"] = s3 is null ? "false" : "true";
        using var process = Process.Start(start) ?? throw new IOException("The synthetic cloud worker could not start.");
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            Check(await process.StandardOutput.ReadLineAsync(wait.Token) == "PUBLISHED", "The worker did not reach remote publication.");
            bool busy = false;
            try { await registry.ProcessCloudTransferAsync(ticket, repository, token); }
            catch (Exception error) when (VersionJob.SharingViolation(error)) { busy = true; }
            Check(busy, "Two workers could send the same queued point.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }

    private static KopiaS3Credentials SyntheticS3Credentials() =>
        new(Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") ?? throw new InvalidDataException("Missing synthetic S3 access key."),
            Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") ?? throw new InvalidDataException("Missing synthetic S3 secret."));

    private static void Check(bool valid, string detail)
    {
        if (!valid) throw new InvalidDataException(detail);
    }
}
