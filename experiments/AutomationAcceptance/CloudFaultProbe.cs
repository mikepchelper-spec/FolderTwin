using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;
using System.Reflection;

internal static class CloudFaultProbe
{
    internal static async Task RunAsync(string executable)
    {
        string kopia = Path.Combine(Path.GetDirectoryName(executable)!, "cloud-tools", "kopia.exe");
        var fixture = LabWorkspace.Create();
        bool passed = false;
        try
        {
            await KopiaOutputProbe.RunAsync(fixture.Root);
            using var registry = new ProductJobRegistry(Folder("registry"));
            string source = Folder("source"), targets = Folder("targets");
            string input = Path.Combine(source, "muestra.txt");
            var job = registry.Create(new("Synthetic transport faults", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, targets));
            foreach (var (status, code, issue, retry) in new (string? Status, string Code, CloudTransferIssue Issue, bool Retry)[]
            {
                (null, "connection reset", CloudTransferIssue.Repository, true),
                ("429 Too Many Requests", "SlowDown", CloudTransferIssue.Repository, true),
                ("507 Insufficient Storage", "QuotaExceeded", CloudTransferIssue.Quota, false),
                ("403 Forbidden", "InvalidAccessKeyId", CloudTransferIssue.Credentials, false)
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
                        item.State == (retry ? CloudTransferState.Retry : CloudTransferState.Review) &&
                        item.Issue == issue && item.RetryUtc.HasValue == retry) ||
                        pending.Any(item => item.State == CloudTransferState.Available))
                        throw new InvalidDataException($"Cloud transport failure did not leave a retry for {code}.");
                    if (!registry.RunHistory(job.Id).Runs.Any(run => run.Result is
                        { State: ProductRunState.Confirmed, Generation: var confirmed } && confirmed == generation))
                        throw new InvalidDataException("The cloud failure changed the local confirmation.");
                    foreach (var transfer in pending)
                        registry.ControlCloudTransfer(transfer.Ticket, CloudTransferAction.Cancel);
                    Console.WriteLine($"PASS {code}: requests={Volatile.Read(ref requests)}, issue={issue}. Local confirmation preserved.");
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
                            string body = $"<Error><Code>{code}</Code><Message>{code}: synthetic transport failure</Message></Error>";
                            string response = $"HTTP/1.1 {status}\r\nContent-Type: application/xml\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n{body}";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                }
            }
            await CheckRepositoryIntegrityAsync(kopia, fixture);
            await CheckLocalAdmissionAsync(kopia, fixture);
            await CheckWorkerTerminationAsync(kopia, fixture);
            passed = true;
        }
        finally
        {
            if (passed) StorageGrowthProbe.RemoveWorkspace(fixture, Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
            else Console.Error.WriteLine($"Synthetic fault fixture preserved at {fixture.Root}.");
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    internal static async Task HeldKopiaAsync(string kopia, string fixture, string endpoint)
    {
        if (!IPEndPoint.TryParse(endpoint, out var address) || !address.Address.Equals(IPAddress.Loopback))
            throw new ArgumentException("The synthetic endpoint must use loopback.");
        var workspace = LabWorkspace.Open(fixture);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var reader = new KopiaRepository(kopia, Path.Combine(workspace.Root, "held-kopia.config"),
            "synthetic-held-password", new("synthetic-key", "synthetic-secret"));
        await reader.RunAsync(timeout.Token, "repository", "connect", "s3", "--bucket", "foldertwin", "--endpoint", endpoint,
            "--region", "us-east-1", "--access-key=", "--secret-access-key=", "--disable-tls", "--readonly");
    }

    private static async Task CheckWorkerTerminationAsync(string kopia, LabWorkspace fixture)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string value in new[] { "--held-kopia-child", kopia, fixture.Root, listener.LocalEndpoint.ToString()! })
            start.ArgumentList.Add(value);
        using var worker = Process.Start(start) ?? throw new IOException("Could not start the held worker.");
        try
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            string? request = await reader.ReadLineAsync(timeout.Token);
            if (request is null) throw new InvalidDataException("Kopia exited before making the held request.");
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
            worker.Kill();
            await worker.WaitForExitAsync(timeout.Token);
            string? after = null;
            try { after = await reader.ReadLineAsync(timeout.Token); }
            catch (IOException error) when (error.InnerException is SocketException) { }
            if (after is not null) throw new InvalidDataException("Kopia remained connected after its worker died.");
            Console.WriteLine("PASS killing the worker closed the in-flight Kopia request.");
        }
        finally
        {
            if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
        }
    }

    private static async Task CheckRepositoryIntegrityAsync(string kopia, LabWorkspace fixture)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        string root = Folder("repository"), first = Folder("original"), second = Folder("replacement");
        string password = Guid.NewGuid().ToString("N");
        var writer = new KopiaRepository(kopia, Path.Combine(fixture.Root, "integrity-writer.config"), password);
        await writer.RunAsync(token, "repository", "create", "filesystem", "--path", root, "--no-enable-actions");
        Guid job = Guid.NewGuid();
        string firstGeneration = VersionStore.NewGenerationId();
        string secondGeneration = VersionStore.NewGenerationId();
        byte[] expected = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096);
        File.WriteAllBytes(Path.Combine(first, "data.bin"), expected);
        File.WriteAllBytes(Path.Combine(second, "data.bin"), System.Security.Cryptography.RandomNumberGenerator.GetBytes(expected.Length));
        Guid original = Guid.NewGuid(), ambiguous = Guid.NewGuid();
        var preserved = await writer.PublishAsync(original, new(job, firstGeneration, expected.Length, 1, 0), first, token);
        await writer.PublishAsync(ambiguous, new(job, secondGeneration, expected.Length, 1, 0), second, token);
        Guid expendable = Guid.NewGuid();
        var removable = await writer.PublishAsync(expendable, new(job, VersionStore.NewGenerationId(), expected.Length, 1, 0), second, token);
        await writer.RunAsync(token, "snapshot", "delete", removable.ManifestId);
        if (await writer.FindAsync(expendable, token) is null)
            throw new InvalidDataException("The manual deletion preview removed a snapshot.");
        await writer.RunAsync(token, "snapshot", "delete", removable.ManifestId, "--delete");
        if (await writer.FindAsync(expendable, token) is not null || await writer.FindAsync(original, token) is null)
            throw new InvalidDataException("Manual retention deleted an unintended point.");
        await writer.RunAsync(token, "snapshot", "create", "--pin", "foldertwin", "--force-disable-actions",
            "--tags", $"foldertwin:{ambiguous:N}", "--tags", $"ft-job:{job:N}", "--tags", $"ft-generation:{secondGeneration}", first);
        await RejectAsync(async () => { _ = await writer.FindAsync(ambiguous, token); }, "Ambiguous operation was accepted.");
        await RejectAsync(async () => { _ = await writer.ListAsync(token); }, "Ambiguous recovery date was accepted.");

        var packed = Directory.EnumerateFiles(Path.Combine(root, "p"), "*.f", SearchOption.AllDirectories).ToArray();
        if (packed.Length == 0) throw new InvalidDataException("Kopia did not create any data packs to corrupt.");
        var pristine = packed.ToDictionary(path => path, File.ReadAllBytes);
        foreach (var path in packed)
        {
            byte[] damaged = new byte[pristine[path].Length];
            File.WriteAllBytes(path, damaged);
        }
        var reader = await OpenReaderAsync();
        await RejectAsync(() => reader.RestoreAsync(preserved, Folder("damaged-recovery"), token), "Damaged data was reported as restored.");
        foreach (var path in packed) File.WriteAllBytes(path, pristine[path]);
        reader = await OpenReaderAsync();
        string recovered = Folder("preserved-recovery");
        await reader.RestoreAsync(preserved, recovered, token);
        if (!File.ReadAllBytes(Path.Combine(recovered, "data.bin")).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("The preserved point did not restore its exact bytes.");
        Console.WriteLine("PASS manual deletion preserved the baseline; contradictions and damaged packs were rejected; original bytes restored.");

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
        async Task<KopiaRepository> OpenReaderAsync()
        {
            string location = Folder("integrity-reader-" + Guid.NewGuid().ToString("N"));
            var result = new KopiaRepository(kopia, Path.Combine(location, "repository.config"), password);
            await result.RunAsync(token, "repository", "connect", "filesystem", "--path", root, "--readonly",
                "--cache-directory", Path.Combine(location, "cache"));
            return result;
        }
        static async Task RejectAsync(Func<Task> action, string message)
        {
            try { await action(); }
            catch (Exception error) when (error is IOException or InvalidDataException) { return; }
            throw new InvalidDataException(message);
        }
    }

    private static async Task CheckLocalAdmissionAsync(string kopia, LabWorkspace fixture)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        string root = Folder("mirror-guard-repository");
        var writer = new KopiaRepository(kopia, Path.Combine(fixture.Root, "mirror-guard.config"), Guid.NewGuid().ToString("N"));
        await writer.RunAsync(token, "repository", "create", "filesystem", "--path", root, "--no-enable-actions");
        string repository = await writer.IdentityAsync(token);
        using var registry = new ProductJobRegistry(Folder("mirror-guard-jobs"));
        string source = Folder("mirror-guard-source"), targets = Folder("mirror-guard-targets");
        string input = Path.Combine(source, "edited.txt");
        const string first = "Synthetic confirmed baseline", second = "Synthetic recovered mirror";
        File.WriteAllText(input, first);
        var job = registry.Create(new("Cloud mirror guard", ProductBackupMode.ProtectedMirror, null),
            LocalJobRoots.ReviewDedicatedDestination(source, targets));
        var backup = ProductRunner.Run(registry, job.Id);
        if (backup is not { State: ProductRunState.Confirmed, Generation: { } generation })
            throw new InvalidDataException("Failed to prepare the protected mirror baseline.");
        var baseline = registry.EnqueueCloud(job.Id, repository, generation);
        await registry.ProcessCloudTransferAsync(baseline, writer, token);

        var store = VersionStore.OpenProduct(job);
        string current = Path.Combine(store.MirrorCurrentPath, "edited.txt");
        FileMetadata originalMetadata;
        using (var original = File.OpenRead(current)) originalMetadata = FileMetadata.Read(original.SafeFileHandle);
        File.WriteAllText(current, "Conflicting synthetic change");
        File.WriteAllText(input, second);
        if (ProductRunner.Run(registry, job.Id).State != ProductRunState.Review)
            throw new InvalidDataException("The local mirror overwrote an external conflict.");
        RequireOnlyBaseline();
        File.WriteAllText(current, first);
        using (var original = new FileStream(current, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            originalMetadata.Apply(original.SafeFileHandle);
        using (var original = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            if (ProductRunner.Run(registry, job.Id).State == ProductRunState.Confirmed)
                throw new InvalidDataException("The local mirror confirmed an unreadable inventory.");
        RequireOnlyBaseline();

        var review = store.PreviewMirror(job.Source.VolumePath);
        bool interrupted = false;
        using (var pending = registry.StartRun(job.Id))
        {
            pending.SetGeneration(review.Generation);
            try
            {
                store.BeginMirror(review, massReviewed: true, checkpoint: step =>
                {
                    if (step == MirrorStep.Retired) throw new IOException("Synthetic mirror interruption after retirement.");
                });
            }
            catch (IOException error) when (error.Message == "Synthetic mirror interruption after retirement.") { interrupted = true; }
        }
        if (!interrupted || store.RegisteredGenerations().PendingMirrorGeneration != review.Generation)
            throw new InvalidDataException("The mirror interruption did not exercise its pending state.");
        RequireOnlyBaseline();
        var resumed = ProductRunner.Run(registry, job.Id);
        if (resumed.State != ProductRunState.Confirmed || resumed.Generation != review.Generation)
            throw new InvalidDataException(resumed.Detail ?? "The mirror did not resume the interrupted point.");
        var next = registry.DiscoverCloudTransfers(job.Id, repository, new()).Single();
        string unavailable = Path.Combine(fixture.Root, "mirror-guard-temporarily-away");
        Directory.Move(targets, unavailable);
        try
        {
            if (await registry.ProcessCloudTransferAsync(next, writer, token) is not
                { State: CloudTransferState.Retry, Issue: CloudTransferIssue.DestinationMissing } ||
                await writer.FindAsync(baseline.Operation, token) is null)
                throw new InvalidDataException("The queue lost its last point or misreported a missing local destination.");
        }
        finally { Directory.Move(unavailable, targets); }
        registry.ControlCloudTransfer(next, CloudTransferAction.Retry);
        if (next.Generation != review.Generation || (await registry.ProcessCloudTransferAsync(next, writer, token)).State != CloudTransferState.Available)
            throw new InvalidDataException("The repaired mirror point did not become available.");
        Directory.Move(source, Path.Combine(fixture.Root, "mirror-guard-source-aside"));
        Directory.Move(targets, Path.Combine(fixture.Root, "mirror-guard-targets-aside"));
        foreach (var (ticket, expected) in new[] { (baseline, first), (next, second) })
        {
            var remote = await writer.FindAsync(ticket.Operation, token) ?? throw new InvalidDataException("Cloud point disappeared.");
            string output = Folder($"mirror-guard-restore-{ticket.Operation:N}");
            await writer.RestoreAsync(remote, output, token);
            if (File.ReadAllText(Path.Combine(output, "edited.txt")) != expected)
                throw new InvalidDataException("A guarded mirror point did not preserve its exact content.");
        }
        Console.WriteLine("PASS conflicts, unreadable inventory, interrupted mirror and missing local destination preserved the previous point.");

        void RequireOnlyBaseline()
        {
            if (registry.DiscoverCloudTransfers(job.Id, repository, new()).Length != 0 ||
                registry.CloudTransfers(job.Id, repository).Single().State != CloudTransferState.Available)
                throw new InvalidDataException("An unsafe mirror state was queued or replaced the previous cloud point.");
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }
}
