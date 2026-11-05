using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal sealed partial class VersionStore
{
    internal string BeginMirror(MirrorPreview review, bool emptyReviewed = false, bool massReviewed = false,
        Action<MirrorStep>? checkpoint = null, Action<RegistryStep>? registryCheckpoint = null,
        Action<RegistryStep>? beginRegistryCheckpoint = null)
    {
        _ = GenerationTime(review.Generation);
        using var scope = workspace.OpenRegisteredRoot();
        workspace.CheckRoots(review.SourcePlan.Source, Destination);
        using var root = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(root.Root);
        using var operation = OpenLock(root.Root, FileAccess.ReadWrite);
        var fresh = BuildMirrorPreview(scope.Root, root.Root, review.SourcePlan.Source, review.MaximumChangePercent, review.Generation);
        if (!fresh.RegistryHash.AsSpan().SequenceEqual(review.RegistryHash) || fresh.Previous != review.Previous ||
            !fresh.CurrentIdentity.AsSpan().SequenceEqual(review.CurrentIdentity) || fresh.Conflicts.Length != 0 ||
            !fresh.SourcePlan.SourceIdentity.AsSpan().SequenceEqual(review.SourcePlan.SourceIdentity) ||
            !fresh.DocumentHash.AsSpan().SequenceEqual(review.DocumentHash))
            throw new InvalidDataException("El origen, el espejo o su registro cambiaron desde la revisión. Repite el análisis.");
        if (fresh.RequiresEmptyReview && !emptyReviewed || fresh.RequiresMassReview && !massReviewed)
            throw new InvalidDataException("Revisa expresamente el origen vacío o el alcance de los cambios antes de actualizar el espejo.");
        LabWorkspace.RequireSpace(fresh.EstimatedBytes, fresh.AvailableBytes);
        using var mirror = MirrorJournal.Open(root.Root, binding, out var currentIdentity);
        using var operations = WindowsProbeNative.OpenPlainDirectory(mirror, MirrorJournal.OperationsName);
        using var directory = WindowsProbeNative.OpenOrCreatePlainDirectory(operations, review.Generation);
        LabWorkspace.RequireEmpty(directory);
        var plan = new MirrorPlan(2, review.Generation, fresh.DocumentHash, fresh.Previous, fresh.PreviousHash,
            Path.GetFileName(fresh.SourcePlan.Source), fresh.SourcePlan.SourceIdentity, currentIdentity,
            binding.Marker, binding.Identity, fresh.MaximumChangePercent, emptyReviewed, massReviewed);
        RetentionJournal.WriteFile(directory, "plan", MirrorJournal.Encode(plan));
        checkpoint?.Invoke(MirrorStep.Planned);
        ContinueMirror(scope.Root, root.Root, directory, plan, checkpoint, registryCheckpoint, beginRegistryCheckpoint);
        return plan.Generation;
    }

    internal void ResumeMirror(string generation, Action<MirrorStep>? checkpoint = null, Action<RegistryStep>? registryCheckpoint = null)
    {
        _ = GenerationTime(generation);
        using var scope = workspace.OpenRegisteredRoot();
        using var root = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(root.Root);
        using var operation = OpenLock(root.Root, FileAccess.ReadWrite);
        using var mirror = MirrorJournal.Open(root.Root, binding, out var currentIdentity);
        using var operations = WindowsProbeNative.OpenPlainDirectory(mirror, MirrorJournal.OperationsName);
        using var directory = WindowsProbeNative.OpenPlainDirectory(operations, generation);
        var plan = MirrorJournal.Read(directory, generation, binding, currentIdentity);
        if (ReadMirrorOperations(root.Root).Any(item => !item.Applied && item.Generation != generation))
            throw new InvalidDataException("Hay otra actualización de espejo pendiente.");
        ContinueMirror(scope.Root, root.Root, directory, plan, checkpoint, registryCheckpoint, beginRegistryCheckpoint: null);
    }

    private void ContinueMirror(SafeFileHandle scope, SafeFileHandle root, SafeFileHandle directory, MirrorPlan plan,
        Action<MirrorStep>? checkpoint, Action<RegistryStep>? registryCheckpoint, Action<RegistryStep>? beginRegistryCheckpoint)
    {
        if (MirrorJournal.HasState(directory, plan, "applied"))
            return;
        // Hold the registered current directory across all observations and mutations.
        using var currentRoot = new DirectoryFence(MirrorCurrentPath);
        if (!WindowsProbeNative.DirectoryIdentity(currentRoot.Root).AsSpan().SequenceEqual(plan.CurrentIdentity))
            throw new InvalidDataException("La raíz actual del espejo fue sustituida.");
        using var pending = WindowsProbeNative.OpenPlainDirectory(root, PendingDirectory);
        BatchPlan intent;
        try { intent = ReadIntent(scope, root, pending, plan.Generation); }
        catch (FileNotFoundException)
        {
            intent = ReadMirrorSource(plan);
            PendingBatch.SaveLocked(intent, scope, IntentFiles(pending, plan.Generation));
        }
        if (!intent.SourceIdentity.AsSpan().SequenceEqual(plan.SourceIdentity) || Path.GetFileName(intent.Source) != plan.Source ||
            !BatchCatalog.WriteMetadata(Stream.Null, intent, plan.Generation).AsSpan().SequenceEqual(plan.DocumentHash))
            throw new InvalidDataException("El plan de datos no corresponde a la actualización del espejo.");
        if (!IsCommitted(root, plan.Generation))
        {
            RecordExpected(root, intent, plan.Generation);
            CopyAndConfirm(root, intent, plan.Generation, checkpoint: null, objectCheckpoint: null);
        }
        var after = ReadPoint(root, plan.Generation);
        if (after.Degraded || !after.DocumentHash.AsSpan().SequenceEqual(plan.DocumentHash))
            throw new InvalidDataException("El nuevo punto del espejo no tiene ambos catálogos íntegros.");
        VerifyObjects(root, after.Files);
        checkpoint?.Invoke(MirrorStep.PointConfirmed);
        var before = MirrorPrevious(scope, root, plan.Previous, plan.SourceIdentity);
        if (!(before?.DocumentHash ?? new byte[32]).AsSpan().SequenceEqual(plan.PreviousHash))
            throw new InvalidDataException("El punto anterior del espejo no corresponde al diario.");
        bool ready = MirrorJournal.HasState(directory, plan, "ready");
        if (!ready)
        {
            using var origin = new DirectoryFence(intent.Source);
            RequireMirrorContents(before, plan.CurrentIdentity);
            var fresh = ReadMirrorSource(plan);
            if (fresh.Files.Length == 0 && fresh.Directories.Length == 0 && !plan.EmptyReviewed)
                throw new InvalidDataException("No se autorizó una actualización con origen vacío.");
            StageMirrorObjects(directory, plan, before, after, checkpoint);
            _ = ReadMirrorSource(plan);
            RequireMirrorContents(before, plan.CurrentIdentity);
            MirrorJournal.SetState(directory, plan, "ready");
            checkpoint?.Invoke(MirrorStep.Ready);
        }
        RegistryState registry;
        try { registry = GenerationRegistry.Read(root, binding); }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            var prefix = GenerationRegistry.ReadCommonPrefix(root, binding);
            if (prefix.PendingMirrorGeneration == plan.Generation || plan.Version == 1)
            {
                RequireMirrorContents(after, plan.CurrentIdentity);
                GenerationRegistry.SetMirror(root, binding, plan.Generation, plan.DocumentHash, registryCheckpoint, legacy: plan.Version == 1);
            }
            else
            {
                RequireMirrorContents(before, plan.CurrentIdentity);
                GenerationRegistry.SetMirrorPending(root, binding, plan.Generation, plan.DocumentHash, beginRegistryCheckpoint);
            }
            registry = GenerationRegistry.Read(root, binding);
        }
        if (registry.MirrorGeneration != plan.Previous && registry.MirrorGeneration != plan.Generation)
            throw new InvalidDataException("Otra actualización cambió el punto actual del espejo.");
        if (registry.MirrorGeneration != plan.Generation)
        {
            var presentDirectories = RequireRecoverableMirrorState(directory, plan, before, after);
            if (plan.Version == 2)
                GenerationRegistry.SetMirrorPending(root, binding, plan.Generation, plan.DocumentHash, beginRegistryCheckpoint);
            ApplyMirrorFiles(directory, plan, before, after, presentDirectories, checkpoint);
            RequireMirrorContents(after, plan.CurrentIdentity);
            checkpoint?.Invoke(MirrorStep.CurrentVerified);
            GenerationRegistry.SetMirror(root, binding, plan.Generation, plan.DocumentHash, registryCheckpoint, legacy: plan.Version == 1);
        }
        RequireMirrorContents(after, plan.CurrentIdentity);
        checkpoint?.Invoke(MirrorStep.Registered);
        // The previous verified catalog and CAS objects are the recovery area.
        // Release redundant retired files only once publication is durable.
        using (var objects = WindowsProbeNative.OpenPlainDirectory(root, ObjectDirectory))
        using (var algorithm = WindowsProbeNative.OpenPlainDirectory(objects, Algorithm))
        using (var retired = WindowsProbeNative.OpenPlainDirectory(directory, "retired"))
            foreach (var file in RetiredMirrorFiles(before, after))
            {
                string token = MirrorToken(file.RelativePath);
                if (!WindowsProbeNative.PlainFileExists(retired, token))
                    continue;
                string digest = Digest(file);
                using var bucket = WindowsProbeNative.OpenPlainDirectory(algorithm, digest[..2]);
                using var recovery = WindowsProbeNative.OpenPlainFile(bucket, digest, FileAccess.Read, FileShare.Read);
                RequireObject(recovery, file.RelativePath, file);
                using (var copy = WindowsProbeNative.OpenPlainFile(retired, token, FileAccess.Read, FileShare.None, rename: true))
                {
                    RequireObject(copy, file.RelativePath, file);
                    file.Metadata?.RequireMatch(copy.SafeFileHandle);
                    WindowsProbeNative.DeletePlainFile(copy.SafeFileHandle);
                }
                checkpoint?.Invoke(MirrorStep.Released);
            }
        MirrorJournal.SetState(directory, plan, "applied");
        checkpoint?.Invoke(MirrorStep.Applied);
    }

    private BatchPlan ReadMirrorSource(MirrorPlan plan)
    {
        string source = Path.Combine(workspace.Root, plan.Source);
        workspace.CheckRoots(source, Destination);
        using var origin = new DirectoryFence(source);
        if (!WindowsProbeNative.DirectoryIdentity(origin.Root).AsSpan().SequenceEqual(plan.SourceIdentity))
            throw new InvalidDataException("La identidad del origen cambió desde la revisión del espejo.");
        var observed = FolderBatch.AnalyzeSource(workspace.Root, source, Destination, binding, plan.SourceIdentity, BatchCatalog.VersionMaximumEntries);
        FolderBatch.RequireSameInventory(observed);
        if (!BatchCatalog.WriteMetadata(Stream.Null, observed, plan.Generation).AsSpan().SequenceEqual(plan.DocumentHash))
            throw new InvalidDataException("El origen cambió desde la revisión del espejo. Se conserva la operación pendiente.");
        return observed;
    }

    private void RequireMirrorContents(CatalogSnapshot? expected, byte[] identity)
    {
        using var current = new DirectoryFence(MirrorCurrentPath);
        if (!WindowsProbeNative.DirectoryIdentity(current.Root).AsSpan().SequenceEqual(identity))
            throw new InvalidDataException("La raíz actual del espejo fue sustituida.");
        var observed = FolderBatch.AnalyzeSource(workspace.Root, MirrorCurrentPath, Destination, binding, identity, BatchCatalog.VersionMaximumEntries);
        if (MirrorConflicts(expected, observed).Length != 0)
            throw new InvalidDataException("El espejo contiene cambios o rutas inesperadas. Se conservan para revisión.");
    }

    private static string MirrorToken(string relative) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relative)));

    private static IEnumerable<BatchFile> RetiredMirrorFiles(CatalogSnapshot? before, CatalogSnapshot after)
    {
        var next = after.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        return (before?.Files ?? []).Where(file => !next.TryGetValue(file.RelativePath, out var updated) || !SameMirrorFile(file, updated));
    }

    private HashSet<string> RequireRecoverableMirrorState(SafeFileHandle directory, MirrorPlan plan, CatalogSnapshot? before, CatalogSnapshot after)
    {
        var current = FolderBatch.AnalyzeSource(workspace.Root, MirrorCurrentPath, Destination, binding, plan.CurrentIdentity,
            BatchCatalog.VersionMaximumEntries);
        var observed = current.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var previous = (before?.Files ?? []).ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var desired = after.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var directories = (before?.Directories ?? []).Concat(after.Directories).ToHashSet(StringComparer.Ordinal);
        if (current.Directories.Any(path => !directories.Contains(path)) || current.Files.Any(file =>
            !(previous.TryGetValue(file.RelativePath, out var old) && SameMirrorFile(old, file)) &&
            !(desired.TryGetValue(file.RelativePath, out var next) && SameMirrorFile(next, file))))
            throw new InvalidDataException("El espejo contiene cambios o rutas inesperadas. Se conservan para revisión.");
        using var retired = WindowsProbeNative.OpenOrCreatePlainDirectory(directory, "retired");
        using var staged = WindowsProbeNative.OpenPlainDirectory(directory, "staged");
        foreach (var old in RetiredMirrorFiles(before, after))
        {
            bool inCurrent = observed.TryGetValue(old.RelativePath, out var found) && SameMirrorFile(old, found);
            string token = MirrorToken(old.RelativePath);
            bool inRetired = WindowsProbeNative.PlainFileExists(retired, token);
            if (inCurrent == inRetired)
                throw new InvalidDataException("La ubicación de un archivo anterior es ambigua o falta.");
            if (inRetired)
                VerifyStaged(retired, token, old);
        }
        foreach (var next in after.Files)
        {
            bool promoted = observed.TryGetValue(next.RelativePath, out var found) && SameMirrorFile(next, found);
            if (previous.TryGetValue(next.RelativePath, out var old) && SameMirrorFile(old, next))
            {
                if (!promoted)
                    throw new InvalidDataException("Falta un archivo que debía permanecer intacto en el espejo.");
                continue;
            }
            if (!promoted)
                VerifyStaged(staged, MirrorToken(next.RelativePath), next);
        }
        return current.Directories.ToHashSet(StringComparer.Ordinal);
    }

    private static void VerifyStaged(SafeFileHandle directory, string token, BatchFile file)
    {
        using var input = WindowsProbeNative.OpenPlainFile(directory, token, FileAccess.Read, FileShare.Read);
        RequireObject(input, file.RelativePath, file);
        file.Metadata?.RequireMatch(input.SafeFileHandle);
    }

    private void StageMirrorObjects(SafeFileHandle directory, MirrorPlan plan, CatalogSnapshot? before, CatalogSnapshot after,
        Action<MirrorStep>? checkpoint)
    {
        using var staged = WindowsProbeNative.OpenOrCreatePlainDirectory(directory, "staged");
        var previous = before?.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        foreach (var file in after.Files.Where(file => !previous.TryGetValue(file.RelativePath, out var old) || !SameMirrorFile(old, file)))
        {
            string token = MirrorToken(file.RelativePath);
            string final = Path.Combine(Destination, MirrorJournal.DirectoryName, MirrorJournal.OperationsName, plan.Generation, "staged", token);
            if (!WindowsProbeNative.PlainFileExists(staged, token))
            {
                string temporary = Path.Combine(Path.GetDirectoryName(final)!, FolderBatch.ControlPrefix + token + ".tmp");
                var result = CopyRecovery.Recover(workspace.Root, temporary, final, expectedHash: file.Hash, metadata: file.Metadata);
                if (result == RecoveryResult.NoReceipt)
                {
                    // CAS is already confirmed. Preserve an interrupted staging
                    // attempt rather than discard bytes to make a retry succeed.
                    foreach (string name in new[] { temporary, CopyRecovery.ReceiptDraftPath(temporary) })
                        if (WindowsProbeNative.PlainFileExists(staged, name))
                        {
                            using var abandoned = WindowsProbeNative.OpenPlainFile(staged, name, FileAccess.Read, FileShare.None, rename: true);
                            WindowsProbeNative.RenameNew(abandoned.SafeFileHandle, staged, "unverified-" + token + "-" + Guid.NewGuid().ToString("N"));
                        }
                    VerifiedCopy.Run(ObjectPath(Digest(file)), final, temporary, expectedHash: file.Hash,
                        recoveryRoot: workspace.Root, metadata: file.Metadata);
                }
                else if (result is not (RecoveryResult.Published or RecoveryResult.AlreadyPublished))
                    throw new InvalidDataException("Un archivo preparado del espejo requiere revisión: " + result);
            }
            VerifyStaged(staged, token, file);
            checkpoint?.Invoke(MirrorStep.Staged);
        }
    }

    private void ApplyMirrorFiles(SafeFileHandle directory, MirrorPlan plan, CatalogSnapshot? before, CatalogSnapshot after,
        HashSet<string> presentDirectories, Action<MirrorStep>? checkpoint)
    {
        var wanted = after.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var previous = before?.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        using var retired = WindowsProbeNative.OpenOrCreatePlainDirectory(directory, "retired");
        foreach (var file in RetiredMirrorFiles(before, after))
        {
            string token = MirrorToken(file.RelativePath);
            if (WindowsProbeNative.PlainFileExists(retired, token))
            {
                using var known = WindowsProbeNative.OpenPlainFile(retired, token, FileAccess.Read, FileShare.Read);
                RequireObject(known, token, file);
                file.Metadata?.RequireMatch(known.SafeFileHandle);
                continue;
            }
            string path = Path.Combine(MirrorCurrentPath, file.RelativePath);
            using var parent = new DirectoryFence(MirrorCurrentPath, path);
            using var current = WindowsProbeNative.OpenPlainFile(parent.Parent(path), path, FileAccess.Read, FileShare.None, rename: true);
            RequireObject(current, file.RelativePath, file);
            file.Metadata?.RequireMatch(current.SafeFileHandle);
            WindowsProbeNative.RenameNew(current.SafeFileHandle, retired, token);
            checkpoint?.Invoke(MirrorStep.Retired);
        }
        foreach (string relative in (before?.Directories ?? []).Except(after.Directories, StringComparer.Ordinal)
            .OrderByDescending(path => path.Count(c => c == Path.DirectorySeparatorChar)).ThenByDescending(path => path, StringComparer.Ordinal))
        {
            // A prior pass may already have replaced an ancestor with a file.
            if (!presentDirectories.Contains(relative))
                continue;
            string path = Path.Combine(MirrorCurrentPath, relative);
            using (var parent = new DirectoryFence(MirrorCurrentPath, path))
            using (var child = WindowsProbeNative.OpenDirectoryForDeletion(parent.Parent(path), Path.GetFileName(path)))
            {
                WindowsProbeNative.DeleteEmptyDirectory(child);
            }
            checkpoint?.Invoke(MirrorStep.DirectoryRemoved);
        }
        using (var current = new DirectoryFence(MirrorCurrentPath))
            foreach (string relative in after.Directories)
                FolderBatch.WithDirectories(current.Root, relative, () => true);
        using var staged = WindowsProbeNative.OpenOrCreatePlainDirectory(directory, "staged");
        foreach (var file in wanted.Values.Where(file => !previous.TryGetValue(file.RelativePath, out var old) || !SameMirrorFile(old, file)))
        {
            if (MirrorFileMatches(file))
                continue;
            string path = Path.Combine(MirrorCurrentPath, file.RelativePath);
            using var parent = new DirectoryFence(MirrorCurrentPath, path);
            if (WindowsProbeNative.PlainFileExists(parent.Parent(path), path))
                throw new InvalidDataException("Una ruta del espejo contiene datos inesperados. No se sobrescriben.");
            using var prepared = WindowsProbeNative.OpenPlainFile(staged, MirrorToken(file.RelativePath), FileAccess.Read, FileShare.None,
                rename: true, writeAttributes: file.Metadata?.CreationUtc is not null);
            RequireObject(prepared, file.RelativePath, file);
            file.Metadata?.RequireMatch(prepared.SafeFileHandle);
            CopyRecovery.PublishData(prepared, parent.Parent(path), path, file.Metadata);
            checkpoint?.Invoke(MirrorStep.Promoted);
        }

    }

    private bool MirrorFileMatches(BatchFile expected)
    {
        string path = Path.Combine(MirrorCurrentPath, expected.RelativePath);
        try
        {
            using var parent = new DirectoryFence(MirrorCurrentPath, path);
            using var input = WindowsProbeNative.OpenPlainFile(parent.Parent(path), path, FileAccess.Read, FileShare.Read);
            RequireObject(input, expected.RelativePath, expected);
            expected.Metadata?.RequireMatch(input.SafeFileHandle);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (InvalidDataException) { return false; }
    }
}
