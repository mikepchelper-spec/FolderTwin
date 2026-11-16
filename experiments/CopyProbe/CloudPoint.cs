using System.Security.Cryptography;
using System.Text.Json.Serialization;

internal sealed record CloudPoint(Guid Job, string Generation, long Bytes, int Files, int Directories,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Layout = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CatalogHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long CatalogBytes = 0);
internal sealed class CloudLocalDestinationUnavailableException(Exception inner) :
    IOException("El disco del respaldo local no está disponible.", inner);

internal sealed partial class VersionStore
{
    internal static CloudPoint ExportConfirmedPoint(ProductJobRegistry registry, Guid jobId, string generation,
        string output, CancellationToken cancellationToken = default, int layout = 2)
    {
        if (IsPartialPoint(generation) || !registry.RunHistory(jobId).Runs.Any(run =>
            run.Intent.Operation == ProductRunOperation.Backup && run.Result is
            { State: ProductRunState.Confirmed, Generation: var point } && point == generation))
            throw new InvalidDataException("Solo se pueden enviar puntos locales confirmados.");

        var job = registry.Get(jobId);
        try { using var root = new VersionScope(job).OpenRegisteredRoot(); }
        catch (Exception error) when (VersionJob.Unavailable(error)) { throw new CloudLocalDestinationUnavailableException(error); }
        return OpenProduct(job).ExportCloudStage(jobId, generation, output, cancellationToken, layout);
    }

    private CloudPoint ExportCloudStage(Guid job, string generation, string output, CancellationToken token, int layout)
    {
        if (layout is not (0 or 2)) throw new InvalidDataException("El formato de envío remoto no está admitido.");
        token.ThrowIfCancellationRequested();
        using var scope = workspace.OpenRegisteredRoot();
        workspace.CheckRoots(Destination, output);
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.Read);
        using var target = new DirectoryFence(output);
        LabWorkspace.RequireEmpty(target.Root);
        var catalog = ReadPoint(store.Root, generation);
        var ledger = GenerationRegistry.Read(store.Root, binding);
        if (!ledger.Generations.TryGetValue(generation, out byte[]? registered) ||
            !registered.AsSpan().SequenceEqual(catalog.DocumentHash) || ledger.Expired.Contains(generation) ||
            ledger.Abandoned.ContainsKey(generation) || ledger.PendingMirrorGeneration == generation)
            throw new InvalidDataException("El punto cambió de estado y requiere revisión antes de enviarlo.");

        long bytes = catalog.Files.Sum(file => file.Length);
        if (layout == 0 && catalog.Files.Any(file => file.Metadata?.ZoneData is not null))
            throw new InvalidDataException("El formato remoto anterior no puede conservar Zone.Identifier. Requiere un envío con catálogo.");
        using var catalogInput = layout == 2 ? OpenCloudCatalog(store.Root, generation, catalog.DocumentHash) : null;
        LabWorkspace.RequireSpace(checked(bytes + (catalogInput?.Length ?? 0) +
            catalog.Files.Sum(file => (long)(file.Metadata?.ZoneData?.Length ?? 0))), LabWorkspace.AvailableSpace(target.Root));
        using var data = layout == 2 ? WindowsProbeNative.CreatePlainDirectory(target.Root, CloudCatalog.DataDirectory) : null;
        var payloadRoot = data ?? target.Root;
        using var objects = WindowsProbeNative.OpenPlainDirectory(store.Root, ObjectDirectory);
        using var algorithm = WindowsProbeNative.OpenPlainDirectory(objects, Algorithm);
        byte[] buffer = new byte[64 * 1024];
        foreach (string directory in catalog.Directories)
        {
            token.ThrowIfCancellationRequested();
            FolderBatch.WithDirectories(payloadRoot, directory, () => true);
        }
        foreach (var group in catalog.Files.GroupBy(file => Path.GetDirectoryName(file.RelativePath) ?? "", StringComparer.Ordinal))
            FolderBatch.WithDirectories(payloadRoot, group.Key, parent =>
            {
                foreach (var file in group)
                {
                    token.ThrowIfCancellationRequested();
                    string digest = Digest(file);
                    using var bucket = WindowsProbeNative.OpenPlainDirectory(algorithm, digest[..2]);
                    using var input = WindowsProbeNative.OpenPlainFile(bucket, digest, FileAccess.Read, FileShare.Read);
                    WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, digest);
                    if (input.Length != file.Length) throw new InvalidDataException("Un objeto no coincide con su tamaño registrado.");
                    using var staged = WindowsProbeNative.OpenPlainFile(parent, file.RelativePath, FileAccess.ReadWrite,
                        FileShare.None, FileMode.CreateNew);
                    int count;
                    while ((count = input.Read(buffer)) != 0)
                    {
                        token.ThrowIfCancellationRequested();
                        staged.Write(buffer.AsSpan(0, count));
                    }
                    WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, digest);
                    // Staging is rebuilt after interruption. Only a complete
                    // tree can reach Kopia, so it needs no per-file journal.
                    staged.Flush();
                    file.Metadata?.Apply(staged.SafeFileHandle);
                    staged.Position = 0;
                    if (staged.Length != file.Length || !SHA256.HashData(staged).AsSpan().SequenceEqual(file.Hash))
                        throw new InvalidDataException("La preparación no coincide con el SHA-256 del punto local.");
                    file.Metadata?.RequireMatch(staged.SafeFileHandle);
                }
                return true;
            });
        token.ThrowIfCancellationRequested();
        if (layout == 0) return new(job, generation, bytes, catalog.Files.Length, catalog.Directories.Length);
        using var document = WindowsProbeNative.OpenPlainFile(target.Root, CloudCatalog.Name, FileAccess.ReadWrite, FileShare.None, FileMode.CreateNew);
        catalogInput!.CopyTo(document);
        document.Flush(flushToDisk: true);
        document.Position = 0;
        if (!BatchCatalog.MatchesDocument(document, generation, catalog.DocumentHash))
            throw new InvalidDataException("El catálogo remoto preparado no coincide con el punto confirmado.");
        return new(job, generation, checked(bytes + document.Length), checked(catalog.Files.Length + 1), checked(catalog.Directories.Length + 1),
            layout, Convert.ToHexStringLower(catalog.DocumentHash), document.Length);
    }

    private static FileStream OpenCloudCatalog(Microsoft.Win32.SafeHandles.SafeFileHandle root, string generation, byte[] hash)
    {
        foreach (string area in new[] { PrimaryDirectory, ReplicaDirectory })
        {
            FileStream? input = null;
            try
            {
                using var directory = WindowsProbeNative.OpenPlainDirectory(root, area);
                input = WindowsProbeNative.OpenPlainFile(directory, generation + ".jsonl", FileAccess.Read, FileShare.Read);
                WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, "catálogo remoto");
                if (BatchCatalog.MatchesDocument(input, generation, hash)) { input.Position = 0; return input; }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
            catch { input?.Dispose(); throw; }
            input?.Dispose();
        }
        throw new InvalidDataException("No hay un catálogo íntegro para el envío remoto.");
    }
}
