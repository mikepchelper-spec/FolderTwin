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

        var requested = hints.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var touched = before.Files.Where(file => requested.Contains(file.RelativePath)).ToArray();
        var sourceInventory = FolderInventory.Scan(source, BatchCatalog.VersionMaximumEntries);
        if (touched.Length == 0 || sourceInventory.Entries.Any(entry => entry.IsDirectory && requested.Contains(entry.RelativePath)) ||
            !SameScopedLayout(sourceInventory, before, requested)) return null;
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
            var fresh = ReadScopedFile(source, old.RelativePath);
            if (!SameMirrorFile(old, fresh)) replacements.Add(old.RelativePath, fresh);
        }
        int previousEntries = before.Files.Length + before.Directories.Length;
        if (replacements.Count * 100L >= previousEntries * 25L) return null;
        string generation = NewGenerationId();
        var desired = new BatchPlan(workspace.Root, source, Destination, sourceIdentity, binding, before.Directories,
            before.Files.Select(file => replacements.GetValueOrDefault(file.RelativePath, file)).ToArray());
        var baseline = new BatchPlan(workspace.Root, MirrorCurrentPath, Destination, currentIdentity, binding, before.Directories, before.Files);
        int catalogBytes = 0;
        byte[] hash = replacements.Count == 0 ? before.DocumentHash : BatchCatalog.WriteMetadata(Stream.Null, desired, generation, out catalogBytes);
        long bytes = replacements.Values.Sum(file => file.Length);
        return new(generation, desired, hash, registry.MirrorGeneration, before.DocumentHash, registry.LastHash, currentIdentity,
            0, replacements.Count, 0, 0, 0, [], bytes, checked(2 * bytes + (workspace.IsAccumulative ? 5L : 3L) * catalogBytes),
            LabWorkspace.AvailableSpace(root), 25, false, false, baseline, previousEntries, replacements.Keys.ToArray(), Scoped: true);
    }

    private static bool SameScopedLayout(InventoryResult inventory, CatalogSnapshot baseline, HashSet<string>? mayResize = null,
        HashSet<string>? mayBeMissing = null)
    {
        if (!inventory.Complete) return false;
        int missingAllowed = mayBeMissing is null ? 0 : mayBeMissing.Count - inventory.Entries.Count(entry =>
            !entry.IsDirectory && mayBeMissing.Contains(entry.RelativePath));
        if (inventory.Entries.Length + missingAllowed != baseline.Files.Length + baseline.Directories.Length) return false;
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
            throw new InvalidDataException("Cambió la identidad de una raíz durante el reemplazo acotado.");
        try
        {
            if (SameScopedLayout(FolderInventory.Scan(root, BatchCatalog.VersionMaximumEntries), layout) &&
                touched.Files.All(file => SameMirrorFile(file, ReadScopedFile(root, file.RelativePath)))) return;
        }
        catch (Exception error) when (source && VersionJob.Unavailable(error))
        {
            throw new SourceChangedDuringBackupException("Un archivo seleccionado desapareció antes de publicar.", error);
        }
        const string message = "El árbol o los archivos seleccionados cambiaron durante el reemplazo acotado.";
        throw source ? new SourceChangedDuringBackupException(message) : new InvalidDataException(message);
    }

    private static (CatalogSnapshot Before, CatalogSnapshot After) ScopeMirror(CatalogSnapshot? before, CatalogSnapshot after)
    {
        if (before is null || !before.Directories.SequenceEqual(after.Directories) || before.Files.Length != after.Files.Length)
            throw new InvalidDataException("Un diario acotado cambió la estructura del respaldo.");
        var old = new List<BatchFile>();
        var next = new List<BatchFile>();
        foreach (var (previous, current) in before.Files.Zip(after.Files))
        {
            if (previous.RelativePath != current.RelativePath)
                throw new InvalidDataException("Un diario acotado cambió las rutas del respaldo.");
            if (SameMirrorFile(previous, current)) continue;
            old.Add(previous);
            next.Add(current);
        }
        if (next.Count == 0 || next.Count > ProductWatchSignal.MaximumEditHints ||
            next.Count * 100L >= (before.Files.Length + before.Directories.Length) * 25L)
            throw new InvalidDataException("Un diario acotado supera el alcance de reemplazo admitido.");
        return (before with { Files = old.ToArray(), Directories = [] }, after with { Files = next.ToArray(), Directories = [] });
    }
}
