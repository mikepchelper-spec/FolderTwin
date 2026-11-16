using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

internal static class CloudRecoveryProbe
{
    private const string RegistryName = "remote-recovery-jobs";

    internal static async Task<int> HeldChildAsync(string root, Guid id, string kopia)
    {
        var fixture = LabWorkspace.Open(root);
        using var registry = new ProductJobRegistry(Path.Combine(fixture.Root, RegistryName));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await registry.RunCloudRecoveryAsync(id, (connection, credentials) =>
        {
            if (connection.Destination.Provider != CloudProvider.CompatibleS3 ||
                !IPEndPoint.TryParse(connection.Destination.Endpoint, out var endpoint) ||
                !endpoint.Address.Equals(IPAddress.Loopback) || endpoint.Port == 0)
                throw new InvalidDataException("The synthetic recovery child requires an IPv4 loopback repository.");
            return new(kopia, connection.Destination, credentials, lab: true);
        }, deadline.Token, lab: true);
        return result.State == CloudRecoveryState.Cancelled ? 0 : 1;
    }

    internal static async Task<int> RunAsync(string kopia)
    {
        var fixture = LabWorkspace.Create();
        bool passed = false;
        try
        {
            string settings = Folder(RegistryName);
            string output = Folder("recovery-output");
            string source = Folder("backup-source");
            string targets = Folder("backup-targets");
            File.WriteAllText(Path.Combine(source, "synthetic.txt"), "Local backup stays independent.");
            using var registration = new ProductJobRegistry(settings);
            var job = registration.Create(new("Independent backup", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, targets));
            bool hasLocal = false;
            foreach (bool terminate in new[] { false, true })
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var (connection, point, credentials) = Synthetic(listener);
                var request = registration.ApproveCloudRecovery(connection, point, credentials, output, lab: true);
                using var reopened = new ProductJobRegistry(settings);
                Check(Find(reopened, request.Id).State == CloudRecoveryState.Queued, "Reopening lost the admitted request.");
                string intentFolder = Path.Combine(settings, ".cloud-recovery-runs", request.Id.ToString("N"));
                foreach (string path in Directory.EnumerateFiles(intentFolder))
                {
                    string raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
                    Check(!raw.Contains(credentials.Password, StringComparison.Ordinal) && !raw.Contains(credentials.AccessKey, StringComparison.Ordinal) &&
                        !raw.Contains(credentials.SecretKey, StringComparison.Ordinal), "A recovery record contains plaintext credentials.");
                }
                using var worker = Launch(fixture.Root, request.Id, kopia);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    using var held = await listener.AcceptTcpClientAsync(timeout.Token);
                    Check(!worker.HasExited && Find(reopened, request.Id) is
                        { State: CloudRecoveryState.Running, Progress.Phase: WorkPhase.Preparing } running &&
                        running.Request.Output == request.Output && Directory.Exists(request.Output),
                        "The reader cannot see the live process, phase and output.");
                    string aliasSource = output;
                    Reject(() => reopened.Create(new("Overlap", ProductBackupMode.Versions, null),
                        LocalJobRoots.ReviewDedicatedDestination(aliasSource, targets)));
                    if (!hasLocal)
                    {
                        Check(ProductRunner.Run(reopened, job.Id).State == ProductRunState.Confirmed,
                            "An independent copy was blocked by a pending cloud recovery.");
                        hasLocal = true;
                    }
                    if (terminate) worker.Kill();
                    else reopened.CancelCloudRecovery(request.Id, lab: true);
                    await worker.WaitForExitAsync(timeout.Token);
                    var outcome = terminate ? CloudRecoveryState.Interrupted : CloudRecoveryState.Cancelled;
                    var final = Find(reopened, request.Id);
                    // Windows may still be releasing the file handles immediately after exit.
                    while (final.State == CloudRecoveryState.Running)
                    {
                        await Task.Delay(20, timeout.Token);
                        final = Find(reopened, request.Id);
                    }
                    Check(final.State == outcome && Directory.Exists(request.Output),
                        $"The worker exited with {worker.ExitCode}. Expected {outcome}, observed {final.State}, output exists={Directory.Exists(request.Output)}.");
                    if (!terminate) Check(worker.ExitCode == 0 && final.CancellationRequested &&
                        !File.Exists(Path.Combine(intentFolder, "credential")), "Cancellation did not stop Kopia or remove its protected credential.");
                    else
                    {
                        reopened.CancelCloudRecovery(request.Id, lab: true);
                        Check(Find(reopened, request.Id).State == CloudRecoveryState.Cancelled, "Interrupted attempt could not be dismissed.");
                    }
                }
                finally
                {
                    if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); }
                }
            }
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                var (connection, point, credentials) = Synthetic(listener);
                string parent = Folder("replaceable-parent");
                var request = registration.ApproveCloudRecovery(connection, point, credentials, parent, lab: true);
                string displaced = Path.Combine(fixture.Root, "old-parent");
                Directory.Move(parent, displaced);
                Directory.CreateDirectory(parent);
                var result = await registration.RunCloudRecoveryAsync(request.Id, (_, _) =>
                    throw new InvalidOperationException("A substituted output parent reached the repository."), CancellationToken.None, lab: true);
                Check(result.State == CloudRecoveryState.Review && !Directory.Exists(request.Output),
                    "A substituted parent did not leave a safe review result.");
                var occupied = registration.ApproveCloudRecovery(connection, point, credentials, output, lab: true);
                Directory.CreateDirectory(occupied.Output);
                File.WriteAllText(Path.Combine(occupied.Output, "occupied.txt"), "preserve");
                var refused = await registration.RunCloudRecoveryAsync(occupied.Id, (_, _) =>
                    throw new InvalidOperationException("An occupied output reached the repository."), CancellationToken.None, lab: true);
                Check(refused.State == CloudRecoveryState.Review &&
                    File.ReadAllText(Path.Combine(occupied.Output, "occupied.txt")) == "preserve", "An existing output was touched.");
            }
            passed = true;
            Console.WriteLine("PASS background cloud recovery: DPAPI, live phase/output, cancellation across processes, worker termination, disjoint local backup, overlap rejection and substituted/occupied output.");
            return 0;
        }
        finally
        {
            if (passed)
            {
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
            else Console.Error.WriteLine($"Synthetic cloud recovery fixture preserved at {fixture.Root}.");
        }

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    private static CloudRecoveryView Find(ProductJobRegistry registry, Guid id) =>
        registry.ReadCloudRecoveries(lab: true).Runs.Single(view => view.Request.Id == id);

    private static (CloudConnection, CloudRecoveryPoint, CloudCredentials) Synthetic(TcpListener listener)
    {
        var connection = new CloudConnection(new string('a', 64),
            new(CloudProvider.CompatibleS3, listener.LocalEndpoint.ToString()!, "us-east-1", "foldertwin"));
        var point = new CloudRecoveryPoint(Guid.NewGuid(), VersionStore.NewGenerationId(),
            new(new string('b', 32), "k" + new string('c', 32), DateTimeOffset.UtcNow, 8, 1, 1));
        return (connection, point, new("pw-" + Guid.NewGuid().ToString("N"),
            "key-" + Guid.NewGuid().ToString("N"), "secret-" + Guid.NewGuid().ToString("N")));
    }

    private static Process Launch(string root, Guid id, string kopia)
    {
        string executable = Environment.ProcessPath ?? throw new IOException("No synthetic recovery executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in new[] { "--held-cloud-recovery-child", root, id.ToString("N"), kopia }) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("The synthetic cloud recovery child did not start.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("An overlapping recovery output was admitted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
