using Microsoft.Win32.SafeHandles;

internal sealed record ProductRestoreRequest(int Version, Guid Job, Guid Id, byte[] JobStamp, string Generation,
    DedicatedDestinationReview Target, string? RelativeFile);

internal sealed partial class ProductJobRegistry
{
    internal Guid ApproveRestore(Guid id, string generation, DedicatedDestinationReview target, string? relativeFile = null)
    {
        using var operation = OpenLock();
        using var job = OpenJob(id);
        var definition = ReadConfirmed(job, ReadIntent(job, id));
        var request = new ProductRestoreRequest(1, id, Guid.NewGuid(), JobStamp(job), generation, target, relativeFile);
        ValidateRestoreRequest(request, definition);
        CheckRestoreLocationLocked(target);
        using var root = new VersionScope(definition).OpenRegisteredRoot();
        using var requests = WindowsProbeNative.OpenOrCreatePlainDirectory(job, "restores");
        byte[] encoded = Encode(request);
        if (encoded.Length > MaxRecordBytes) throw new InvalidDataException("La revisión de recuperación excede el tamaño permitido.");
        RetentionJournal.WriteFile(requests, request.Id.ToString("N"), encoded);
        return request.Id;
    }

    internal ProductRestoreRequest ReadRestoreRequest(Guid id, Guid requestId)
    {
        using var job = OpenJob(id);
        return ReadRestoreRequest(job, id, requestId);
    }

    private ProductRestoreRequest ReadRestoreRequest(SafeFileHandle job, Guid id, Guid requestId)
    {
        var definition = ReadConfirmed(job, ReadIntent(job, id));
        using var requests = WindowsProbeNative.OpenPlainDirectory(job, "restores");
        var request = Decode<ProductRestoreRequest>(requests, requestId.ToString("N"));
        if (request.Id != requestId || !request.JobStamp.AsSpan().SequenceEqual(JobStamp(job)))
            throw new InvalidDataException("La revisión de recuperación está dañada o pertenece a otro trabajo.");
        ValidateRestoreRequest(request, definition);
        return request;
    }

    private static void ValidateRestoreRequest(ProductRestoreRequest request, ProductJob definition)
    {
        _ = VersionStore.GenerationTime(request.Generation);
        var target = request.Target;
        if (request.Version != 1 || request.Id == Guid.Empty || request.Job != definition.Id ||
            target.FolderId == Guid.Empty || !ValidRoot(target.Source) || !ValidRoot(target.Parent) ||
            !target.Source.VolumePath.Equals(definition.Destination.LastVolumePath, StringComparison.OrdinalIgnoreCase) ||
            !target.Source.Identity.AsSpan().SequenceEqual(definition.Destination.Identity) || !ValidRelative(request.RelativeFile))
            throw new InvalidDataException("La recuperación no corresponde al respaldo o a una carpeta nueva válida.");
    }

    internal void CheckRestoreLocation(DedicatedDestinationReview target)
    {
        using var operation = OpenLock();
        CheckRestoreLocationLocked(target);
    }

    private void CheckRestoreLocationLocked(DedicatedDestinationReview target)
    {
        string output = Path.Combine(target.Parent.VolumePath, target.FolderName);
        if (LocalJobRoots.Overlap(output, volumePath) || LocalJobRoots.Overlap(output, target.Source.VolumePath))
            throw new InvalidDataException("Recupera fuera de la configuración y de los respaldos.");
        foreach (var entry in WindowsProbeNative.ReadDirectory(directory.Root))
        {
            if (!Guid.TryParseExact(entry.Name, "N", out Guid other)) continue;
            using var job = OpenJob(other);
            var intent = ReadIntent(job, other, allowDraft: !WindowsProbeNative.PlainFileExists(job, CommitName));
            if (LocalJobRoots.Overlap(output, intent.Source.VolumePath) || LocalJobRoots.Overlap(output, TargetPath(intent)))
                throw new InvalidDataException("La recuperación debe quedar fuera del origen y destino de todos los trabajos.");
        }
    }

    private void RequireDisjointFromActiveRestores(SafeFileHandle job, Guid id, string source, string destination)
    {
        if (!DirectoryPresent(job, "runs")) return;
        using var runs = WindowsProbeNative.OpenPlainDirectory(job, "runs");
        foreach (var entry in WindowsProbeNative.ReadDirectory(runs).Where(entry => entry.Name is not ("." or "..")))
        {
            using var run = WindowsProbeNative.OpenPlainDirectory(runs, entry.Name);
            if (!IsLocked(run, "active.lock")) continue;
            var intent = Decode<ProductRunIntent>(run, "intent");
            if (!Enum.IsDefined(intent.Operation) || intent.Job != id || !ValidRestoreReference(intent.Operation, intent.RestoreRequest))
                throw new InvalidDataException("Un trabajo activo requiere revisión antes de registrar más carpetas.");
            if (intent.RestoreRequest is not { } requestId) continue;
            var request = ReadRestoreRequest(job, id, requestId);
            string output = Path.Combine(request.Target.Parent.VolumePath, request.Target.FolderName);
            if (LocalJobRoots.Overlap(source, output) || LocalJobRoots.Overlap(destination, output))
                throw new InvalidDataException("Una recuperación activa está utilizando esa carpeta.");
        }
    }
}

internal static partial class ProductRunner
{
    internal static ProductRunResult Restore(ProductJobRegistry registry, Guid id, Guid requestId,
        Action<WorkProgress>? checkpoint = null)
    {
        using var run = registry.StartRun(id, operation: ProductRunOperation.Restore, restoreRequest: requestId);
        var control = new Controller(run, checkpoint);
        try
        {
            var request = registry.ReadRestoreRequest(id, requestId);
            run.SetGeneration(request.Generation);
            control.Observe(new(WorkPhase.Restoring, 0, 0));
            registry.CheckRestoreLocation(request.Target);
            var store = VersionStore.OpenProduct(run.Job, observe: control.Observe);
            if (store.RegisteredGenerations().Expired.Contains(request.Generation))
                throw new InvalidDataException("El punto expiró. Deshaz la cuarentena antes de recuperarlo.");
            using var roots = LocalJobRoots.CreateDedicatedDestination(request.Target);
            var restored = store.Restore(request.Generation, roots.Review.Destination.VolumePath, request.RelativeFile);
            return run.Finish(ProductRunState.Confirmed, degraded: restored.Degraded);
        }
        catch (OperationCanceledException) { return run.Finish(ProductRunState.Cancelled, control.Current.RelativePath); }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            return run.Finish(VersionJob.SharingViolation(error) ? ProductRunState.Busy : ProductRunState.Review,
                control.Current.RelativePath, error.HResult, detail: error.Message);
        }
    }

    internal static int RestoreCommand(string location, Guid id, Guid requestId)
    {
        try
        {
            using var registry = new ProductJobRegistry(location);
            return Restore(registry, id, requestId).State == ProductRunState.Confirmed ? 0 : 1;
        }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            Console.Error.WriteLine("La recuperación no pudo completar su registro. " + error.Message);
            return VersionJob.SharingViolation(error) ? 2 : 1;
        }
    }
}
