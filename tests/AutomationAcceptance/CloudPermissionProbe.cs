using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

internal static class CloudPermissionProbe
{
    internal static async Task RunAsync(string package)
    {
        string kopia = Path.Combine(package, "cloud-tools", "kopia.exe");
        string rclone = Path.Combine(package, "cloud-tools", "rclone.exe");
        var fixture = LabWorkspace.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        bool passed = false;
        try
        {
            string storage = Folder("synthetic-storage"), source = Folder("source"), other = Folder("other");
            File.WriteAllText(Path.Combine(source, "first.txt"), "Synthetic recovery with minimum permissions.");
            File.WriteAllText(Path.Combine(other, "new-write.txt"), "This must not reach the read-only repository.");
            var destination = new CloudDestination(CloudProvider.Filen, "", "", "", "dedicated/");
            var credentials = new CloudCredentials(Guid.NewGuid().ToString("N"), "permission-test@example.invalid",
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            CloudConnection connection;
            using (var writer = new CloudRepositorySession(kopia, destination, credentials, lab: true, () => rclone, storage))
            {
                connection = await writer.OpenAsync(create: true, readOnly: false, token);
                await writer.Repository.RunAsync(token, "snapshot", "create", "--fail-fast", source);
            }
            string sample = Path.Combine(other, "new-write.txt");
            using (var writable = await RcloneGateway.OpenAsync(rclone, destination, credentials, create: false, readOnly: false, token, storage))
                await CheckWriteAsync(rclone, writable, sample, readOnly: false, token);
            var before = Inventory(storage);
            using (var gateway = await RcloneGateway.OpenAsync(rclone, destination, credentials, create: false, readOnly: true, token, storage))
            {
                var address = new CloudDestination(CloudProvider.CompatibleS3, gateway.Endpoint, "us-east-1", "foldertwin");
                var client = new KopiaRepository(kopia, Path.Combine(Folder("config"), "repository.config"),
                    credentials.Password, gateway.Credentials);
                await client.RunAsync(token, ["repository", "connect", .. address.S3Arguments(lab: true)]);
                if (await client.IdentityAsync(token) != connection.Repository)
                    throw new InvalidDataException("The read-only gateway opened another repository.");
                using var items = JsonDocument.Parse(await client.RunAsync(token, "snapshot", "list", "--all", "--json"));
                string root = items.RootElement.EnumerateArray().Single().GetProperty("rootEntry").GetProperty("obj").GetString()!;
                string restored = Folder("restored");
                await client.RunAsync(token, "snapshot", "restore", root, restored);
                if (File.ReadAllText(Path.Combine(restored, "first.txt")) != File.ReadAllText(Path.Combine(source, "first.txt")))
                    throw new InvalidDataException("Read-only S3 access did not restore the expected bytes.");
                await CheckWriteAsync(rclone, gateway, sample, readOnly: true, token);
            }
            if (!before.SequenceEqual(Inventory(storage)))
                throw new InvalidDataException("The read-only gateway changed repository bytes.");
            passed = true;
            Console.WriteLine("PASS read-only gateway allowed status, list and restoration and rejected a write from a read-write client. Remote names and SHA-256 remained identical.");
        }
        finally
        {
            if (passed)
            {
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
            else Console.Error.WriteLine($"Synthetic permissions fixture preserved at {fixture.Root}.");
        }

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    private static async Task CheckWriteAsync(string rclone, RcloneGateway gateway, string input, bool readOnly, CancellationToken token)
    {
        // Use a single authenticated upload with retries bounded. Kopia retries
        // server-side write failures for longer than the fixture deadline.
        var start = new ProcessStartInfo(rclone)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["RCLONE_CONFIG_FTPERMISSION_TYPE"] = "s3";
        start.Environment["RCLONE_CONFIG_FTPERMISSION_PROVIDER"] = "Other";
        start.Environment["RCLONE_CONFIG_FTPERMISSION_ENDPOINT"] = "http://" + gateway.Endpoint;
        start.Environment["RCLONE_CONFIG_FTPERMISSION_REGION"] = "us-east-1";
        start.Environment["RCLONE_CONFIG_FTPERMISSION_ACCESS_KEY_ID"] = gateway.Credentials.AccessKey;
        start.Environment["RCLONE_CONFIG_FTPERMISSION_SECRET_ACCESS_KEY"] = gateway.Credentials.SecretKey;
        string path = readOnly ? "ftpermission:foldertwin/denied-write.txt" : "ftpermission:foldertwin/write-control.txt";
        foreach (string argument in new[] { "--config", "NUL", "copyto", input, path,
            "--s3-no-check-bucket", "--retries", "1", "--low-level-retries", "1" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("The synthetic S3 client did not start.");
        try
        {
            var errors = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await errors;
            if ((process.ExitCode == 0) == readOnly)
                throw new InvalidDataException($"The gateway write did not match the requested read-only mode. Exit code {process.ExitCode}.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }

    private static string[] Inventory(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => $"{Path.GetRelativePath(path, file)} {Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))}").ToArray();
}
