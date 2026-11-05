using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal enum PendingStep { MetadataFlushed, MetadataPublished, LocationFlushed, LocationPublished }
internal readonly record struct PendingFiles(SafeFileHandle Parent, string Metadata, string Location, string Generation = "initial");

// Immutable execution intent. Progress is reconstructed from the per-file receipts,
// not from persisted success flags. These files never confirm a recovery point.
internal static class PendingBatch
{
    internal const string MetadataName = ".foldertwin-pending.jsonl";
    internal const string LocationName = ".foldertwin-pending-location.json";
    private const int MaximumLocationBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 16
    };
    private sealed record Location(int Version, byte[] FixtureIdentity, string SourceRelative, byte[] SourceIdentity, byte[] MetadataHash);
    private sealed record Envelope(Location Payload, byte[] Checksum);

    internal static void Save(BatchPlan plan, Action<PendingStep>? checkpoint = null)
    {
        ValidatePlan(plan);
        using var fixture = new DirectoryFence(plan.FixtureRoot);
        using var root = new DirectoryFence(plan.Destination);
        using var marker = plan.Target.OpenVerifiedMarker(root.Root);
        using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.ReadWrite,
            FileShare.None, FileMode.OpenOrCreate);
        SaveLocked(plan, fixture.Root, new(root.Root, MetadataName, LocationName), checkpoint);
    }

    internal static void SaveLocked(BatchPlan plan, SafeFileHandle fixture, PendingFiles files,
        Action<PendingStep>? checkpoint = null)
    {
        var location = new Location(1, WindowsProbeNative.DirectoryIdentity(fixture),
            Path.GetRelativePath(plan.FixtureRoot, plan.Source), plan.SourceIdentity,
            BatchCatalog.WriteMetadata(Stream.Null, plan, files.Generation));
        SaveLocked(plan, files, location, checkpoint);
    }

    internal static void SaveProductLocked(BatchPlan plan, SafeFileHandle destination, PendingFiles files,
        Action<PendingStep>? checkpoint = null)
    {
        var location = new Location(2, WindowsProbeNative.DirectoryIdentity(destination), plan.Source,
            plan.SourceIdentity, BatchCatalog.WriteMetadata(Stream.Null, plan, files.Generation));
        SaveLocked(plan, files, location, checkpoint);
    }

    private static void SaveLocked(BatchPlan plan, PendingFiles files, Location location, Action<PendingStep>? checkpoint)
    {
        byte[] metadataHash = location.MetadataHash;
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(new Envelope(location,
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(location, Json))), Json);
        if (encoded.Length > MaximumLocationBytes)
            throw new InvalidDataException("El descriptor de ejecución supera el límite del laboratorio.");

        if (!WindowsProbeNative.PlainFileExists(files.Parent, files.Metadata))
        {
            string draft = files.Metadata + ".tmp";
            if (!WindowsProbeNative.PlainFileExists(files.Parent, draft))
            {
                using var output = WindowsProbeNative.OpenPlainFile(files.Parent, draft, FileAccess.Write, FileShare.None, FileMode.CreateNew);
                if (!BatchCatalog.WriteMetadata(output, plan, files.Generation).AsSpan().SequenceEqual(metadataHash))
                    throw new InvalidDataException("El plan cambió al escribir el descriptor.");
                output.Flush(flushToDisk: true);
            }
            using var verified = WindowsProbeNative.OpenPlainFile(files.Parent, draft, FileAccess.Read, FileShare.Read, rename: true);
            RequireMetadata(verified);
            checkpoint?.Invoke(PendingStep.MetadataFlushed);
            WindowsProbeNative.RenameNew(verified.SafeFileHandle, files.Parent, files.Metadata);
        }
        using (var existing = WindowsProbeNative.OpenPlainFile(files.Parent, files.Metadata, FileAccess.Read, FileShare.Read))
            RequireMetadata(existing);
        checkpoint?.Invoke(PendingStep.MetadataPublished);

        if (!WindowsProbeNative.PlainFileExists(files.Parent, files.Location))
        {
            string draft = files.Location + ".tmp";
            if (!WindowsProbeNative.PlainFileExists(files.Parent, draft))
            {
                using var output = WindowsProbeNative.OpenPlainFile(files.Parent, draft, FileAccess.Write, FileShare.None, FileMode.CreateNew);
                output.Write(encoded);
                output.Flush(flushToDisk: true);
            }
            using var verified = WindowsProbeNative.OpenPlainFile(files.Parent, draft, FileAccess.Read, FileShare.Read, rename: true);
            RequireLocation(verified);
            checkpoint?.Invoke(PendingStep.LocationFlushed);
            WindowsProbeNative.RenameNew(verified.SafeFileHandle, files.Parent, files.Location);
        }
        using (var existing = WindowsProbeNative.OpenPlainFile(files.Parent, files.Location, FileAccess.Read, FileShare.Read))
            RequireLocation(existing);
        checkpoint?.Invoke(PendingStep.LocationPublished);

        void RequireMetadata(Stream file)
        {
            if (!BatchCatalog.Parse(file, files.Generation).DocumentHash.AsSpan().SequenceEqual(metadataHash))
                throw new InvalidDataException("Ya existe un plan pendiente diferente o dañado.");
        }
        void RequireLocation(Stream file)
        {
            byte[] bytes = ReadLocationBytes(file);
            if (!bytes.AsSpan().SequenceEqual(encoded))
                throw new InvalidDataException("El descriptor pendiente es diferente o está incompleto.");
        }
    }

    internal static void ValidatePlan(BatchPlan plan, int maximumEntries = FolderInventory.DefaultMaximumEntries)
    {
        if (plan.SourceBinding is not null)
            throw new ArgumentException("Este descriptor solo admite trabajos de copia.");
        FolderBatch.ValidateRoots(plan.FixtureRoot, plan.Source, plan.Destination);
        FolderBatch.ValidateEntries(plan.Files, plan.Directories, maximumEntries);
        if (plan.SourceIdentity is not { Length: 24 })
            throw new ArgumentException("Identidad de origen no válida.");
    }

    internal static BatchPlan Read(string fixtureRoot, string destination)
    {
        using var fixture = new DirectoryFence(fixtureRoot, Path.Combine(destination, "pending-scope-check"));
        using var root = new DirectoryFence(destination);
        using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.Read, FileShare.None);
        return ReadLocked(fixtureRoot, destination, fixture.Root, root.Root, new(root.Root, MetadataName, LocationName));
    }

    internal static BatchPlan ReadLocked(string fixtureRoot, string destination, SafeFileHandle fixture, SafeFileHandle root,
        PendingFiles files)
    {
        var (location, source) = ReadLocation(fixtureRoot, destination, fixture, files);
        CatalogSnapshot metadata;
        using (var file = WindowsProbeNative.OpenPlainFile(files.Parent, files.Metadata, FileAccess.Read, FileShare.Read))
            metadata = BatchCatalog.Parse(file, files.Generation);
        if (!metadata.DocumentHash.AsSpan().SequenceEqual(location.MetadataHash))
            throw new InvalidDataException("El descriptor y la lista de archivos no coinciden.");
        var target = new DestinationBinding { Version = 1, Marker = metadata.Binding.Marker, Identity = metadata.Binding.Identity,
            LastVolumePath = WindowsProbeNative.VolumePath(root) };
        using var marker = target.OpenVerifiedMarker(root);
        return new(fixtureRoot, source, destination, location.SourceIdentity, target, metadata.Directories, metadata.Files);
    }

    internal static (string Source, byte[] Identity) ReadRegisteredSource(string fixtureRoot, string destination, SafeFileHandle fixture,
        PendingFiles files, byte[] documentHash)
    {
        var (location, source) = ReadLocation(fixtureRoot, destination, fixture, files);
        using var metadata = WindowsProbeNative.OpenPlainFile(files.Parent, files.Metadata, FileAccess.Read, FileShare.Read);
        if (!location.MetadataHash.AsSpan().SequenceEqual(documentHash) || metadata.Length is < 1 or > BatchCatalog.VersionMaximumBytes ||
            !SHA256.HashData(metadata).AsSpan().SequenceEqual(documentHash))
            throw new InvalidDataException("El descriptor de origen no coincide con el catálogo esperado.");
        return (source, location.SourceIdentity);
    }

    internal static BatchPlan ReadProductLocked(ProductJob job, SafeFileHandle scope, SafeFileHandle root, PendingFiles files)
    {
        var location = ReadProductLocation(job, scope, files);
        using var file = WindowsProbeNative.OpenPlainFile(files.Parent, files.Metadata, FileAccess.Read, FileShare.Read);
        var metadata = BatchCatalog.Parse(file, files.Generation);
        if (!metadata.DocumentHash.AsSpan().SequenceEqual(location.MetadataHash) || metadata.Binding.Marker != job.Destination.Marker ||
            !metadata.Binding.Identity.AsSpan().SequenceEqual(job.Destination.Identity))
            throw new InvalidDataException("El catálogo pendiente no corresponde al trabajo.");
        using var marker = job.Destination.OpenVerifiedMarker(root);
        return new(job.Destination.LastVolumePath, job.Source.VolumePath, job.Destination.LastVolumePath,
            location.SourceIdentity, job.Destination, metadata.Directories, metadata.Files);
    }

    internal static (string Source, byte[] Identity) ReadProductRegisteredSource(ProductJob job, SafeFileHandle scope,
        PendingFiles files, byte[] documentHash)
    {
        var location = ReadProductLocation(job, scope, files);
        using var file = WindowsProbeNative.OpenPlainFile(files.Parent, files.Metadata, FileAccess.Read, FileShare.Read);
        if (!location.MetadataHash.AsSpan().SequenceEqual(documentHash) || file.Length is < 1 or > BatchCatalog.VersionMaximumBytes ||
            !SHA256.HashData(file).AsSpan().SequenceEqual(documentHash))
            throw new InvalidDataException("El descriptor de origen no coincide con el catálogo esperado.");
        return (job.Source.VolumePath, location.SourceIdentity);
    }

    private static Location ReadProductLocation(ProductJob job, SafeFileHandle scope, PendingFiles files)
    {
        var location = ReadEnvelope(files);
        if (location.Version != 2 || !WindowsProbeNative.DirectoryIdentity(scope).AsSpan().SequenceEqual(location.FixtureIdentity) ||
            !location.FixtureIdentity.AsSpan().SequenceEqual(job.Destination.Identity) ||
            !location.SourceIdentity.AsSpan().SequenceEqual(job.Source.Identity) ||
            !location.SourceRelative.Equals(job.Source.VolumePath, StringComparison.OrdinalIgnoreCase) &&
            !(job.SourceAliases?.Contains(location.SourceRelative, StringComparer.OrdinalIgnoreCase) ?? false))
            throw new InvalidDataException("El descriptor pendiente no corresponde al origen y al destino del trabajo.");
        return location;
    }

    private static (Location Location, string Source) ReadLocation(string fixtureRoot, string destination, SafeFileHandle fixture,
        PendingFiles files)
    {
        var location = ReadEnvelope(files);
        if (location.Version != 1 || !WindowsProbeNative.DirectoryIdentity(fixture).AsSpan().SequenceEqual(location.FixtureIdentity))
            throw new InvalidDataException("Descriptor de ejecución dañado o de otro laboratorio.");
        _ = new UTF8Encoding(false, true).GetByteCount(location.SourceRelative);
        if (Path.IsPathRooted(location.SourceRelative))
            throw new InvalidDataException("El origen del descriptor no puede ser una ruta absoluta.");
        string source = Path.Combine(fixtureRoot, location.SourceRelative);
        FolderBatch.ValidateRoots(fixtureRoot, source, destination);
        return (location, source);
    }

    private static Location ReadEnvelope(PendingFiles files)
    {
        Envelope envelope;
        using (var file = WindowsProbeNative.OpenPlainFile(files.Parent, files.Location, FileAccess.Read, FileShare.Read))
            envelope = JsonSerializer.Deserialize<Envelope>(ReadLocationBytes(file), Json)
                ?? throw new InvalidDataException("Falta el descriptor de ejecución.");
        var location = envelope.Payload;
        if (location.FixtureIdentity is not { Length: 24 } || location.SourceIdentity is not { Length: 24 } ||
            location.MetadataHash is not { Length: 32 } || envelope.Checksum is not { Length: 32 } ||
            !SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(location, Json)).AsSpan().SequenceEqual(envelope.Checksum))
            throw new InvalidDataException("Descriptor de ejecución dañado o de otro laboratorio.");
        return location;
    }

    internal static BatchResult Resume(string fixtureRoot, string destination, CancellationToken cancellationToken = default,
        Action<string, CopyStep, string>? checkpoint = null, Action<BatchPlan>? validate = null, bool finishCurrentFileOnCancel = false,
        IProgress<BatchProgress>? progress = null, Action<BatchProgress>? waitBetweenFiles = null)
    {
        BatchResult? result = null;
        try
        {
            var plan = Read(fixtureRoot, destination);
            validate?.Invoke(plan);
            result = FolderBatch.Run(plan, cancellationToken, checkpoint, resume: true, finishCurrentFileOnCancel: finishCurrentFileOnCancel,
                progress: progress, waitBetweenFiles: waitBetweenFiles);
            if (result.Complete)
            {
                if (cancellationToken.IsCancellationRequested)
                    return result with { Cancelled = true };
                BatchCatalog.Publish(plan);
            }
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or Win32Exception or UnauthorizedAccessException)
        {
            return new(result?.Entries ?? [], "El lote pendiente necesita revisión. " + error.Message);
        }
    }

    internal static BatchResult Start(BatchPlan plan, CancellationToken cancellationToken = default,
        Action<string, CopyStep, string>? checkpoint = null, IProgress<BatchProgress>? progress = null,
        Action<BatchProgress>? waitBetweenFiles = null)
    {
        try
        {
            Save(plan);
            return Resume(plan.FixtureRoot, plan.Destination, cancellationToken, checkpoint, finishCurrentFileOnCancel: true,
                progress: progress, waitBetweenFiles: waitBetweenFiles);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or Win32Exception or UnauthorizedAccessException)
        {
            return new([], "El plan pendiente no pudo guardarse. " + error.Message);
        }
    }

    private static byte[] ReadLocationBytes(Stream file)
    {
        if (file.Length is < 1 or > MaximumLocationBytes)
            throw new InvalidDataException("Tamaño de descriptor de ejecución no válido.");
        byte[] bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }
}
