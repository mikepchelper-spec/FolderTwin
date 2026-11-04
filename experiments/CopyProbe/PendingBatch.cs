using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal enum PendingStep { MetadataFlushed, MetadataPublished, LocationFlushed, LocationPublished }

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
        if (plan.SourceBinding is not null)
            throw new ArgumentException("Este descriptor solo admite trabajos de copia.");
        FolderBatch.ValidateRoots(plan.FixtureRoot, plan.Source, plan.Destination);
        FolderBatch.ValidateEntries(plan.Files, plan.Directories);
        if (plan.SourceIdentity is not { Length: 24 })
            throw new ArgumentException("Identidad de origen no válida.");
        byte[] metadataHash = BatchCatalog.WriteMetadata(Stream.Null, plan);
        using var fixture = new DirectoryFence(plan.FixtureRoot);
        using var root = new DirectoryFence(plan.Destination);
        using var marker = plan.Target.OpenVerifiedMarker(root.Root);
        using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.ReadWrite,
            FileShare.None, FileMode.OpenOrCreate);
        var location = new Location(1, WindowsProbeNative.DirectoryIdentity(fixture.Root),
            Path.GetRelativePath(plan.FixtureRoot, plan.Source), plan.SourceIdentity, metadataHash);
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(new Envelope(location,
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(location, Json))), Json);
        if (encoded.Length > MaximumLocationBytes)
            throw new InvalidDataException("El descriptor de ejecución supera el límite del laboratorio.");

        if (!WindowsProbeNative.PlainFileExists(root.Root, MetadataName))
        {
            string draft = MetadataName + ".tmp";
            if (!WindowsProbeNative.PlainFileExists(root.Root, draft))
            {
                using var output = WindowsProbeNative.OpenPlainFile(root.Root, draft, FileAccess.Write, FileShare.None, FileMode.CreateNew);
                if (!BatchCatalog.WriteMetadata(output, plan).AsSpan().SequenceEqual(metadataHash))
                    throw new InvalidDataException("El plan cambió al escribir el descriptor.");
                output.Flush(flushToDisk: true);
            }
            using var verified = WindowsProbeNative.OpenPlainFile(root.Root, draft, FileAccess.Read, FileShare.Read, rename: true);
            RequireMetadata(verified);
            checkpoint?.Invoke(PendingStep.MetadataFlushed);
            WindowsProbeNative.RenameNew(verified.SafeFileHandle, root.Root, MetadataName);
        }
        using (var existing = WindowsProbeNative.OpenPlainFile(root.Root, MetadataName, FileAccess.Read, FileShare.Read))
            RequireMetadata(existing);
        checkpoint?.Invoke(PendingStep.MetadataPublished);

        if (!WindowsProbeNative.PlainFileExists(root.Root, LocationName))
        {
            string draft = LocationName + ".tmp";
            if (!WindowsProbeNative.PlainFileExists(root.Root, draft))
            {
                using var output = WindowsProbeNative.OpenPlainFile(root.Root, draft, FileAccess.Write, FileShare.None, FileMode.CreateNew);
                output.Write(encoded);
                output.Flush(flushToDisk: true);
            }
            using var verified = WindowsProbeNative.OpenPlainFile(root.Root, draft, FileAccess.Read, FileShare.Read, rename: true);
            RequireLocation(verified);
            checkpoint?.Invoke(PendingStep.LocationFlushed);
            WindowsProbeNative.RenameNew(verified.SafeFileHandle, root.Root, LocationName);
        }
        using (var existing = WindowsProbeNative.OpenPlainFile(root.Root, LocationName, FileAccess.Read, FileShare.Read))
            RequireLocation(existing);
        checkpoint?.Invoke(PendingStep.LocationPublished);

        void RequireMetadata(Stream file)
        {
            if (!BatchCatalog.Parse(file).DocumentHash.AsSpan().SequenceEqual(metadataHash))
                throw new InvalidDataException("Ya existe un plan pendiente diferente o dañado.");
        }
        void RequireLocation(Stream file)
        {
            byte[] bytes = ReadLocationBytes(file);
            if (!bytes.AsSpan().SequenceEqual(encoded))
                throw new InvalidDataException("El descriptor pendiente es diferente o está incompleto.");
        }
    }

    internal static BatchPlan Read(string fixtureRoot, string destination)
    {
        using var fixture = new DirectoryFence(fixtureRoot, Path.Combine(destination, "pending-scope-check"));
        using var root = new DirectoryFence(destination);
        using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.Read, FileShare.None);
        Envelope envelope;
        using (var file = WindowsProbeNative.OpenPlainFile(root.Root, LocationName, FileAccess.Read, FileShare.Read))
            envelope = JsonSerializer.Deserialize<Envelope>(ReadLocationBytes(file), Json)
                ?? throw new InvalidDataException("Falta el descriptor de ejecución.");
        var location = envelope.Payload;
        if (location.Version != 1 || location.FixtureIdentity is not { Length: 24 } || location.SourceIdentity is not { Length: 24 } ||
            location.MetadataHash is not { Length: 32 } || envelope.Checksum is not { Length: 32 } ||
            !SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(location, Json)).AsSpan().SequenceEqual(envelope.Checksum) ||
            !WindowsProbeNative.DirectoryIdentity(fixture.Root).AsSpan().SequenceEqual(location.FixtureIdentity))
            throw new InvalidDataException("Descriptor de ejecución dañado o de otro laboratorio.");
        _ = new UTF8Encoding(false, true).GetByteCount(location.SourceRelative);
        if (Path.IsPathRooted(location.SourceRelative))
            throw new InvalidDataException("El origen del descriptor no puede ser una ruta absoluta.");
        string source = Path.Combine(fixtureRoot, location.SourceRelative);
        FolderBatch.ValidateRoots(fixtureRoot, source, destination);
        CatalogSnapshot metadata;
        using (var file = WindowsProbeNative.OpenPlainFile(root.Root, MetadataName, FileAccess.Read, FileShare.Read))
            metadata = BatchCatalog.Parse(file);
        if (!metadata.DocumentHash.AsSpan().SequenceEqual(location.MetadataHash))
            throw new InvalidDataException("El descriptor y la lista de archivos no coinciden.");
        var target = new DestinationBinding { Version = 1, Marker = metadata.Binding.Marker, Identity = metadata.Binding.Identity,
            LastVolumePath = WindowsProbeNative.VolumePath(root.Root) };
        using var marker = target.OpenVerifiedMarker(root.Root);
        return new(fixtureRoot, source, destination, location.SourceIdentity, target, metadata.Directories, metadata.Files);
    }

    internal static BatchResult Resume(string fixtureRoot, string destination, CancellationToken cancellationToken = default,
        Action<string, CopyStep, string>? checkpoint = null, Action<BatchPlan>? validate = null)
    {
        BatchResult? result = null;
        try
        {
            var plan = Read(fixtureRoot, destination);
            validate?.Invoke(plan);
            result = FolderBatch.Run(plan, cancellationToken, checkpoint, resume: true);
            if (result.Complete)
                BatchCatalog.Publish(plan);
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or Win32Exception or UnauthorizedAccessException)
        {
            return new(result?.Entries ?? [], "El lote pendiente necesita revisión. " + error.Message);
        }
    }

    internal static BatchResult Start(BatchPlan plan)
    {
        try
        {
            Save(plan);
            return Resume(plan.FixtureRoot, plan.Destination);
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
