using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

internal sealed record VersionPoint(string Id, DateTimeOffset CreatedUtc, int FileCount, bool Degraded);

internal sealed class VersionStore
{
    private const string StoreName = ".foldertwin-version-store";
    private const string PrimaryDirectory = "manifests";
    private const string ReplicaDirectory = "manifests_backup";
    private const string CommitDirectory = "commits";
    private const string ObjectDirectory = "objects";
    private const string Algorithm = "sha256";
    private const string TimeFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";
    private static ReadOnlySpan<byte> StoreMagic => "FTVERS01"u8;
    private readonly LabWorkspace workspace;
    private readonly DestinationBinding binding;
    internal string Destination { get; }

    private VersionStore(LabWorkspace workspace, string destination, DestinationBinding binding)
    {
        this.workspace = workspace;
        Destination = destination;
        this.binding = binding;
    }

    internal static VersionStore Initialize(LabWorkspace workspace, string destination)
    {
        using var scope = workspace.OpenRegisteredRoot();
        workspace.CheckChild(destination);
        using var store = new DirectoryFence(destination);
        LabWorkspace.RequireEmpty(store.Root);
        var binding = DestinationBinding.Register(store.Root);
        using (var operation = WindowsProbeNative.OpenPlainFile(store.Root, FolderBatch.LockName, FileAccess.ReadWrite,
            FileShare.None, FileMode.CreateNew))
        {
            foreach (string directory in new[] { PrimaryDirectory, ReplicaDirectory, CommitDirectory })
                using (WindowsProbeNative.OpenOrCreatePlainDirectory(store.Root, directory)) { }
            using (var objects = WindowsProbeNative.OpenOrCreatePlainDirectory(store.Root, ObjectDirectory))
            using (WindowsProbeNative.OpenOrCreatePlainDirectory(objects, Algorithm)) { }
            using var record = WindowsProbeNative.OpenPlainFile(store.Root, StoreName, FileAccess.Write, FileShare.None, FileMode.CreateNew);
            record.Write(StoreMagic);
            record.Write(binding.Identity);
            record.Write(binding.Marker.ToByteArray());
            record.Flush(flushToDisk: true);
        }
        return new(workspace, destination, binding);
    }

    internal static VersionStore Open(LabWorkspace workspace, string destination)
    {
        using var scope = workspace.OpenRegisteredRoot();
        workspace.CheckChild(destination);
        using var store = new DirectoryFence(destination);
        using var operation = OpenLock(store.Root, FileAccess.Read);
        using var record = WindowsProbeNative.OpenPlainFile(store.Root, StoreName, FileAccess.Read, FileShare.Read);
        Span<byte> bytes = stackalloc byte[48];
        if (record.ReadAtLeast(bytes, bytes.Length, false) != bytes.Length || record.ReadByte() != -1 ||
            !bytes[..8].SequenceEqual(StoreMagic))
            throw new InvalidDataException("El registro del almacén de versiones está dañado.");
        var binding = new DestinationBinding
        {
            Version = 1, Identity = bytes[8..32].ToArray(), Marker = new Guid(bytes[32..]),
            LastVolumePath = WindowsProbeNative.VolumePath(store.Root)
        };
        using var marker = binding.OpenVerifiedMarker(store.Root);
        return new(workspace, destination, binding);
    }

    internal static string NewGenerationId() =>
        DateTimeOffset.UtcNow.ToString(TimeFormat, CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");

    internal void CreatePoint(string source, string generation, Action<CatalogStep>? checkpoint = null)
    {
        _ = GenerationTime(generation);
        using var scope = workspace.OpenRegisteredRoot();
        workspace.CheckRoots(source, Destination);
        using var origin = new DirectoryFence(source);
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.ReadWrite);
        byte[] identity = WindowsProbeNative.DirectoryIdentity(origin.Root);
        if (identity.AsSpan().SequenceEqual(binding.Identity))
            throw new InvalidDataException("El origen y el almacén son la misma carpeta.");
        var plan = FolderBatch.AnalyzeSource(workspace.Root, source, Destination, binding, identity);
        FolderBatch.ValidateEntries(plan.Files, plan.Directories);
        _ = BatchCatalog.WriteMetadata(Stream.Null, plan, generation);
        LabWorkspace.RequireSpace(0, LabWorkspace.AvailableSpace(store.Root));
        using var objects = WindowsProbeNative.OpenPlainDirectory(store.Root, ObjectDirectory);
        using var algorithm = WindowsProbeNative.OpenPlainDirectory(objects, Algorithm);
        foreach (var file in DistinctObjects(plan.Files))
        {
            string digest = Digest(file);
            string final = ObjectPath(digest);
            using var bucket = WindowsProbeNative.OpenOrCreatePlainDirectory(algorithm, digest[..2]);
            if (!WindowsProbeNative.PlainFileExists(bucket, digest))
            {
                string temporary = Path.Combine(Path.GetDirectoryName(final)!, FolderBatch.ControlPrefix + digest + ".tmp");
                var recovered = CopyRecovery.Recover(workspace.Root, temporary, final, expectedHash: file.Hash);
                if (recovered == RecoveryResult.NoReceipt)
                    VerifiedCopy.Run(Path.Combine(source, file.RelativePath), final, temporary, expectedHash: file.Hash, recoveryRoot: workspace.Root);
                else if (recovered is not (RecoveryResult.Published or RecoveryResult.AlreadyPublished))
                    throw new InvalidDataException($"El objeto {digest} requiere revisión. Estado {recovered}.");
            }
            VerifyObject(bucket, digest, file);
        }
        FolderBatch.RequireSameInventory(plan);
        WithCatalogFiles(store.Root, generation, files =>
        {
            BatchCatalog.PublishLocked(plan, files, generation, () => VerifyObjects(store.Root, plan.Files), checkpoint);
            return true;
        });
    }

