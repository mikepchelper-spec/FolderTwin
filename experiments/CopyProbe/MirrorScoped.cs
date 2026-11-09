using Microsoft.Win32.SafeHandles;

internal sealed partial class VersionStore
{
    private MirrorPreview? TryPreviewScopedMirror(SafeFileHandle scope, SafeFileHandle root, string source, string[] hints)
    {
        if (!ProductWatchSignal.ValidEditHints(hints)) return null;
        using var mirror = MirrorJournal.Open(root, binding, out var currentIdentity);
        RequireNoPendingMirror(root);
        var registry = GenerationRegistry.Read(root, binding);
        using var origin = new DirectoryFence(source);
        workspace.RequireSourceIdentity(source, origin.Root);
        byte[] sourceIdentity = WindowsProbeNative.DirectoryIdentity(origin.Root);
        var before = MirrorPrevious(scope, root, registry.MirrorGeneration, sourceIdentity, verifyObjects: false);
        if (before is null || before.Files.Length == 0 || before.Files[0].CatalogVersion != 3) return null;
        if (!before.DocumentHash.AsSpan().SequenceEqual(registry.Generations[registry.MirrorGeneration!]))
            throw new InvalidDataException("El catálogo actual del espejo no coincide con su registro.");

        var requested = hints.Select(ProductWatchSignal.HintPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nameEvents = hints.Where(hint => !ProductWatchSignal.MetadataHint(hint)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceInventory = FolderInventory.Scan(source, BatchCatalog.VersionMaximumEntries);
        if (!sourceInventory.Complete || sourceInventory.Entries.Length == 0) return null;
        var knownDirectories = before.Directories.ToHashSet(StringComparer.Ordinal);
        var knownFiles = before.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<string>(StringComparer.Ordinal);
        var additions = new List<string>();
        int directoryCount = 0;
        foreach (var entry in sourceInventory.Entries)
        {
            bool hinted = requested.Contains(entry.RelativePath);
            if (entry.IsDirectory)
            {
                if (nameEvents.Contains(entry.RelativePath) || !knownDirectories.Contains(entry.RelativePath)) return null;
                directoryCount++;
            }
            else if (knownFiles.TryGetValue(entry.RelativePath, out var old))
            {
                if (old.RelativePath != entry.RelativePath || !hinted && old.Length != entry.Length) return null;
                present.Add(entry.RelativePath);
            }
            else if (hinted) additions.Add(entry.RelativePath);
            else return null;
        }
        if (directoryCount != before.Directories.Length) return null;
        var removals = workspace.IsAccumulative ? [] : before.Files.Where(file => !present.Contains(file.RelativePath)).ToArray();
        if (removals.Any(file => !requested.Contains(file.RelativePath))) return null;
        var removed = removals.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        var touched = before.Files.Where(file => requested.Contains(file.RelativePath) &&
            (present.Contains(file.RelativePath) || removed.Contains(file.RelativePath))).ToArray();
        if (touched.Length == 0 && additions.Count == 0 && !requested.Any(knownFiles.ContainsKey)) return null;
        using var current = new DirectoryFence(MirrorCurrentPath);
        if (!WindowsProbeNative.DirectoryIdentity(current.Root).AsSpan().SequenceEqual(currentIdentity))
            throw new InvalidDataException("La raíz actual del espejo fue sustituida.");
        var currentInventory = FolderInventory.Scan(MirrorCurrentPath, BatchCatalog.VersionMaximumEntries);
        if (!SameScopedLayout(currentInventory, before)) return null;
        var replacements = new Dictionary<string, BatchFile>(StringComparer.Ordinal);
        foreach (var old in touched)
        {
            observe?.Invoke(new(WorkPhase.Analyzing, 0, touched.Length, old.RelativePath));
            if (!SameMirrorFile(old, ReadScopedFile(MirrorCurrentPath, old.RelativePath)))
                throw new InvalidDataException("Hay una edición externa en el destino. Se conserva para revisión.");
            if (removed.Contains(old.RelativePath)) continue;
            var fresh = ReadScopedFile(source, old.RelativePath);
            if (!SameMirrorFile(old, fresh)) replacements.Add(old.RelativePath, fresh);
        }
        int changed = additions.Count + replacements.Count + removals.Length;
        int previousEntries = before.Files.Length + before.Directories.Length;
        if (changed * 100L >= previousEntries * 25L) return null;
        var added = additions.Select(path => ReadScopedFile(source, path)).ToArray();
        var files = before.Files.Where(file => !removed.Contains(file.RelativePath))
            .Select(file => replacements.GetValueOrDefault(file.RelativePath, file)).Concat(added).ToArray();
        var staged = replacements.Values.Concat(added).ToArray();
        string generation = NewGenerationId();
        var desired = new BatchPlan(workspace.Root, source, Destination, sourceIdentity, binding, before.Directories,
            files);
        var baseline = new BatchPlan(workspace.Root, MirrorCurrentPath, Destination, currentIdentity, binding, before.Directories, before.Files);
        int catalogBytes = 0;
        byte[] hash = changed == 0 ? before.DocumentHash : BatchCatalog.WriteMetadata(Stream.Null, desired, generation, out catalogBytes);
        long bytes = staged.Sum(file => file.Length);
        return new(generation, desired, hash, registry.MirrorGeneration, before.DocumentHash, registry.LastHash, currentIdentity,
            added.Length, replacements.Count, removals.Length, 0, 0, [], bytes, checked(2 * bytes + (workspace.IsAccumulative ? 5L : 3L) * catalogBytes),
            LabWorkspace.AvailableSpace(root), 25, false, false, baseline, previousEntries, replacements.Keys.Concat(removed).ToArray(),
            Scoped: true, ScopedFiles: staged);
    }

    private static bool SameScopedLayout(InventoryResult inventory, CatalogSnapshot baseline, HashSet<string>? mayResize = null,
        HashSet<string>? mayBeMissing = null, bool allowSubset = false)
    {
        if (!inventory.Complete) return false;
        if (allowSubset && inventory.Entries.Count(entry => entry.IsDirectory) != baseline.Directories.Length) return false;
        int missingAllowed = mayBeMissing is null ? 0 : mayBeMissing.Count - inventory.Entries.Count(entry =>
            !entry.IsDirectory && mayBeMissing.Contains(entry.RelativePath));
        if (!allowSubset && inventory.Entries.Length + missingAllowed != baseline.Files.Length + baseline.Directories.Length) return false;
        var files = baseline.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var directories = baseline.Directories.ToHashSet(StringComparer.Ordinal);
        return inventory.Entries.All(entry => entry.IsDirectory ? directories.Contains(entry.RelativePath) :
            files.TryGetValue(entry.RelativePath, out var prior) && (prior.Length == entry.Length || mayResize?.Contains(entry.RelativePath) == true));
    }

    private BatchFile ReadScopedFile(string root, string relative)
    {
        string path = Path.Combine(root, relative);
        using var fence = new DirectoryFence(root, path);
        return FolderBatch.ReadFile(fence.Parent(path), relative);
    }

    private void RequireScopedContents(string root, byte[] identity, CatalogSnapshot layout, CatalogSnapshot touched, bool source = false)
    {
        using var fence = new DirectoryFence(root);
        if (!WindowsProbeNative.DirectoryIdentity(fence.Root).AsSpan().SequenceEqual(identity))
            throw new InvalidDataException("Cambió la identidad de una raíz durante la actualización acotada.");
        try
        {
            if (SameScopedLayout(FolderInventory.Scan(root, BatchCatalog.VersionMaximumEntries), layout,
                    allowSubset: source && workspace.IsAccumulative) &&
                touched.Files.All(file => SameMirrorFile(file, ReadScopedFile(root, file.RelativePath)))) return;
        }
        catch (Exception error) when (source && VersionJob.Unavailable(error))
        {
            throw new SourceChangedDuringBackupException("Un archivo seleccionado desapareció antes de publicar.", error);
        }
        const string message = "El árbol o los archivos seleccionados cambiaron durante la actualización acotada.";
        throw source ? new SourceChangedDuringBackupException(message) : new InvalidDataException(message);
    }

    private (CatalogSnapshot Before, CatalogSnapshot After) ScopeMirror(CatalogSnapshot? before, CatalogSnapshot after)
    {
        if (before is null || !before.Directories.SequenceEqual(after.Directories))
            throw new InvalidDataException("Un diario acotado cambió las carpetas del respaldo.");
        var old = new List<BatchFile>();
        var next = new List<BatchFile>();
        int prior = 0, current = 0, changes = 0;
        // Both catalogs use ordinal path order. Walk them together so only the delta is retained.
        while (prior < before.Files.Length || current < after.Files.Length)
        {
            int order = prior == before.Files.Length ? 1 : current == after.Files.Length ? -1 :
                string.CompareOrdinal(before.Files[prior].RelativePath, after.Files[current].RelativePath);
            if (order < 0)
            {
                if (workspace.IsAccumulative) throw new InvalidDataException("Un diario acotado no puede retirar archivos acumulados.");
                old.Add(before.Files[prior++]);
            }
            else if (order > 0) next.Add(after.Files[current++]);
            else
            {
                var previousFile = before.Files[prior++];
                var currentFile = after.Files[current++];
                if (SameMirrorFile(previousFile, currentFile)) continue;
                old.Add(previousFile);
                next.Add(currentFile);
            }
            if (++changes > ProductWatchSignal.MaximumEditHints)
                throw new InvalidDataException("Un diario acotado supera el alcance admitido.");
        }
        var spellings = next.ToDictionary(file => file.RelativePath, file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (changes == 0 || changes * 100L >= (before.Files.Length + before.Directories.Length) * 25L ||
            old.Any(file => spellings.TryGetValue(file.RelativePath, out string? spelling) && spelling != file.RelativePath))
            throw new InvalidDataException("Un diario acotado supera el alcance admitido o cambia el uso de mayúsculas de una ruta.");
        return (before with { Files = old.ToArray(), Directories = [] }, after with { Files = next.ToArray(), Directories = [] });
    }
}
