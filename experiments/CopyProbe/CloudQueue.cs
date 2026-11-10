using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

internal enum CloudTransferState { Pending, Paused, Cancelled, Retry, Review, Available }
internal enum CloudTransferIssue { None, LocalPoint, Repository, Credentials, Space, Quota, DestinationMissing }
internal enum CloudTransferAction { Pause, Cancel, Retry }
internal sealed record CloudTicket(Guid Job, string Repository, string Generation, Guid Operation)
{
    internal static CloudTicket For(Guid job, string repository, string generation)
    {
        if (job == Guid.Empty || !RetentionJournal.IsDigest(repository) || VersionStore.IsPartialPoint(generation))
            throw new InvalidDataException("La cola requiere un trabajo, repositorio y punto completo.");
        _ = VersionStore.GenerationTime(generation);
        byte[] digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{job:N}/{repository}/{generation}"));
        return new(job, repository, generation, new Guid(digest.AsSpan(0, 16)));
    }
}

internal sealed record CloudTransfer(CloudTicket Ticket, CloudTransferState State, int Attempts,
    DateTimeOffset? RetryUtc, CloudTransferIssue Issue, KopiaPoint? Point);

internal sealed partial class ProductJobRegistry
{
    private sealed record CloudRevision(int Version, long Sequence, byte[] Previous, CloudTransfer Transfer);

    internal CloudTicket EnqueueCloud(Guid jobId, string repository, string generation)
    {
        var ticket = CloudTicket.For(jobId, repository, generation);
        if (!RunHistory(jobId).Runs.Any(run => run.Intent.Operation == ProductRunOperation.Backup &&
            run.Result is { State: ProductRunState.Confirmed, Generation: var point } && point == generation))
            throw new InvalidDataException("La cola solo admite puntos locales confirmados.");
        using var entry = OpenCloudEntry(ticket, create: true);
        using var operation = CloudStateLock(entry);
        WriteCloudRecord(entry, "intent", Encode(ticket));
        return ticket;
    }

    internal CloudTransfer[] CloudTransfers(Guid jobId, string repository)
    {
        if (!RetentionJournal.IsDigest(repository)) throw new ArgumentException("El repositorio no tiene una identidad válida.", nameof(repository));
        using var job = OpenJob(jobId);
        if (!DirectoryPresent(job, "cloud")) return [];
        using var cloud = WindowsProbeNative.OpenPlainDirectory(job, "cloud");
        if (!DirectoryPresent(cloud, repository)) return [];
        using var destination = WindowsProbeNative.OpenPlainDirectory(cloud, repository);
        var transfers = new List<CloudTransfer>();
        foreach (var file in WindowsProbeNative.ReadDirectory(destination))
        {
            if (!Guid.TryParseExact(file.Name, "N", out var operation)) continue;
            using var entry = WindowsProbeNative.OpenPlainDirectory(destination, file.Name);
            if (!WindowsProbeNative.PlainFileExists(entry, "intent")) continue;
            var ticket = Decode<CloudTicket>(entry, "intent");
            if (ticket.Job != jobId || ticket.Repository != repository || ticket.Operation != operation)
                throw new InvalidDataException("La cola contiene un envío de otro trabajo o repositorio.");
            transfers.Add(ReadCloudTransfer(entry, ticket).Transfer);
        }
        return transfers.OrderBy(transfer => transfer.Ticket.Generation, StringComparer.Ordinal).ToArray();
    }

    internal CloudTransfer ReadCloudTransfer(CloudTicket ticket)
    {
        using var entry = OpenCloudEntry(ticket);
        return ReadCloudTransfer(entry, ticket).Transfer;
    }