    internal VersionPoint[] ListPoints()
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.Read);
        using var commits = WindowsProbeNative.OpenPlainDirectory(store.Root, CommitDirectory);
        return WindowsProbeNative.ReadDirectory(commits)
            .Where(entry => entry.Name.EndsWith(".commit", StringComparison.Ordinal))
            .Select(entry => entry.Name[..^".commit".Length])
            .OrderDescending(StringComparer.Ordinal)
            .Select(id =>
            {
                DateTimeOffset date = GenerationTime(id);
                var catalog = ReadPoint(store.Root, id);
                return new VersionPoint(id, date, catalog.Files.Length, catalog.Degraded);
            }).ToArray();
    }

    internal void Verify(string generation)
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.Read);
        VerifyObjects(store.Root, ReadPoint(store.Root, generation).Files);
    }

    internal CatalogSnapshot Restore(string generation, string output)
    {
        using var scope = workspace.OpenRegisteredRoot();
        workspace.CheckRoots(Destination, output);
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.Read);
        using var target = new DirectoryFence(output);
        LabWorkspace.RequireEmpty(target.Root);
        var catalog = ReadPoint(store.Root, generation);
        VerifyObjects(store.Root, catalog.Files);
        LabWorkspace.RequireSpace(catalog.Files.Sum(file => file.Length), LabWorkspace.AvailableSpace(target.Root));
        DestinationBinding.Register(target.Root);
        foreach (string relative in catalog.Directories)
            FolderBatch.WithDirectories(target.Root, relative, () => true);
        foreach (var file in catalog.Files)
        {
            string final = Path.Combine(output, file.RelativePath);
            VerifiedCopy.Run(ObjectPath(Digest(file)), final, FolderBatch.TemporaryPath(output, file.RelativePath),
                expectedHash: file.Hash, recoveryRoot: workspace.Root, metadata: file.Metadata);
        }
        foreach (var file in catalog.Files)
            BatchCatalog.VerifyFile(output, file);
        return catalog;
    }

    private static FileStream OpenLock(SafeFileHandle root, FileAccess access) =>
        WindowsProbeNative.OpenPlainFile(root, FolderBatch.LockName, access, FileShare.None);

    private static CatalogSnapshot ReadPoint(SafeFileHandle store, string generation)
    {
        _ = GenerationTime(generation);
        return WithCatalogFiles(store, generation, files => BatchCatalog.ReadLocked(store, files, generation));
    }

    private static T WithCatalogFiles<T>(SafeFileHandle store, string generation, Func<CatalogFiles, T> action)
    {
        using var primary = WindowsProbeNative.OpenPlainDirectory(store, PrimaryDirectory);
        using var replica = WindowsProbeNative.OpenPlainDirectory(store, ReplicaDirectory);
        using var commits = WindowsProbeNative.OpenPlainDirectory(store, CommitDirectory);
        string name = generation + ".jsonl";
        return action(new(new(primary, name), new(replica, name), new(commits, generation + ".commit")));
    }

    private static DateTimeOffset GenerationTime(string id)
    {
        const int timeLength = 23;
        if (id.Length != timeLength + 1 + 32 || id[timeLength] != '-' ||
            !DateTimeOffset.TryParseExact(id.AsSpan(0, timeLength), TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var date) ||
            !Guid.TryParseExact(id.AsSpan(timeLength + 1), "N", out var key) ||
            !id.AsSpan(timeLength + 1).SequenceEqual(key.ToString("N")))
            throw new InvalidDataException("Identificador de generación no válido.");
        return date;
    }

    private static IEnumerable<BatchFile> DistinctObjects(IEnumerable<BatchFile> files) =>
        files.GroupBy(Digest, StringComparer.Ordinal).Select(group =>
        {
            var file = group.First();
            if (group.Any(candidate => candidate.Length != file.Length))
                throw new InvalidDataException("Un objeto tiene tamaños contradictorios en el catálogo.");
            return file;
        });

    private static string Digest(BatchFile file) => Convert.ToHexStringLower(file.Hash);

    private string ObjectPath(string digest) => Path.Combine(Destination, ObjectDirectory, Algorithm, digest[..2], digest);

    private static void VerifyObjects(SafeFileHandle store, BatchFile[] files)
    {
        using var objects = WindowsProbeNative.OpenPlainDirectory(store, ObjectDirectory);
        using var algorithm = WindowsProbeNative.OpenPlainDirectory(objects, Algorithm);
        foreach (var file in DistinctObjects(files))
        {
            string digest = Digest(file);
            using var bucket = WindowsProbeNative.OpenPlainDirectory(algorithm, digest[..2]);
            VerifyObject(bucket, digest, file);
        }
    }

    private static void VerifyObject(SafeFileHandle directory, string digest, BatchFile file)
    {
        using var input = WindowsProbeNative.OpenPlainFile(directory, digest, FileAccess.Read, FileShare.Read);
        WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, digest);
        if (input.Length != file.Length || !SHA256.HashData(input).AsSpan().SequenceEqual(file.Hash))
            throw new InvalidDataException($"El objeto {digest} no coincide con el SHA-256 del catálogo.");
        WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, digest);
    }
}
