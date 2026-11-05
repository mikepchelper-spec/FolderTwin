using Microsoft.Win32.SafeHandles;

// The lab and product entry points keep separate root authorization. Catalogs,
// objects, journals and their verification continue through the same engine.
internal sealed class VersionScope
{
    private readonly LabWorkspace? lab;
    private readonly ProductJob? job;
    internal string Root => lab?.Root ?? job!.Destination.LastVolumePath;

    internal VersionScope(LabWorkspace lab) => this.lab = lab;
    internal VersionScope(ProductJob job) => this.job = job;

    internal DirectoryFence OpenRegisteredRoot()
    {
        if (lab is not null) return lab.OpenRegisteredRoot();
        var root = new DirectoryFence(Root);
        try
        {
            WindowsProbeNative.RequireNtfsVolume(root.Root, writable: true);
            using var marker = job!.Destination.OpenVerifiedMarker(root.Root);
            return root;
        }
        catch { root.Dispose(); throw; }
    }

    internal void CheckRoots(string source, string destination)
    {
        if (lab is not null) { lab.CheckRoots(source, destination); return; }
        if (Same(destination, Root) && Same(source, job!.Source.VolumePath)) return;
        if (!Same(source, Root))
            throw new InvalidDataException("Las carpetas no corresponden al trabajo guardado.");
        using var roots = new LocalJobRoots(source, destination);
        if (LocalJobRoots.Overlap(roots.Review.Destination.VolumePath, job!.Source.VolumePath))
            throw new InvalidDataException("La recuperación necesita una carpeta separada del origen.");
        job.Destination.RequireDirectoryIdentity(roots.SourceHandle);
    }

    internal void RequireSourceIdentity(string source, SafeFileHandle handle)
    {
        if (job is not null && (!Same(source, job.Source.VolumePath) ||
            !WindowsProbeNative.DirectoryIdentity(handle).AsSpan().SequenceEqual(job.Source.Identity)))
            throw new InvalidDataException("La carpeta de origen fue sustituida desde que se guardó el trabajo.");
    }

    internal string SourcePath(string name)
    {
        if (lab is not null) return Path.Combine(Root, name);
        if (name != Path.GetFileName(job!.Source.VolumePath))
            throw new InvalidDataException("El origen del diario no corresponde al trabajo.");
        return job.Source.VolumePath;
    }

    internal void ValidatePlan(BatchPlan plan)
    {
        if (lab is not null) { PendingBatch.ValidatePlan(plan, BatchCatalog.VersionMaximumEntries); return; }
        CheckRoots(plan.Source, plan.Destination);
        FolderBatch.ValidateEntries(plan.Files, plan.Directories, BatchCatalog.VersionMaximumEntries);
        if (plan.SourceBinding is not null || !plan.SourceIdentity.AsSpan().SequenceEqual(job!.Source.Identity) ||
            plan.Target.Marker != job.Destination.Marker || !plan.Target.Identity.AsSpan().SequenceEqual(job.Destination.Identity))
            throw new InvalidDataException("El plan no corresponde a las identidades del trabajo.");
    }

    internal void SaveIntent(BatchPlan plan, SafeFileHandle scope, PendingFiles files, Action<PendingStep>? checkpoint = null)
    {
        ValidatePlan(plan);
        if (lab is not null) PendingBatch.SaveLocked(plan, scope, files, checkpoint);
        else PendingBatch.SaveProductLocked(plan, scope, files, checkpoint);
    }

    internal BatchPlan ReadIntent(SafeFileHandle scope, string destination, SafeFileHandle store, PendingFiles files) =>
        lab is not null ? PendingBatch.ReadLocked(Root, destination, scope, store, files) :
        PendingBatch.ReadProductLocked(job!, scope, store, files);

    internal (string Source, byte[] Identity) ReadRegisteredSource(string destination, SafeFileHandle scope,
        PendingFiles files, byte[] documentHash) => lab is not null ?
        PendingBatch.ReadRegisteredSource(Root, destination, scope, files, documentHash) :
        PendingBatch.ReadProductRegisteredSource(job!, scope, files, documentHash);

    internal void Copy(string sourceRoot, string destinationRoot, string input, string final, string temporary,
        DestinationBinding target, byte[] hash, Action<CopyStep, string>? checkpoint = null, FileMetadata? metadata = null)
    {
        if (lab is not null)
        {
            VerifiedCopy.Run(input, final, temporary, checkpoint, expectedHash: hash, recoveryRoot: Root, metadata: metadata);
            return;
        }
        using var roots = new LocalJobRoots(sourceRoot, destinationRoot);
        if (Same(sourceRoot, job!.Source.VolumePath)) RequireSourceIdentity(sourceRoot, roots.SourceHandle);
        else job.Destination.RequireDirectoryIdentity(roots.SourceHandle);
        VerifiedCopy.RunBound(roots, target, Path.GetRelativePath(sourceRoot, input),
            Path.GetRelativePath(destinationRoot, final), Path.GetRelativePath(destinationRoot, temporary),
            hash, checkpoint, metadata: metadata);
    }

    private static bool Same(string first, string second) => first.Equals(second, StringComparison.OrdinalIgnoreCase);
}