    internal void ControlCloudTransfer(CloudTicket ticket, CloudTransferAction action)
    {
        using var entry = OpenCloudEntry(ticket);
        using var operation = CloudStateLock(entry);
        var (current, sequence, hash) = ReadCloudTransfer(entry, ticket);
        if (current.State == CloudTransferState.Available) return;
        var next = action switch
        {
            CloudTransferAction.Pause => current with { State = CloudTransferState.Paused, RetryUtc = null, Issue = CloudTransferIssue.None },
            CloudTransferAction.Cancel => current with { State = CloudTransferState.Cancelled, RetryUtc = null, Issue = CloudTransferIssue.None },
            CloudTransferAction.Retry => current with { State = CloudTransferState.Pending, Attempts = 0, RetryUtc = null, Issue = CloudTransferIssue.None },
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        if (current != next) AppendCloudRevision(entry, next, sequence, hash);
    }

    internal void RecordCloudConnectionFailure(CloudTicket ticket, CloudTransferIssue issue, bool retry)
    {
        using var entry = OpenCloudEntry(ticket);
        using var worker = CloudLock(entry, "worker.lock");
        using var state = CloudStateLock(entry);
        var (current, sequence, hash) = ReadCloudTransfer(entry, ticket);
        if (current.State is not (CloudTransferState.Pending or CloudTransferState.Retry)) return;
        int attempts = current.Attempts + 1;
        retry &= attempts < 4;
        AppendCloudRevision(entry, current with { State = retry ? CloudTransferState.Retry : CloudTransferState.Review,
            Attempts = attempts, Issue = issue, RetryUtc = retry ? DateTimeOffset.UtcNow.AddSeconds(30 * (1 << (attempts - 1))) : null },
            sequence, hash);
    }

    internal async Task<CloudTransfer> ProcessCloudTransferAsync(CloudTicket ticket, KopiaRepository repository,
        CancellationToken shutdown, Action? afterUpload = null, TimeProvider? time = null)
    {
        using var entry = OpenCloudEntry(ticket);
        using var worker = CloudLock(entry, "worker.lock");
        var initial = ReadCloudTransfer(entry, ticket).Transfer;
        time ??= TimeProvider.System;
        if (initial.State is not (CloudTransferState.Pending or CloudTransferState.Retry) || initial.RetryUtc > time.GetUtcNow() ||
            IsCloudPaused(ticket.Job))
            return initial;

        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        var control = ObserveCloudControlAsync(entry, ticket, stopped);
        CloudTransferIssue failureStage = CloudTransferIssue.Repository;
        try
        {
            if (await repository.IdentityAsync(deadline.Token) != ticket.Repository)
                throw new InvalidDataException("La conexión pertenece a otro repositorio de Kopia.");
            var remote = await repository.FindAsync(ticket.Operation, deadline.Token);
            CloudPoint? prepared = WindowsProbeNative.PlainFileExists(entry, "prepared") ? Decode<CloudPoint>(entry, "prepared") : null;
            if (prepared is not null && (prepared.Job != ticket.Job || prepared.Generation != ticket.Generation))
                throw new InvalidDataException("El inventario temporal corresponde a otro envío.");
            if (remote is not null)
            {
                if (prepared is null) throw new InvalidDataException("Falta el inventario previo del punto remoto.");
                KopiaRepository.RequireInventory(remote, prepared);
            }
            else
            {
                failureStage = CloudTransferIssue.LocalPoint;
                RemoveCloudStage(entry);
                using var stage = OpenCloudStage(entry);
                string payload = WindowsProbeNative.VolumePath(stage);
                var point = VersionStore.ExportConfirmedPoint(this, ticket.Job, ticket.Generation, payload, deadline.Token);
                WriteCloudRecord(entry, "prepared", Encode(point));
                failureStage = CloudTransferIssue.Repository;
                remote = await repository.PublishAsync(ticket.Operation, point, payload, deadline.Token, afterUpload);
            }
            return Finish(CloudTransferState.Available, CloudTransferIssue.None, remote);
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested)
        {
            return ReadCloudTransfer(entry, ticket).Transfer;
        }
        catch (Exception error) when (VersionJob.Expected(error) || error is OperationCanceledException)
        {
            var issue = error is CloudProviderException provider ? provider.Issue :
                error is CloudLocalDestinationUnavailableException ? CloudTransferIssue.DestinationMissing :
                LabWorkspace.IsDiskFull(error.HResult) ? CloudTransferIssue.Space : failureStage;
            bool retry = error is not (InvalidDataException or UnauthorizedAccessException) &&
                (issue is CloudTransferIssue.Repository or CloudTransferIssue.DestinationMissing || VersionJob.SharingViolation(error));
            return Finish(retry ? CloudTransferState.Retry : CloudTransferState.Review, issue, null);
        }
        finally
        {
            stopped.Cancel();
            await control;
            RemoveCloudStage(entry);
        }

        CloudTransfer Finish(CloudTransferState state, CloudTransferIssue issue, KopiaPoint? point)
        {
            // A control may have arrived after the remote accepted a point.
            // Preserve the confirmed remote result in that case.
            using var operation = CloudStateLock(entry);
            var (current, sequence, hash) = ReadCloudTransfer(entry, ticket);
            if (point is null && current.State is (CloudTransferState.Cancelled or CloudTransferState.Paused))
                return current;
            int attempts = current.Attempts + 1;
            if (state == CloudTransferState.Retry && attempts >= 4) state = CloudTransferState.Review;
            var next = new CloudTransfer(ticket, state, attempts,
                state == CloudTransferState.Retry ? time.GetUtcNow().AddSeconds(30 * (1 << (attempts - 1))) : null, issue, point);
            AppendCloudRevision(entry, next, sequence, hash);
            return next;
        }
    }

    private async Task ObserveCloudControlAsync(SafeFileHandle entry, CloudTicket ticket, CancellationTokenSource stopped)
    {
        try
        {
            while (!stopped.IsCancellationRequested)
            {
                if (ReadCloudTransfer(entry, ticket).Transfer.State is CloudTransferState.Paused or CloudTransferState.Cancelled ||
                    IsCloudPaused(ticket.Job))
                    stopped.Cancel();
                await Task.Delay(250, stopped.Token);
            }
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        catch (Exception error) when (VersionJob.Expected(error)) { stopped.Cancel(); throw; }
    }

    private SafeFileHandle OpenCloudEntry(CloudTicket ticket, bool create = false)
    {
        if (ticket != CloudTicket.For(ticket.Job, ticket.Repository, ticket.Generation))
            throw new InvalidDataException("La identidad del envío no es válida.");
        using var job = OpenJob(ticket.Job);
        using var cloud = Open(job, "cloud");
        using var repository = Open(cloud, ticket.Repository);
        return Open(repository, ticket.Operation.ToString("N"));

        SafeFileHandle Open(SafeFileHandle parent, string name) => create ?
            WindowsProbeNative.OpenOrCreatePlainDirectory(parent, name) : WindowsProbeNative.OpenPlainDirectory(parent, name);
    }

    private static (CloudTransfer Transfer, long Sequence, byte[] Hash) ReadCloudTransfer(SafeFileHandle entry, CloudTicket ticket)
    {
        if (Decode<CloudTicket>(entry, "intent") != ticket || ticket != CloudTicket.For(ticket.Job, ticket.Repository, ticket.Generation))
            throw new InvalidDataException("El envío registrado no coincide con su identidad.");
        var latest = new CloudTransfer(ticket, CloudTransferState.Pending, 0, null, CloudTransferIssue.None, null);
        long sequence = 0;
        byte[] hash = SHA256.HashData(Encode(ticket));
        foreach (var file in WindowsProbeNative.ReadDirectory(entry).Where(file => file.Name.EndsWith(".state", StringComparison.Ordinal))
            .OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            var record = Decode<CloudRevision>(entry, file.Name);
            var value = record.Transfer;
            if (record.Version != 1 || record.Sequence != ++sequence || file.Name != CloudRevisionName(sequence) ||
                !record.Previous.AsSpan().SequenceEqual(hash) || value.Ticket != ticket || value.Attempts is < 0 or > 4 ||
                !Enum.IsDefined(value.State) || !Enum.IsDefined(value.Issue) ||
                (value.RetryUtc is not null) != (value.State == CloudTransferState.Retry) ||
                (value.Point is not null) != (value.State == CloudTransferState.Available))
                throw new InvalidDataException("La cola de nube contiene una transición inválida.");
            latest = value;
            hash = SHA256.HashData(Encode(record));
        }
        return (latest, sequence, hash);
    }

    private static string CloudRevisionName(long sequence) => sequence.ToString("D20", CultureInfo.InvariantCulture) + ".state";

    private static void AppendCloudRevision(SafeFileHandle entry, CloudTransfer transfer, long previous, byte[] hash) =>
        WriteCloudRecord(entry, CloudRevisionName(previous + 1), Encode(new CloudRevision(1, previous + 1, hash, transfer)));

    private static FileStream CloudLock(SafeFileHandle entry, string name) =>
        WindowsProbeNative.OpenPlainFile(entry, name, FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);

    private static FileStream CloudStateLock(SafeFileHandle entry)
    {
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return CloudLock(entry, "state.lock"); }
            catch (Exception error) when (VersionJob.SharingViolation(error) && wait.ElapsedMilliseconds < 1000)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static void WriteCloudRecord(SafeFileHandle entry, string name, byte[] bytes)
    {
        // An incomplete draft has no authority. The caller holds the
        // corresponding queue lock and can regenerate the same transition.
        if (WindowsProbeNative.PlainFileExists(entry, name + ".tmp"))
        {
            using var draft = WindowsProbeNative.OpenPlainFile(entry, name + ".tmp", FileAccess.Read, FileShare.None, rename: true);
            WindowsProbeNative.DeletePlainFile(draft.SafeFileHandle);
        }
        RetentionJournal.WriteFile(entry, name, bytes);
    }

    private static SafeFileHandle OpenCloudStage(SafeFileHandle entry)
    {
        bool recorded = WindowsProbeNative.PlainFileExists(entry, "stage-id");
        var stage = recorded ? WindowsProbeNative.OpenPlainDirectory(entry, "payload") :
            WindowsProbeNative.OpenOrCreatePlainDirectory(entry, "payload");
        try
        {
            byte[] identity = WindowsProbeNative.DirectoryIdentity(stage);
            if (recorded)
            {
                if (!Decode<byte[]>(entry, "stage-id").AsSpan().SequenceEqual(identity))
                    throw new InvalidDataException("La carpeta temporal de nube fue sustituida. Se conserva para revisión.");
            }
            else
            {
                LabWorkspace.RequireEmpty(stage);
                WriteCloudRecord(entry, "stage-id", Encode(identity));
            }
            return stage;
        }
        catch { stage.Dispose(); throw; }
    }

    private static void RemoveCloudStage(SafeFileHandle entry)
    {
        if (!WindowsProbeNative.PlainFileExists(entry, "stage-id")) return;
        using var stage = OpenCloudStage(entry);
        RemoveContents(stage);

        static void RemoveContents(SafeFileHandle directory)
        {
            foreach (var item in WindowsProbeNative.ReadDirectory(directory).Where(item => item.Name is not ("." or "..")).ToArray())
            {
                if (item.Attributes.HasFlag(FileAttributes.Directory))
                {
                    using var child = WindowsProbeNative.OpenDirectoryForDeletion(directory, item.Name);
                    RemoveContents(child);
                    WindowsProbeNative.DeleteEmptyDirectory(child);
                }
                else
                {
                    using var file = WindowsProbeNative.OpenPlainFile(directory, item.Name, FileAccess.Read, FileShare.None, rename: true);
                    WindowsProbeNative.DeletePlainFile(file.SafeFileHandle);
                }
            }
        }
    }
}
