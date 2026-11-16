using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

internal enum CloudRecoveryState { Queued, Running, Interrupted, Completed, Cancelled, Review }

internal sealed record CloudRecoveryRequest(int Version, Guid Id, byte[] RegistryIdentity, DateTimeOffset RequestedUtc,
    CloudConnection Connection, CloudRecoveryPoint Remote, LocalJobRoot Parent, CloudRecoverySelection? Selection)
{
    internal string FolderName => $"FolderTwin-Nube-{Id:N}";
    internal string Output => Path.Combine(Parent.VolumePath, FolderName);
}

internal sealed record CloudRecoveryResult(byte[] RequestHash, CloudRecoveryState State, DateTimeOffset FinishedUtc,
    string? Detail);

internal sealed record CloudRecoveryView(CloudRecoveryRequest Request, CloudRecoveryState State,
    WorkProgress? Progress, bool CancellationRequested, CloudRecoveryResult? Result);

internal sealed partial class ProductJobRegistry
{
    private const string CloudRecoveryDirectory = ".cloud-recovery-runs";

    internal CloudRecoveryRequest ApproveCloudRecovery(CloudConnection connection, CloudRecoveryPoint point,
        CloudCredentials credentials, string parentPath, CloudRecoverySelection? selection = null, bool lab = false)
    {
        credentials.Validate(connection.Destination.Provider);
        var parent = LocalJobRoots.InspectSelected(parentPath, writable: true, allowVolumeRoot: true);
        var request = new CloudRecoveryRequest(1, Guid.NewGuid(), identity, DateTimeOffset.UtcNow, connection, point, parent, selection);
        ValidateCloudRecovery(request, request.Id, lab);
        using var operation = OpenLock();
        RequireOutsideJobData(request.Output, recovering: true);
        using var root = WindowsProbeNative.OpenOrCreatePlainDirectory(directory.Root, CloudRecoveryDirectory);
        using var run = WindowsProbeNative.CreatePlainDirectory(root, request.Id.ToString("N"));
        byte[] encoded = Encode(request);
        byte[] secret = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            byte[] sealedSecret = CloudSecret.Transform(secret, SHA256.HashData(encoded), encrypt: true);
            RetentionJournal.WriteFile(run, "credential", sealedSecret);
            RetentionJournal.WriteFile(run, "intent", encoded);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
        return request;
    }

    internal (CloudRecoveryView[] Runs, int Unreadable) ReadCloudRecoveries(bool lab = false)
    {
        if (!DirectoryPresent(directory.Root, CloudRecoveryDirectory)) return ([], 0);
        using var root = WindowsProbeNative.OpenPlainDirectory(directory.Root, CloudRecoveryDirectory);
        var views = new List<CloudRecoveryView>();
        int unreadable = 0;
        foreach (var entry in WindowsProbeNative.ReadDirectory(root))
        {
            if (!Guid.TryParseExact(entry.Name, "N", out var id)) continue;
            try
            {
                using var run = WindowsProbeNative.OpenPlainDirectory(root, entry.Name);
                var request = ReadCloudRecovery(run, id, lab);
                byte[] hash = SHA256.HashData(Encode(request));
                bool started = ReadRecoveryFlag(run, "started", hash);
                bool cancelled = ReadRecoveryFlag(run, "cancel", hash);
                CloudRecoveryResult? result = null;
                if (WindowsProbeNative.PlainFileExists(run, "result"))
                {
                    result = Decode<CloudRecoveryResult>(run, "result");
                    if (!result.RequestHash.AsSpan().SequenceEqual(hash) || result.FinishedUtc.Offset != TimeSpan.Zero ||
                        result.State is not (CloudRecoveryState.Completed or CloudRecoveryState.Cancelled or CloudRecoveryState.Review) ||
                        result.Detail is { Length: > 1000 } || !started && !ReadRecoveryFlag(run, "started", hash))
                        throw new InvalidDataException("El resultado de recuperación remota no corresponde a su solicitud.");
                }
                CloudRecoveryState state = result?.State ?? (IsLocked(run, "active.lock") ? CloudRecoveryState.Running :
                    started ? CloudRecoveryState.Interrupted : CloudRecoveryState.Queued);
                views.Add(new(request, state, ReadRecoveryProgress(run, hash), cancelled, result));
            }
            catch (Exception error) when (VersionJob.Expected(error)) { unreadable++; }
        }
        return (views.OrderByDescending(view => view.Request.RequestedUtc).ThenByDescending(view => view.Request.Id).ToArray(), unreadable);
    }

