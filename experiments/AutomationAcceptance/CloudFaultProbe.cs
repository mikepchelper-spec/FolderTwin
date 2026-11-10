using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class CloudFaultProbe
{
    internal static async Task RunAsync(string executable)
    {
        string kopia = Path.Combine(Path.GetDirectoryName(executable)!, "cloud-tools", "kopia.exe");
        var fixture = LabWorkspace.Create();
        bool passed = false;
        try
        {
            using var registry = new ProductJobRegistry(Folder("registry"));
            string source = Folder("source"), targets = Folder("targets");
            string input = Path.Combine(source, "muestra.txt");
            var job = registry.Create(new("Synthetic transport faults", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, targets));
            foreach (var (status, code) in new (string? Status, string Code)[]
            {
                (null, "connection reset"),
                ("429 Too Many Requests", "SlowDown"),
                ("507 Insufficient Storage", "QuotaExceeded")
            })
            {
                File.WriteAllText(input, code);
                var result = ProductRunner.Run(registry, job.Id);
                if (result is not { State: ProductRunState.Confirmed, Generation: { } generation })
                    throw new IOException("Local backup did not confirm.");
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                using var stop = new CancellationTokenSource();
                int requests = 0;
                var serving = ServeAsync();
                try
                {
                    var destination = new CloudDestination(CloudProvider.CompatibleS3, listener.LocalEndpoint.ToString()!,
                        "us-east-1", "foldertwin");
                    string repository = new('a', 64);
                    registry.SaveCloudConnection(job.Id, new(repository, destination),
                        new("synthetic-password", "synthetic-key", "synthetic-secret"), lab: true);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                    await ProductCloudDispatch.RunPassAsync(registry, kopia, deadline.Token, lab: true);
                    var pending = registry.CloudTransfers(job.Id, repository);
                    if (Volatile.Read(ref requests) == 0 || !pending.Any(item =>
                        item.State == CloudTransferState.Retry && item.Issue == CloudTransferIssue.Repository && item.RetryUtc is not null) ||
                        pending.Any(item => item.State == CloudTransferState.Available))
                        throw new InvalidDataException($"Cloud transport failure did not leave a retry for {code}.");
                    if (!registry.RunHistory(job.Id).Runs.Any(run => run.Result is
                        { State: ProductRunState.Confirmed, Generation: var confirmed } && confirmed == generation))
                        throw new InvalidDataException("The cloud failure changed the local confirmation.");
                    foreach (var transfer in pending)
                        registry.ControlCloudTransfer(transfer.Ticket, CloudTransferAction.Cancel);
                    Console.WriteLine($"PASS {code}: requests={Volatile.Read(ref requests)}. Local confirmation preserved.");
                }
                finally
                {
                    stop.Cancel();
                    await serving;
                }

                async Task ServeAsync()
                {
                    try
                    {
                        while (true)
                        {
                            using var client = await listener.AcceptTcpClientAsync(stop.Token);
                            Interlocked.Increment(ref requests);
                            if (status is null) continue;
                            using var stream = client.GetStream();
                            using var request = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                            while (await request.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                            string body = $"<Error><Code>{code}</Code><Message>Synthetic transport failure</Message></Error>";
                            string response = $"HTTP/1.1 {status}\r\nContent-Type: application/xml\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n{body}";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                }
            }
            passed = true;
        }
        finally
        {
            if (passed) StorageGrowthProbe.RemoveWorkspace(fixture, Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
            else Console.Error.WriteLine($"Synthetic fault fixture preserved at {fixture.Root}.");
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }
}
