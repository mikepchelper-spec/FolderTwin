// This facade never registers a job or exposes writing/retention operations.
internal sealed class ProductBackupRecovery
{
    private readonly VersionStore store;
    internal string Path => store.Destination;

    private ProductBackupRecovery(VersionStore store) => this.store = store;

    internal static ProductBackupRecovery Open(string destination, Action<WorkProgress>? observe = null) =>
        new(VersionStore.OpenProductRecovery(destination, observe));

    internal VersionPoint[] ListPoints() => store.ListPoints();
    internal CatalogSnapshot ReadCatalog(string generation) => store.ReadCatalogs(generation, null).Current;
    internal bool Verify(string generation) => store.Verify(generation);
    internal CatalogSnapshot Restore(string generation, string emptyOutput, string? relativeFile = null) =>
        store.Restore(generation, emptyOutput, relativeFile);

    internal string? RegistryProblem()
    {
        try { _ = store.RegisteredGenerations(); return null; }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            return "El registro del historial necesita revisión. Las fechas se leen de sus catálogos y su recuperación comprobará los objetos disponibles.";
        }
    }
}

internal sealed partial class VersionStore
{
    internal static VersionStore OpenProductRecovery(string destination, Action<WorkProgress>? observe = null)
    {
        using var root = new DirectoryFence(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(destination)));
        WindowsProbeNative.RequireNtfsVolume(root.Root, writable: false);
        WindowsProbeNative.RequireSupportedData(root.Root, "respaldo elegido");
        using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.Read, FileShare.None);
        using var record = WindowsProbeNative.OpenPlainFile(root.Root, StoreName, FileAccess.Read, FileShare.Read);
        Span<byte> bytes = stackalloc byte[48];
        if (record.ReadAtLeast(bytes, bytes.Length, false) != bytes.Length || record.ReadByte() != -1 ||
            !bytes[..8].SequenceEqual(ProductStoreMagic))
            throw new InvalidDataException("La carpeta elegida no contiene un respaldo de producto compatible.");
        var binding = new DestinationBinding
        {
            Version = 1, Identity = bytes[8..32].ToArray(), Marker = new Guid(bytes[32..]),
            LastVolumePath = WindowsProbeNative.VolumePath(root.Root)
        };
        if (binding.Marker == Guid.Empty) throw new InvalidDataException("El respaldo no tiene una identidad válida.");
        using var marker = binding.OpenVerifiedMarker(root.Root);
        return new(new VersionScope(binding), binding.LastVolumePath, binding, registeredGenerations: true, observe);
    }
}
