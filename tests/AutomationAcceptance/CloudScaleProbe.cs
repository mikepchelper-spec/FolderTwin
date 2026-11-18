using System.Diagnostics;
using System.Security.Cryptography;

internal static class CloudScaleProbe
{
    internal static async Task ResumeAsync(string executable, string root)
    {
        var fixture = LabWorkspace.Open(root);
        using var registry = new ProductJobRegistry(Path.Combine(fixture.Root, "registry"));
        var job = registry.List().Jobs.Single();
        var account = registry.OpenCloudConnection(job.Id) ?? throw new InvalidDataException("Missing synthetic credentials.");
        var point = registry.RunHistory(job.Id).Runs.Single(run => run.Result?.State == ProductRunState.Confirmed);
        await RestoreIndependentAsync(executable, fixture, account.Connection, account.Credentials.Password, job.Id, point.Generation!,
            Path.Combine("0000", "000000.bin"), SHA256.HashData(SHA256.HashData(BitConverter.GetBytes(0))), CancellationToken.None);
    }

    internal static async Task RunAsync(string executable, int count)
    {
        string tools = Path.Combine(Path.GetDirectoryName(executable)!, "cloud-tools");
        string kopia = Path.Combine(tools, "kopia.exe"), rclone = Path.Combine(tools, "rclone.exe");
        var fixture = LabWorkspace.Create();
        bool passed = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(count == 4096 ? 5 : 30));
        var token = deadline.Token;
        try
        {
            string source = Folder("source"), target = Folder("backup"), remote = Folder("remote");
            for (int index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                string group = Directory.CreateDirectory(Path.Combine(source, (index / 256).ToString("D4"))).FullName;
                File.WriteAllBytes(Path.Combine(group, $"{index:D6}.bin"), SHA256.HashData(BitConverter.GetBytes(index % 256)));
            }
            string sample = Path.Combine("0000", "000000.bin");
            byte[] expected = SHA256.HashData(File.ReadAllBytes(Path.Combine(source, sample)));
            using var registry = new ProductJobRegistry(Folder("registry"));
            var job = registry.Create(new("Cloud scale synthetic", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, target));
            var timer = Stopwatch.StartNew();
            var backup = ProductRunner.Run(registry, job.Id);
            if (backup is not { State: ProductRunState.Confirmed, Generation: { } generation })
                throw new IOException(backup.Detail ?? "Local scale fixture was not confirmed.");
            var backupElapsed = timer.Elapsed;

            var destination = new CloudDestination(CloudProvider.Mega, "", "", "", "scale/");
            var account = new CloudCredentials(Guid.NewGuid().ToString("N"), "synthetic@example.invalid", Guid.NewGuid().ToString("N"));
            CloudRepositorySession Session() => new(kopia, destination, account, lab: true, () => rclone, remote);
            CloudConnection connection;
            using (var writer = Session())
            {
                connection = await writer.OpenAsync(create: true, readOnly: false, token);
                registry.SaveCloudConnection(job.Id, connection, account, lab: true);
                var ticket = CloudTicket.For(job.Id, connection.Repository, generation);
                string stage = Folder("export");
                timer.Restart();
                CloudPoint point = VersionStore.ExportConfirmedPoint(registry, job.Id, generation, stage, token);
                var exportElapsed = timer.Elapsed;
                long staged = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
                if (point.Files != count || staged != point.Bytes) throw new InvalidDataException("Scale staging included extra records.");
                timer.Restart();
                await writer.Repository.PublishAsync(ticket.Operation, point, stage, token);
                Console.WriteLine($"files={count} staged_bytes={staged} backup_s={backupElapsed.TotalSeconds:F3} export_s={exportElapsed.TotalSeconds:F3} upload_s={timer.Elapsed.TotalSeconds:F3}");
            }

            Directory.Move(source, Path.Combine(fixture.Root, "source-aside"));
            Directory.Move(target, Path.Combine(fixture.Root, "backup-aside"));
            string downloaded = Folder("independent-repository");
            string repository = Path.Combine(remote, "scale", "foldertwin");
            foreach (string directory in Directory.EnumerateDirectories(repository, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(downloaded, Path.GetRelativePath(repository, directory)));
            foreach (string file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(downloaded, Path.GetRelativePath(repository, file)));
            Directory.Move(remote, Path.Combine(fixture.Root, "rclone-storage-aside"));
            await RestoreIndependentAsync(executable, fixture, connection, account.Password, job.Id, generation, sample, expected, token);
            passed = true;
        }
        finally
        {
            if (passed) StorageGrowthProbe.RemoveWorkspace(fixture, Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
            else Console.Error.WriteLine($"Synthetic scale fixture preserved at {fixture.Root}.");
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    private static async Task RestoreIndependentAsync(string executable, LabWorkspace fixture, CloudConnection connection,
        string password, Guid job, string generation, string sample, byte[] expected, CancellationToken token)
    {
        string tools = Path.Combine(Path.GetDirectoryName(executable)!, "cloud-tools");
        string archive = Path.Combine(fixture.Root, "independent-repository");
        string served = Directory.CreateDirectory(Path.Combine(fixture.Root, "offline-s3")).FullName;
        string bucket = Path.Combine(served, "foldertwin");
        if (Directory.Exists(archive)) Directory.Move(archive, bucket);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        token = deadline.Token;
        string access = Guid.NewGuid().ToString("N"), secret = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Path.Combine(tools, "rclone.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["RCLONE_AUTH_KEY"] = $"\"{access},{secret}\"";
        foreach (string value in new[] { "serve", "s3", ":local:" + served, "--addr", "127.0.0.1:0",
            "--read-only", "--config", "NUL", "--log-level", "INFO" }) start.ArgumentList.Add(value);
        using var child = Process.Start(start) ?? throw new IOException("Independent rclone did not start.");
        try
        {
            string? endpoint = null;
            while (await child.StandardError.ReadLineAsync(token) is { } line)
            {
                int startUrl = line.IndexOf("http://127.0.0.1:", StringComparison.Ordinal);
                if (startUrl < 0 || !Uri.TryCreate(line[startUrl..].Trim(), UriKind.Absolute, out var address)) continue;
                endpoint = address.Authority;
                break;
            }
            if (endpoint is null) throw new IOException("Independent rclone did not bind.");
            _ = child.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            var reader = new KopiaRepository(Path.Combine(tools, "kopia.exe"),
                Path.Combine(fixture.Root, $"recovery-{Guid.NewGuid():N}.config"), password, new(access, secret));
            await reader.RunAsync(token, "repository", "connect", "s3", "--bucket", "foldertwin", "--endpoint", endpoint,
                "--region", "us-east-1", "--access-key=", "--secret-access-key=", "--disable-tls", "--readonly");
            if (await reader.IdentityAsync(token) != connection.Repository) throw new InvalidDataException("Downloaded repository changed.");
            var recovery = (await reader.ListAsync(token)).Single(item => item.Job == job && item.Generation == generation);
            string restored = Directory.CreateDirectory(Path.Combine(fixture.Root, $"restored-{Guid.NewGuid():N}")).FullName;
            Console.WriteLine($"Independent restore started: files={recovery.Point.Files}");
            var timer = Stopwatch.StartNew();
            await reader.RestoreAsync(recovery.Point, restored, token);
            var elapsed = timer.Elapsed;
            byte[] actual = SHA256.HashData(File.ReadAllBytes(Path.Combine(restored, sample)));
            if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Restored scale sample differs.");
            int files = Directory.EnumerateFiles(restored, "*", SearchOption.AllDirectories).Count();
            if (files != recovery.Point.Files) throw new InvalidDataException("Restored scale file count differs.");
            Console.WriteLine($"independent_restore_s={elapsed.TotalSeconds:F3} restored_files={files} sha256={Convert.ToHexStringLower(actual)}");
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }
}
