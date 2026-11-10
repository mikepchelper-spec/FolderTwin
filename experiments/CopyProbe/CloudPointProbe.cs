using System.Security.Cryptography;

internal static class CloudPointProbe
{
    internal static async Task<int> RunAsync(string kopia)
    {
        var fixture = LabWorkspace.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        bool completed = false;
        try
        {
            string repository = Create("repository");
            string password = Guid.NewGuid().ToString("N");
            var writer = new KopiaRepository(kopia, Path.Combine(Create("writer"), "repository.config"), password);
            await writer.RunAsync(token, "repository", "create", "filesystem", "--path", repository, "--no-enable-actions");
            await writer.RunAsync(token, "maintenance", "set", "--enable-quick=false", "--enable-full=false");
            // An empty list inherits .kopiaignore. Use a name that cannot be a
            // file entry in the verified Windows tree.
            await writer.RunAsync(token, "policy", "set", "--global", "--add-dot-ignore", "FolderTwin:disabled",
                "--clear-ignore", "--ignore-cache-dirs=false", "--ignore-unknown-types=false");
            await writer.RunAsync(token, "policy", "set", "--global", "--remove-dot-ignore", ".kopiaignore");
            foreach (var mode in Enum.GetValues<ProductBackupMode>())
            {
                string source = Create($"{mode}-source");
                string targets = Create($"{mode}-targets");
                using var registry = new ProductJobRegistry(Create($"{mode}-jobs"));
                var job = registry.Create(new($"Synthetic {mode}", mode, null),
                    LocalJobRoots.ReviewDedicatedDestination(source, targets));
                var published = new List<(Guid Operation, KopiaPoint Point, byte[] Hash, bool KeepsRemovedFile)>();
                for (int number = 1; number <= 2; number++)
                {
                    string payload = Create($"{mode}-payload-{number}");
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
                    var point = VersionStore.ExportConfirmedPoint(registry, job.Id, backup.Generation!, payload, token);
                    var operation = Guid.NewGuid();
                    bool lostReceipt = false;
                    try
                    {
                        await writer.PublishAsync(operation, point, payload, token, () => throw new IOException("Synthetic lost receipt."));
                    }
                    catch (IOException error) when (error.Message == "Synthetic lost receipt.") { lostReceipt = true; }
                    Check(lostReceipt, "The remote publication checkpoint was not reached.");
                    var remote = await writer.PublishAsync(operation, point, payload, token,
                        () => throw new InvalidOperationException("The retry created a second snapshot."));
                    published.Add((operation, remote, hash, number == 1 || mode == ProductBackupMode.Accumulative));
                }
                Directory.Move(source, Path.Combine(fixture.Root, $"{mode}-source-aside"));
                Directory.Move(targets, Path.Combine(fixture.Root, $"{mode}-targets-aside"));
                var reader = new KopiaRepository(kopia, Path.Combine(Create($"{mode}-reader"), "repository.config"), password);
                await reader.RunAsync(token, "repository", "connect", "filesystem", "--path", repository, "--readonly",
                    "--cache-directory", Create($"{mode}-reader-cache"));
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

    private static void Check(bool valid, string detail)
    {
        if (!valid) throw new InvalidDataException(detail);
    }
}