    internal void CancelCloudRecovery(Guid id, bool lab = false)
    {
        using var operation = OpenLock();
        using var run = OpenCloudRecovery(id);
        var request = ReadCloudRecovery(run, id, lab);
        if (WindowsProbeNative.PlainFileExists(run, "result")) return;
        byte[] hash = SHA256.HashData(Encode(request));
        WriteCloudRecord(run, "cancel", hash);
        if (!IsLocked(run, "active.lock"))
        {
            if (!ReadRecoveryFlag(run, "started", hash)) RetentionJournal.WriteFile(run, "started", hash);
            PublishRecoveryResult(run, hash, CloudRecoveryState.Cancelled);
        }
    }

    internal async Task<CloudRecoveryResult> RunCloudRecoveryAsync(Guid id,
        Func<CloudConnection, CloudCredentials, CloudRepositorySession> connect, CancellationToken token, bool lab = false)
    {
        using var run = OpenCloudRecovery(id);
        var request = ReadCloudRecovery(run, id, lab);
        byte[] hash = SHA256.HashData(Encode(request));
        using var active = StartCloudRecovery(run, hash);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        var progressTime = Stopwatch.StartNew();
        WorkPhase? lastPhase = null;
        long revision = 0;
        CloudRecoveryState state;
        string? detail = null;
        var control = ObserveRecoveryCancellationAsync(run, hash, cancelled);
        try
        {
            cancelled.Token.ThrowIfCancellationRequested();
            using (OpenLock()) RequireOutsideJobData(request.Output, recovering: true);
            using var parent = new DirectoryFence(request.Parent.VolumePath);
            WindowsProbeNative.RequireNtfsVolume(parent.Root, writable: true);
            if (!WindowsProbeNative.DirectoryIdentity(parent.Root).AsSpan().SequenceEqual(request.Parent.Identity))
                throw new InvalidDataException("La carpeta elegida cambió. Inicia otro intento y elige de nuevo la salida.");
            using var output = WindowsProbeNative.CreatePlainDirectory(parent.Root, request.FolderName);
            Observe(new(WorkPhase.Preparing, 0, 0));
            using var session = connect(request.Connection, ReadRecoveryCredentials(run, hash, request.Connection.Destination.Provider));
            if (await session.OpenAsync(create: false, readOnly: true, cancelled.Token) != request.Connection)
                throw new InvalidDataException("El repositorio cambió. Vuelve a consultar sus fechas.");
            var remote = (await session.Repository.ListAsync(cancelled.Token)).SingleOrDefault(point => point == request.Remote) ??
                throw new InvalidDataException("La fecha cambió o ya no está disponible.");
            await session.Repository.RestoreAsync(remote.Point, WindowsProbeNative.VolumePath(output), cancelled.Token, Observe, request.Selection);
            state = CloudRecoveryState.Completed;
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { state = CloudRecoveryState.Cancelled; }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            state = CloudRecoveryState.Review;
            detail = LabWorkspace.IsDiskFull(error.HResult) ? "Falta espacio para la recuperación. Libera espacio o elige otro disco." :
                error is InvalidDataException or CloudProviderException ? error.Message :
                "La recuperación se interrumpió. Comprueba el acceso al repositorio y la carpeta de salida.";
        }
        finally
        {
            cancelled.Cancel();
        }
        if (!await control)
        {
            state = CloudRecoveryState.Review;
            detail = "La orden de cancelación está dañada. Revisa el intento y su salida.";
        }
        var result = PublishRecoveryResult(run, hash, state, detail);
        if (state == CloudRecoveryState.Completed)
        {
            try { RecordCloudRestore(request.Connection.Repository, request.Remote.Point); }
            catch (Exception error) when (VersionJob.Expected(error)) { /* The durable result already certifies the recovery. */ }
        }
        return result;

        void Observe(WorkProgress work)
        {
            if (work.Phase == lastPhase && work.Processed != work.Total && progressTime.ElapsedMilliseconds < 250) return;
            lastPhase = work.Phase;
            progressTime.Restart();
            long next = ++revision;
            try
            {
                using var progress = WindowsProbeNative.OpenPlainFile(run, next % 2 == 0 ? "progress-a" : "progress-b",
                    FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
                byte[] bytes = Encode(new ProductRunProgress(1, hash, next, work with { RelativePath = null }, false));
                progress.Write(bytes);
                progress.SetLength(bytes.Length);
                progress.Flush();
            }
            catch (Exception error) when (VersionJob.Expected(error)) { /* Progress cannot determine the recovered result. */ }
        }
    }

    private FileStream StartCloudRecovery(SafeFileHandle run, byte[] hash)
    {
        using var operation = OpenLock();
        var active = WindowsProbeNative.OpenPlainFile(run, "active.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
        try
        {
            if (ReadRecoveryFlag(run, "started", hash) || WindowsProbeNative.PlainFileExists(run, "result"))
                throw new InvalidDataException("Este intento ya se ejecutó. Inicia otro para usar una carpeta nueva.");
            RetentionJournal.WriteFile(run, "started", hash);
            return active;
        }
        catch { active.Dispose(); throw; }
    }

    private void RequireDisjointFromCloudRecovery(string source, string destination)
    {
        if (!DirectoryPresent(directory.Root, CloudRecoveryDirectory)) return;
        using var root = WindowsProbeNative.OpenPlainDirectory(directory.Root, CloudRecoveryDirectory);
        foreach (var entry in WindowsProbeNative.ReadDirectory(root))
        {
            if (!Guid.TryParseExact(entry.Name, "N", out var id)) continue;
            using var run = WindowsProbeNative.OpenPlainDirectory(root, entry.Name);
            if (!IsLocked(run, "active.lock")) continue;
            var request = ReadCloudRecovery(run, id, lab: true);
            if (LocalJobRoots.Overlap(source, request.Output) || LocalJobRoots.Overlap(destination, request.Output))
                throw new InvalidDataException("Una recuperación de nube activa está utilizando esa carpeta.");
        }
    }

    private SafeFileHandle OpenCloudRecovery(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("Falta la identidad de la recuperación de nube.");
        using var root = WindowsProbeNative.OpenPlainDirectory(directory.Root, CloudRecoveryDirectory);
        return WindowsProbeNative.OpenPlainDirectory(root, id.ToString("N"));
    }

    private CloudRecoveryRequest ReadCloudRecovery(SafeFileHandle run, Guid id, bool lab)
    {
        var request = Decode<CloudRecoveryRequest>(run, "intent");
        ValidateCloudRecovery(request, id, lab);
        return request;
    }

    private void ValidateCloudRecovery(CloudRecoveryRequest request, Guid id, bool lab)
    {
        if (id == Guid.Empty || request.Id != id || request.Version != 1 || !request.RegistryIdentity.AsSpan().SequenceEqual(identity) ||
            request.RequestedUtc.Offset != TimeSpan.Zero || !ValidRoot(request.Parent) || !RetentionJournal.IsDigest(request.Connection.Repository) ||
            request.Remote.Job == Guid.Empty || VersionStore.IsPartialPoint(request.Remote.Generation) ||
            request.Remote.Point.Layout is not (0 or 2) ||
            request.Remote.Point.Layout == 2 &&
                (request.Remote.Point.Generation != request.Remote.Generation || request.Remote.Point.Job != request.Remote.Job) ||
            request.Selection is { } selection && (request.Remote.Point.Layout == 0 || !ValidRelative(selection.RelativePath) || FolderBatch.IsControl(selection.RelativePath)))
            throw new InvalidDataException("La revisión de recuperación de nube está dañada o pertenece a otro respaldo.");
        _ = VersionStore.GenerationTime(request.Remote.Generation);
        request.Connection.Destination.Validate(lab);
    }

    private static bool ReadRecoveryFlag(SafeFileHandle run, string name, byte[] hash)
    {
        if (!WindowsProbeNative.PlainFileExists(run, name)) return false;
        using var marker = WindowsProbeNative.OpenPlainFile(run, name, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Span<byte> bytes = stackalloc byte[32];
        if (marker.Length != bytes.Length || marker.ReadAtLeast(bytes, bytes.Length, false) != bytes.Length || !bytes.SequenceEqual(hash))
            throw new InvalidDataException("Un estado de recuperación remota no coincide con su solicitud.");
        return true;
    }

    private static async Task<bool> ObserveRecoveryCancellationAsync(SafeFileHandle run, byte[] hash, CancellationTokenSource cancelled)
    {
        try
        {
            while (!cancelled.IsCancellationRequested)
            {
                if (ReadRecoveryFlag(run, "cancel", hash)) { cancelled.Cancel(); return true; }
                await Task.Delay(250, cancelled.Token);
            }
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        catch (Exception error) when (VersionJob.Expected(error)) { cancelled.Cancel(); return false; }
        return true;
    }

    private static WorkProgress? ReadRecoveryProgress(SafeFileHandle run, byte[] hash)
    {
        ProductRunProgress? latest = null;
        foreach (string name in new[] { "progress-a", "progress-b" })
        {
            try
            {
                var candidate = Decode<ProductRunProgress>(run, name);
                if (candidate.Version == 1 && candidate.IntentHash.AsSpan().SequenceEqual(hash) &&
                    candidate.Revision > (latest?.Revision ?? 0) && ValidWork(candidate.Work))
                    latest = candidate;
            }
            catch (Exception error) when (VersionJob.Expected(error)) { }
        }
        return latest?.Work;
    }

    private static CloudCredentials ReadRecoveryCredentials(SafeFileHandle run, byte[] hash, CloudProvider provider)
    {
        using var input = WindowsProbeNative.OpenPlainFile(run, "credential", FileAccess.Read, FileShare.Read);
        byte[] secret = CloudSecret.Transform(ReadCredentialBlob(input), hash, encrypt: false);
        try
        {
            var credentials = JsonSerializer.Deserialize<CloudCredentials>(secret) ??
                throw new InvalidDataException("No se pudieron leer las credenciales de recuperación.");
            credentials.Validate(provider);
            return credentials;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private static CloudRecoveryResult PublishRecoveryResult(SafeFileHandle run, byte[] hash, CloudRecoveryState state, string? detail = null)
    {
        var result = new CloudRecoveryResult(hash, state, DateTimeOffset.UtcNow, detail is { Length: > 1000 } ? detail[..1000] : detail);
        RetentionJournal.WriteFile(run, "result", Encode(result));
        try
        {
            using var credential = WindowsProbeNative.OpenPlainFile(run, "credential", FileAccess.Read, FileShare.None, rename: true);
            byte[] clear = CloudSecret.Transform(ReadCredentialBlob(credential), hash, encrypt: false);
            CryptographicOperations.ZeroMemory(clear);
            WindowsProbeNative.DeletePlainFile(credential.SafeFileHandle);
        }
        catch (Exception error) when (VersionJob.Expected(error)) { /* The intent retains no plaintext. */ }
        return result;
    }

    private static byte[] ReadCredentialBlob(FileStream input)
    {
        WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, "credencial protegida");
        if (input.Length is < 1 or > 60_000) throw new InvalidDataException("La credencial protegida tiene un tamaño inválido.");
        byte[] encrypted = new byte[(int)input.Length];
        input.ReadExactly(encrypted);
        return encrypted;
    }
}
