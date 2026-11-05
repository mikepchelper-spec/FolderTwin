using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

internal sealed record MirrorPreview(string Generation, BatchPlan SourcePlan, string? Previous, byte[] PreviousHash,
    byte[] RegistryHash, byte[] CurrentIdentity, int Added, int Replaced, int Removed, int AddedDirectories, int RemovedDirectories,
    string[] Conflicts, long StageBytes, long EstimatedBytes, ulong AvailableBytes, int MaximumChangePercent,
    bool RequiresEmptyReview, bool RequiresMassReview);

internal sealed partial class VersionStore
{
    internal string MirrorCurrentPath => Path.Combine(Destination, MirrorJournal.DirectoryName, MirrorJournal.CurrentName);

    internal void InitializeMirror()
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.ReadWrite);
        if (!registeredGenerations)
            throw new InvalidDataException("El espejo protegido requiere un almacén nuevo con registro de generaciones.");
        _ = GenerationRegistry.Read(store.Root, binding);
        MirrorJournal.Initialize(store.Root, binding);
    }

    internal MirrorPreview PreviewMirror(string source, int maximumChangePercent = 25)
    {
        using var scope = workspace.OpenRegisteredRoot();
        workspace.CheckRoots(source, Destination);
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.ReadWrite);
        return BuildMirrorPreview(scope.Root, store.Root, source, maximumChangePercent);
    }

    private MirrorPreview BuildMirrorPreview(SafeFileHandle scope, SafeFileHandle root, string source, int maximumChangePercent)
    {
        using var mirror = MirrorJournal.Open(root, binding, out var currentIdentity);
        RequireNoPendingMirror(root);
        if (maximumChangePercent is < 1 or > 100)
            throw new InvalidDataException("El umbral de cambios debe estar entre 1 y 100 por ciento.");
        var registry = GenerationRegistry.Read(root, binding);
        using var origin = new DirectoryFence(source);
        byte[] sourceIdentity = WindowsProbeNative.DirectoryIdentity(origin.Root);
        var sourcePlan = FolderBatch.AnalyzeSource(workspace.Root, source, Destination, binding, sourceIdentity, BatchCatalog.VersionMaximumEntries);
        PendingBatch.ValidatePlan(sourcePlan, BatchCatalog.VersionMaximumEntries);
        FolderBatch.RequireSameInventory(sourcePlan);
        string generation = NewGenerationId();
        var before = MirrorPrevious(scope, root, registry.MirrorGeneration, sourceIdentity);
        if (before is not null && !before.DocumentHash.AsSpan().SequenceEqual(registry.Generations[registry.MirrorGeneration!]))
            throw new InvalidDataException("El catálogo actual del espejo no coincide con su registro.");
        var observed = FolderBatch.AnalyzeSource(workspace.Root, MirrorCurrentPath, Destination, binding, currentIdentity, BatchCatalog.VersionMaximumEntries);
        string[] conflicts = MirrorConflicts(before, observed);
        var desired = sourcePlan.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var previous = before?.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        int added = desired.Keys.Count(path => !previous.ContainsKey(path));
        int replaced = desired.Values.Count(file => previous.TryGetValue(file.RelativePath, out var old) && !SameMirrorFile(old, file));
        int removed = previous.Keys.Count(path => !desired.ContainsKey(path));
        int addedDirectories = sourcePlan.Directories.Except(before?.Directories ?? [], StringComparer.Ordinal).Count();
        int removedDirectories = (before?.Directories ?? []).Except(sourcePlan.Directories, StringComparer.Ordinal).Count();
        long stageBytes = desired.Values.Where(file => !previous.TryGetValue(file.RelativePath, out var old) || !SameMirrorFile(old, file)).Sum(file => file.Length);
        long previousEntries = previous.Count + (before?.Directories.Length ?? 0);
        bool mass = previousEntries > 0 && (replaced + removed + removedDirectories) * 100L >= previousEntries * maximumChangePercent;
        return new(generation, sourcePlan, registry.MirrorGeneration, before?.DocumentHash ?? new byte[32], registry.LastHash,
            currentIdentity, added, replaced, removed, addedDirectories, removedDirectories, conflicts, stageBytes,
            checked(sourcePlan.Bytes + stageBytes), LabWorkspace.AvailableSpace(root), maximumChangePercent,
            sourcePlan.Files.Length == 0 && sourcePlan.Directories.Length == 0, mass);
    }

    private CatalogSnapshot? MirrorPrevious(SafeFileHandle scope, SafeFileHandle root, string? generation, byte[]? sourceIdentity = null)
    {
        if (generation is null)
            return null;
        var catalog = ReadPoint(root, generation);
        if (catalog.Degraded)
            throw new InvalidDataException("El punto anterior del espejo requiere sus dos catálogos íntegros.");
        using var pending = WindowsProbeNative.OpenPlainDirectory(root, PendingDirectory);
        var intent = ReadIntent(scope, root, pending, generation);
        if (sourceIdentity is not null && !intent.SourceIdentity.AsSpan().SequenceEqual(sourceIdentity))
            throw new InvalidDataException("El espejo pertenece a otra identidad de origen.");
        VerifyObjects(root, catalog.Files);
        return catalog;
    }

    private static string[] MirrorConflicts(CatalogSnapshot? expected, BatchPlan observed)
    {
        var actual = observed.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var wanted = expected?.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        return wanted.Keys.Union(actual.Keys, StringComparer.Ordinal)
            .Where(path => !wanted.TryGetValue(path, out var old) || !actual.TryGetValue(path, out var file) || !SameMirrorFile(old, file))
            .Concat((expected?.Directories ?? []).Except(observed.Directories, StringComparer.Ordinal))
            .Concat(observed.Directories.Except(expected?.Directories ?? [], StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool SameMirrorFile(BatchFile first, BatchFile second) => first.Length == second.Length &&
        first.Hash.AsSpan().SequenceEqual(second.Hash) && first.Metadata == second.Metadata;

    internal MirrorOperation[] ListMirrorOperations()
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.Read);
        return ReadMirrorOperations(store.Root);
    }

    internal (bool Configured, MirrorOperation? Pending) MirrorStatus()
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.Read);
        if (!registeredGenerations)
            return (false, null);
        var operations = ReadMirrorOperations(store.Root);
        try
        {
            using var mirror = MirrorJournal.Open(store.Root, binding, out _);
            var pending = operations.Where(item => !item.Applied).ToArray();
            if (pending.Length > 1)
                throw new InvalidDataException("Hay varios diarios del espejo pendientes.");
            if (pending.Length == 0 && GenerationRegistry.Read(store.Root, binding).PendingMirrorGeneration is not null)
                throw new InvalidDataException("Falta el diario de una actualización registrada del espejo.");
            return (true, pending.SingleOrDefault());
        }
        catch (FileNotFoundException) when (operations.Length == 0) { return (false, null); }
    }

    private MirrorOperation[] ReadMirrorOperations(SafeFileHandle root)
    {
        // A missing mirror is valid only while no mirror reference has ever been published.
        SafeFileHandle presence;
        try { presence = WindowsProbeNative.OpenPlainDirectory(root, MirrorJournal.DirectoryName); }
        catch (FileNotFoundException)
        {
            var registry = GenerationRegistry.Read(root, binding);
            if (registry.MirrorGeneration is not null || registry.PendingMirrorGeneration is not null)
                throw new InvalidDataException("Falta el espejo registrado. La limpieza queda suspendida.");
            return [];
        }
        using (presence)
        using (var mirror = MirrorJournal.Open(root, binding, out var currentIdentity))
        using (var operations = WindowsProbeNative.OpenPlainDirectory(mirror, MirrorJournal.OperationsName))
            return WindowsProbeNative.ReadDirectory(operations).Where(entry => entry.Name is not ("." or "..")).Select(entry =>
            {
                using var directory = WindowsProbeNative.OpenPlainDirectory(operations, entry.Name);
                var plan = MirrorJournal.Read(directory, entry.Name, binding, currentIdentity);
                bool ready = MirrorJournal.HasState(directory, plan, "ready");
                bool applied = MirrorJournal.HasState(directory, plan, "applied");
                if (applied && !ready)
                    throw new InvalidDataException("El diario del espejo tiene estados contradictorios.");
                return new MirrorOperation(plan.Generation, plan.Previous, ready, applied);
            }).ToArray();
    }

    private void RequireNoPendingMirror(SafeFileHandle root)
    {
        if (ReadMirrorOperations(root).Any(operation => !operation.Applied) ||
            GenerationRegistry.Read(root, binding).PendingMirrorGeneration is not null)
            throw new InvalidDataException("Hay una actualización de espejo pendiente. Reanúdala antes de continuar.");
    }
}
