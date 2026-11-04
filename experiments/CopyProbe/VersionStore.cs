using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

internal sealed record VersionPoint(string Id, DateTimeOffset CreatedUtc, int FileCount, bool Degraded, string? Problem = null);
internal sealed record PendingVersion(string Id, string Source, int FileCount);

internal sealed class VersionStore
{
    private const string StoreName = ".foldertwin-version-store";
    private const string PrimaryDirectory = "manifests";
    private const string ReplicaDirectory = "manifests_backup";
    private const string CommitDirectory = "commits";
    private const string PendingDirectory = "pending";
    private const string LocationSuffix = ".location.json";
    private const string ObjectDirectory = "objects";
    private const string Algorithm = "sha256";
    private const string TimeFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";
    private static ReadOnlySpan<byte> StoreMagic => "FTVERS01"u8;
    private readonly LabWorkspace workspace;
    private readonly DestinationBinding binding;
    internal string Destination { get; }
    internal DestinationBinding Binding => binding;

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
            foreach (string directory in new[] { PrimaryDirectory, ReplicaDirectory, CommitDirectory, PendingDirectory })
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

    internal void CreatePoint(string source, string generation, Action<CatalogStep>? checkpoint = null,
        Action<PendingStep>? pendingCheckpoint = null, Action<CopyStep, string>? objectCheckpoint = null)
    {
        _ = GenerationTime(generation);
        using var scope = workspace.OpenRegisteredRoot();
        workspace.CheckRoots(source, Destination);
        using var origin = new DirectoryFence(source);
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.ReadWrite);
        if (IsCommitted(store.Root, generation))
            throw new InvalidDataException("La generación ya está confirmada.");
        byte[] identity = WindowsProbeNative.DirectoryIdentity(origin.Root);
        if (identity.AsSpan().SequenceEqual(binding.Identity))
            throw new InvalidDataException("El origen y el almacén son la misma carpeta.");
        var plan = FolderBatch.AnalyzeSource(workspace.Root, source, Destination, binding, identity, BatchCatalog.VersionMaximumEntries);
        PendingBatch.ValidatePlan(plan, BatchCatalog.VersionMaximumEntries);
        FolderBatch.RequireSameInventory(plan);
        using var pending = WindowsProbeNative.OpenOrCreatePlainDirectory(store.Root, PendingDirectory);
        var files = IntentFiles(pending, generation);
        if (WindowsProbeNative.PlainFileExists(pending, files.Location))
            throw new InvalidDataException("Ya hay un plan para esa generación. Usa reintentar.");
        LabWorkspace.RequireSpace(0, LabWorkspace.AvailableSpace(store.Root));
        PendingBatch.SaveLocked(plan, scope.Root, files, pendingCheckpoint);
        CopyAndConfirm(store.Root, plan, generation, checkpoint, objectCheckpoint);
    }

    internal PendingVersion[] ListPending()
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.Read);
        using var pending = OpenPending(store.Root);
        if (pending is null)
            return [];
        return WindowsProbeNative.ReadDirectory(pending)
            .Where(entry => entry.Name.EndsWith(LocationSuffix, StringComparison.Ordinal))
            .Select(entry => entry.Name[..^LocationSuffix.Length])
            .OrderDescending(StringComparer.Ordinal)
            .Where(id => !IsCommitted(store.Root, id))
            .Select(id =>
            {
                var plan = ReadIntent(scope.Root, store.Root, pending, id);
                return new PendingVersion(id, plan.Source, plan.Files.Length);
            }).ToArray();
    }

    internal void ResumePoint(string generation, Action<CatalogStep>? checkpoint = null, byte[]? expectedSourceIdentity = null)
    {
        _ = GenerationTime(generation);
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.ReadWrite);
        bool committed = IsCommitted(store.Root, generation);
        if (committed && expectedSourceIdentity is null)
        {
            VerifyObjects(store.Root, ReadPoint(store.Root, generation).Files);
            return;
        }
        using var pending = WindowsProbeNative.OpenPlainDirectory(store.Root, PendingDirectory);
        var plan = ReadIntent(scope.Root, store.Root, pending, generation);
        if (expectedSourceIdentity is not null && !plan.SourceIdentity.AsSpan().SequenceEqual(expectedSourceIdentity))
            throw new InvalidDataException("El origen del plan pendiente no corresponde al trabajo.");
        if (committed)
        {
            VerifyObjects(store.Root, ReadPoint(store.Root, generation).Files);
            return;
        }
        CopyAndConfirm(store.Root, plan, generation, checkpoint, objectCheckpoint: null);
    }

    private BatchPlan ReadIntent(SafeFileHandle scope, SafeFileHandle store, SafeFileHandle pending, string generation)
    {
        var plan = PendingBatch.ReadLocked(workspace.Root, Destination, scope, store, IntentFiles(pending, generation));
        workspace.CheckRoots(plan.Source, Destination);
        if (plan.Target.Marker != binding.Marker || plan.SourceIdentity.AsSpan().SequenceEqual(binding.Identity))
            throw new InvalidDataException("La generación pendiente pertenece a otro almacén.");
        return plan;
    }

    private void CopyAndConfirm(SafeFileHandle store, BatchPlan plan, string generation, Action<CatalogStep>? checkpoint,
        Action<CopyStep, string>? objectCheckpoint)
    {
        DirectoryFence? origin = null;
        try
        {
            CopyObjects();
            if (origin is not null)
                FolderBatch.RequireSameInventory(plan);
            WithCatalogFiles(store, generation, files =>
            {
                BatchCatalog.PublishLocked(plan, files, generation, () => VerifyObjects(store, plan.Files), checkpoint);
                return true;
            });
        }
        finally { origin?.Dispose(); }

        void CopyObjects()
        {
            using var objects = WindowsProbeNative.OpenPlainDirectory(store, ObjectDirectory);
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
                    {
                        if (origin is null)
                        {
                            origin = new DirectoryFence(plan.Source);
                            if (!WindowsProbeNative.DirectoryIdentity(origin.Root).AsSpan().SequenceEqual(plan.SourceIdentity))
                                throw new InvalidDataException("La carpeta de origen cambió desde el análisis.");
                            FolderBatch.RequireSameInventory(plan);
                        }
                        string input = Path.Combine(plan.Source, file.RelativePath);
                        using var parent = new DirectoryFence(plan.Source, input);
                        using var observed = WindowsProbeNative.OpenPlainFile(parent.Parent(input), input, FileAccess.Read, FileShare.Read);
                        WindowsProbeNative.RequireSupportedData(observed.SafeFileHandle, file.RelativePath);
                        if (observed.Length != file.Length || !SHA256.HashData(observed).AsSpan().SequenceEqual(file.Hash))
                            throw new InvalidDataException($"El contenido de «{file.RelativePath}» cambió desde el análisis.");
                        VerifiedCopy.Run(input, final, temporary, objectCheckpoint,
                            expectedHash: file.Hash, recoveryRoot: workspace.Root);
                    }
                    else if (recovered is not (RecoveryResult.Published or RecoveryResult.AlreadyPublished))
                        throw new InvalidDataException($"El objeto {digest} requiere revisión. Estado {recovered}.");
                }
                VerifyObject(bucket, digest, file);
            }
        }
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
                try
                {
                    var catalog = ReadPoint(store.Root, id);
                    return new VersionPoint(id, date, catalog.Files.Length, catalog.Degraded);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                    ArgumentException or JsonException or Win32Exception)
                {
                    return new VersionPoint(id, date, 0, false, error.Message);
                }
            }).ToArray();
    }

    internal (CatalogSnapshot Current, CatalogSnapshot? Previous) ReadCatalogs(string generation, string? previous)
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.Read);
        return (ReadPoint(store.Root, generation), previous is null ? null : ReadPoint(store.Root, previous));
    }

    internal bool Verify(string generation)
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.Read);
        var catalog = ReadPoint(store.Root, generation);
        VerifyObjects(store.Root, catalog.Files);
        return catalog.Degraded;
    }

    internal CatalogSnapshot Restore(string generation, string output, string? relativeFile = null)
    {
        using var scope = workspace.OpenRegisteredRoot();
        workspace.CheckRoots(Destination, output);
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.Read);
        using var target = new DirectoryFence(output);
        LabWorkspace.RequireEmpty(target.Root);
        var catalog = ReadPoint(store.Root, generation);
        BatchFile[] files = relativeFile is null ? catalog.Files :
            [catalog.Files.SingleOrDefault(file => file.RelativePath == relativeFile)
                ?? throw new InvalidDataException("El archivo no pertenece a la generación seleccionada.")];
        var directories = relativeFile is null ? catalog.Directories : catalog.Directories.Where(directory =>
            relativeFile.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        VerifyObjects(store.Root, files);
        LabWorkspace.RequireSpace(files.Sum(file => file.Length), LabWorkspace.AvailableSpace(target.Root));
        DestinationBinding.Register(target.Root);
        foreach (string relative in directories)
            FolderBatch.WithDirectories(target.Root, relative, () => true);
        foreach (var file in files)
        {
            string final = Path.Combine(output, file.RelativePath);
            VerifiedCopy.Run(ObjectPath(Digest(file)), final, FolderBatch.TemporaryPath(output, file.RelativePath),
                expectedHash: file.Hash, recoveryRoot: workspace.Root, metadata: file.Metadata);
        }
        foreach (var file in files)
            BatchCatalog.VerifyFile(output, file);
        return catalog;
    }

    private static FileStream OpenLock(SafeFileHandle root, FileAccess access) =>
        WindowsProbeNative.OpenPlainFile(root, FolderBatch.LockName, access, FileShare.None);

    private static bool IsCommitted(SafeFileHandle store, string generation)
    {
        _ = GenerationTime(generation);
        using var commits = WindowsProbeNative.OpenPlainDirectory(store, CommitDirectory);
        return WindowsProbeNative.PlainFileExists(commits, generation + ".commit");
    }

    private static SafeFileHandle? OpenPending(SafeFileHandle store)
    {
        try { return WindowsProbeNative.OpenPlainDirectory(store, PendingDirectory); }
        catch (FileNotFoundException) { return null; }
    }

    private static PendingFiles IntentFiles(SafeFileHandle pending, string generation) =>
        new(pending, generation + ".jsonl", generation + LocationSuffix, generation);

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

    internal static DateTimeOffset GenerationTime(string id)
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
