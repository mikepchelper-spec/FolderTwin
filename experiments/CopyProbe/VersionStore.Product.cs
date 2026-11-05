using System.Security.Cryptography;
using System.Text;

internal enum ProductStoreStep { Prepared, DirectoriesCreated, RegistryCreated, StoreCreated }

internal sealed partial class VersionStore
{
    private static ReadOnlySpan<byte> ProductStoreMagic => "FTVERS03"u8;
    private const string InitializationName = ".foldertwin-version-initialize";

    internal static VersionStore OpenProduct(ProductJob job, bool initialize = false,
        Action<ProductStoreStep>? checkpoint = null)
    {
        job.Options.Validate();
        var scope = new VersionScope(job);
        using var root = scope.OpenRegisteredRoot();
        using var marker = job.Destination.OpenVerifiedMarker(root.Root);
        using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName,
            initialize ? FileAccess.ReadWrite : FileAccess.Read, FileShare.None,
            initialize ? FileMode.OpenOrCreate : FileMode.Open);
        if (!WindowsProbeNative.PlainFileExists(root.Root, StoreName) && initialize)
        {
            string[] directories = [PrimaryDirectory, ReplicaDirectory, CommitDirectory, PendingDirectory, ObjectDirectory];
            bool prepared = WindowsProbeNative.PlainFileExists(root.Root, InitializationName);
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                DestinationBinding.MarkerName, FolderBatch.LockName, InitializationName, InitializationName + ".tmp"
            };
            if (prepared)
                allowed.UnionWith(directories.Concat(new[] { StoreName + ".tmp", GenerationRegistry.PrimaryName,
                    GenerationRegistry.PrimaryName + ".tmp", GenerationRegistry.ReplicaName, GenerationRegistry.ReplicaName + ".tmp" }));
            if (WindowsProbeNative.ReadDirectory(root.Root).Any(entry => entry.Name is not ("." or "..") && !allowed.Contains(entry.Name)))
                throw new InvalidDataException("La carpeta dedicada contiene datos ajenos a la preparación del almacén.");
            byte[] intent = [.. "FTVINIT1"u8, .. job.Id.ToByteArray(), .. job.Source.Identity, .. job.Destination.Identity,
                .. job.Destination.Marker.ToByteArray(), .. SHA256.HashData(Encoding.UTF8.GetBytes(job.Source.VolumePath))];
            RetentionJournal.WriteFile(root.Root, InitializationName, intent);
            checkpoint?.Invoke(ProductStoreStep.Prepared);
            foreach (string name in directories)
            {
                using var directory = WindowsProbeNative.OpenOrCreatePlainDirectory(root.Root, name);
                if (name == ObjectDirectory)
                {
                    if (WindowsProbeNative.ReadDirectory(directory).Any(entry => entry.Name is not ("." or "..") && entry.Name != Algorithm))
                        throw new InvalidDataException("Hay objetos inesperados durante la preparación del almacén.");
                    using var algorithm = WindowsProbeNative.OpenOrCreatePlainDirectory(directory, Algorithm);
                    LabWorkspace.RequireEmpty(algorithm);
                }
                else LabWorkspace.RequireEmpty(directory);
            }
            checkpoint?.Invoke(ProductStoreStep.DirectoriesCreated);
            GenerationRegistry.Initialize(root.Root, job.Destination, resumeInitialization: true);
            checkpoint?.Invoke(ProductStoreStep.RegistryCreated);
            RetentionJournal.WriteFile(root.Root, StoreName,
                [.. ProductStoreMagic, .. job.Destination.Identity, .. job.Destination.Marker.ToByteArray()]);
            checkpoint?.Invoke(ProductStoreStep.StoreCreated);
        }
        using var record = WindowsProbeNative.OpenPlainFile(root.Root, StoreName, FileAccess.Read, FileShare.Read);
        byte[] expected = [.. ProductStoreMagic, .. job.Destination.Identity, .. job.Destination.Marker.ToByteArray()];
        Span<byte> observed = stackalloc byte[48];
        if (record.ReadAtLeast(observed, observed.Length, false) != observed.Length || record.ReadByte() != -1 ||
            !observed.SequenceEqual(expected))
            throw new InvalidDataException("El almacén de producto está dañado o pertenece a otro trabajo.");
        return new(scope, job.Destination.LastVolumePath, job.Destination, registeredGenerations: true);
    }
}
