using System.Security.Cryptography;

internal sealed record CloudPoint(Guid Job, string Generation, long Bytes, int Files, int Directories);
internal sealed class CloudLocalDestinationUnavailableException(Exception inner) :
    IOException("El disco del respaldo local no está disponible.", inner);

internal sealed partial class VersionStore
{
    internal static CloudPoint ExportConfirmedPoint(ProductJobRegistry registry, Guid jobId, string generation,
        string output, CancellationToken cancellationToken = default)
    {
        if (IsPartialPoint(generation) || !registry.RunHistory(jobId).Runs.Any(run =>
            run.Intent.Operation == ProductRunOperation.Backup && run.Result is
            { State: ProductRunState.Confirmed, Generation: var point } && point == generation))
            throw new InvalidDataException("Solo se pueden enviar puntos locales confirmados.");

        var job = registry.Get(jobId);
        try { using var root = new VersionScope(job).OpenRegisteredRoot(); }
        catch (Exception error) when (VersionJob.Unavailable(error)) { throw new CloudLocalDestinationUnavailableException(error); }
        return OpenProduct(job).ExportCloudStage(jobId, generation, output, cancellationToken);
    }

    private CloudPoint ExportCloudStage(Guid job, string generation, string output, CancellationToken token)
    {
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
        LabWorkspace.RequireSpace(bytes, LabWorkspace.AvailableSpace(target.Root));
        using var objects = WindowsProbeNative.OpenPlainDirectory(store.Root, ObjectDirectory);
        using var algorithm = WindowsProbeNative.OpenPlainDirectory(objects, Algorithm);
        byte[] buffer = new byte[64 * 1024];
        foreach (string directory in catalog.Directories)
        {
            token.ThrowIfCancellationRequested();
            FolderBatch.WithDirectories(target.Root, directory, () => true);
        }
        foreach (var group in catalog.Files.GroupBy(file => Path.GetDirectoryName(file.RelativePath) ?? "", StringComparer.Ordinal))
            FolderBatch.WithDirectories(target.Root, group.Key, parent =>
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
        return new(job, generation, bytes, catalog.Files.Length, catalog.Directories.Length);
    }
}
