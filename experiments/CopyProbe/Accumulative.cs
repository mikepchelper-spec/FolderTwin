using Microsoft.Win32.SafeHandles;

internal sealed partial class VersionStore
{
    private static string[] AccumulativeConflicts(BatchPlan source, CatalogSnapshot? previous, BatchPlan current)
    {
        var old = previous?.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase) ?? [];
        var actual = current.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        return source.Files.Where(file => old.TryGetValue(file.RelativePath, out var known) ?
                !actual.TryGetValue(file.RelativePath, out var found) || !SameMirrorFile(known, found) :
                actual.TryGetValue(file.RelativePath, out var collision) && !SameMirrorFile(file, collision))
            .Select(file => file.RelativePath).ToArray();
    }

    private BatchPlan MergeAccumulative(BatchPlan source, BatchPlan current)
    {
        // NTFS paths in this mode are case-insensitive. Retain current's spelling,
        // including existing parents of new files, without changing the raw source inventory.
        var currentPaths = current.Directories.Concat(current.Files.Select(file => file.RelativePath))
            .ToDictionary(path => path, path => path, StringComparer.OrdinalIgnoreCase);
        string Canonical(string path)
        {
            if (currentPaths.TryGetValue(path, out string? existing)) return existing;
            string? parent = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(parent) ? path : Path.Combine(Canonical(parent), Path.GetFileName(path));
        }
        var files = current.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var file in source.Files)
        {
            string path = Canonical(file.RelativePath);
            files[path] = file with { RelativePath = path };
        }
        var directories = current.Directories.Concat(source.Directories.Select(Canonical))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (directories.Any(files.ContainsKey))
            throw new InvalidDataException("Una ruta cambia entre archivo y carpeta. La copia acumulativa conserva el destino para revisión.");
        var merged = source with
        {
            Files = files.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray(),
            Directories = directories
        };
        workspace.ValidatePlan(merged);
        return merged;
    }

    private (BatchPlan Source, CatalogSnapshot Baseline) ReadAccumulativeContext(SafeFileHandle scope, SafeFileHandle root,
        SafeFileHandle directory, MirrorPlan plan)
    {
        var sourceFiles = new PendingFiles(directory, "source.jsonl", "source.location.json", plan.Generation);
        BatchPlan source;
        try { source = workspace.ReadIntent(scope, Destination, root, sourceFiles); }
        catch (FileNotFoundException)
        {
            source = ReadMirrorSource(plan);
            workspace.SaveIntent(source, scope, sourceFiles);
        }
        if (!source.SourceIdentity.AsSpan().SequenceEqual(plan.SourceIdentity) ||
            !BatchCatalog.WriteMetadata(Stream.Null, source, plan.Generation).AsSpan().SequenceEqual(plan.SourceHash))
            throw new InvalidDataException("El inventario de origen no corresponde a la copia acumulativa pendiente.");

        const string baselineName = "baseline.jsonl";
        if (!WindowsProbeNative.PlainFileExists(directory, baselineName))
        {
            var observed = AnalyzeSource(MirrorCurrentPath, plan.CurrentIdentity, WorkPhase.Verifying);
            if (!BatchCatalog.WriteMetadata(Stream.Null, observed, plan.Generation).AsSpan().SequenceEqual(plan.BaselineHash))
                throw new InvalidDataException("El destino cambió antes de guardar la revisión acumulativa. Se conserva para revisión.");
            string draft = baselineName + ".tmp";
            if (!WindowsProbeNative.PlainFileExists(directory, draft))
            {
                using var output = WindowsProbeNative.OpenPlainFile(directory, draft, FileAccess.Write, FileShare.None, FileMode.CreateNew);
                _ = BatchCatalog.WriteMetadata(output, observed, plan.Generation);
                output.Flush(flushToDisk: true);
            }
            using var verified = WindowsProbeNative.OpenPlainFile(directory, draft, FileAccess.Read, FileShare.Read, rename: true);
            RequireBaseline(BatchCatalog.Parse(verified, plan.Generation));
            WindowsProbeNative.RenameNew(verified.SafeFileHandle, directory, baselineName);
        }
        using var baselineFile = WindowsProbeNative.OpenPlainFile(directory, baselineName, FileAccess.Read, FileShare.Read);
        var baseline = BatchCatalog.Parse(baselineFile, plan.Generation);
        RequireBaseline(baseline);
        return (source, baseline);

        void RequireBaseline(CatalogSnapshot catalog)
        {
            if (!catalog.DocumentHash.AsSpan().SequenceEqual(plan.BaselineHash) || catalog.Binding.Marker != binding.Marker ||
                !catalog.Binding.Identity.AsSpan().SequenceEqual(binding.Identity))
                throw new InvalidDataException("La revisión del destino acumulativo está dañada o corresponde a otra carpeta.");
        }
    }

    private void ImportAccumulativeBaseline(SafeFileHandle root, MirrorPlan plan, CatalogSnapshot baseline)
    {
        using var objects = WindowsProbeNative.OpenPlainDirectory(root, ObjectDirectory);
        using var algorithm = WindowsProbeNative.OpenPlainDirectory(objects, Algorithm);
        foreach (var file in DistinctObjects(baseline.Files))
        {
            observe?.Invoke(new(WorkPhase.Copying, 0, baseline.Files.Length, file.RelativePath));
            string digest = Digest(file);
            using var bucket = WindowsProbeNative.OpenOrCreatePlainDirectory(algorithm, digest[..2]);
            if (!WindowsProbeNative.PlainFileExists(bucket, digest))
                _ = RescueQuarantinedObject(root, digest, file);
            if (!WindowsProbeNative.PlainFileExists(bucket, digest))
            {
                string final = ObjectPath(digest);
                string temporary = Path.Combine(Path.GetDirectoryName(final)!, FolderBatch.ControlPrefix + digest + "-" + plan.Generation + ".tmp");
                var recovered = CopyRecovery.Recover(workspace.Root, temporary, final, expectedHash: file.Hash);
                if (recovered == RecoveryResult.NoReceipt)
                {
                    string input = Path.Combine(MirrorCurrentPath, file.RelativePath);
                    using var fence = new DirectoryFence(MirrorCurrentPath, input);
                    using var current = WindowsProbeNative.OpenPlainFile(fence.Parent(input), input, FileAccess.Read, FileShare.Read);
                    RequireObject(current, file.RelativePath, file);
                    file.Metadata?.RequireMatch(current.SafeFileHandle);
                    VerifiedCopy.Run(input, final, temporary, expectedHash: file.Hash, recoveryRoot: workspace.Root,
                        resumeUnverifiedPrefix: true);
                }
                else if (recovered is not (RecoveryResult.Published or RecoveryResult.AlreadyPublished))
                    throw new InvalidDataException("Un archivo exclusivo del destino necesita revisión: " + recovered);
            }
            VerifyObject(bucket, digest, file);
        }
    }
}
