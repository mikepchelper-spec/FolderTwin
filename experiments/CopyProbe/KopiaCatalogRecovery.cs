using System.Security.Cryptography;

// The catalog is part of the encrypted snapshot. Kopia's Windows adapter does
// not carry ADS; the verified catalog carries per-path Zone.Identifier bytes.
internal static class CloudCatalog
{
    internal const string Name = ".foldertwin-remote-catalog.jsonl";
    internal const string DataDirectory = "data";
    internal const string RecoveryDirectory = ".foldertwin-cloud-staging";
}

internal sealed record CloudRecoverySelection(string RelativePath, bool IsDirectory);

internal sealed partial class KopiaRepository
{
    internal async Task<CatalogSnapshot> ReadCatalogAsync(KopiaPoint point, string emptyOutput, CancellationToken token)
    {
        if (point.Layout != 2 || point.Generation is null || !RetentionJournal.IsDigest(point.CatalogHash ?? "") ||
            point.CatalogBytes is < 1 or > BatchCatalog.VersionMaximumBytes)
            throw new InvalidDataException("Esta fecha no contiene un catálogo remoto compatible.");
        using var directory = new DirectoryFence(emptyOutput);
        LabWorkspace.RequireEmpty(directory.Root);
        LabWorkspace.RequireSpace(point.CatalogBytes, LabWorkspace.AvailableSpace(directory.Root));
        string path = Path.Combine(WindowsProbeNative.VolumePath(directory.Root), CloudCatalog.Name);
        await DownloadAsync(Identifier(point.RootId, "k") + "/" + CloudCatalog.Name, path, token);
        using var document = WindowsProbeNative.OpenPlainFile(directory.Root, CloudCatalog.Name, FileAccess.Read, FileShare.Read);
        WindowsProbeNative.RequireSupportedData(document.SafeFileHandle, CloudCatalog.Name);
        if (document.Length != point.CatalogBytes ||
            !BatchCatalog.MatchesDocument(document, point.Generation, Convert.FromHexString(point.CatalogHash!)))
            throw new InvalidDataException("El catálogo descargado no coincide con la fecha remota. Se conserva para revisión.");
        document.Position = 0;
        var catalog = BatchCatalog.Parse(document, point.Generation);
        if (catalog.Files.Length + 1 != point.Files || catalog.Directories.Length + 2 != point.Directories ||
            checked(catalog.Files.Sum(file => file.Length) + document.Length) != point.Bytes)
            throw new InvalidDataException("El catálogo y el inventario remoto son contradictorios.");
        return catalog;
    }

    private Task DownloadAsync(string remotePath, string output, CancellationToken token) => RunAsync(token,
        "snapshot", "restore", remotePath, output, "--no-overwrite-files", "--no-overwrite-directories",
        "--no-overwrite-symlinks", "--no-ignore-permission-errors", "--write-files-atomically", "--flush-files");

    private async Task RestoreCatalogPointAsync(KopiaPoint point, string emptyOutput, CancellationToken token,
        Action<WorkProgress>? observe = null, CloudRecoverySelection? selection = null)
    {
        using var output = new DirectoryFence(emptyOutput);
        LabWorkspace.RequireEmpty(output.Root);
        string root = WindowsProbeNative.VolumePath(output.Root);
        using (var hidden = WindowsProbeNative.CreatePlainDirectory(output.Root, CloudCatalog.RecoveryDirectory, writeAttributes: true))
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        string staging = Path.Combine(root, CloudCatalog.RecoveryDirectory);
        using var stage = new DirectoryFence(staging);
        observe?.Invoke(new(WorkPhase.Preparing, 0, 0));
        var catalog = await ReadCatalogAsync(point, staging, token);
        var files = catalog.Files;
        var directories = catalog.Directories;
        string remote = Identifier(point.RootId, "k") + "/" + CloudCatalog.DataDirectory;
        string local = Path.Combine(staging, CloudCatalog.DataDirectory);
        if (selection is not null)
        {
            string path = selection.RelativePath;
            if (selection.IsDirectory ? !catalog.Directories.Contains(path, StringComparer.Ordinal) :
                !catalog.Files.Any(file => file.RelativePath == path))
                throw new InvalidDataException("El archivo o carpeta elegidos no pertenecen a la fecha remota.");
            bool Selected(string candidate) => candidate == path || selection.IsDirectory && candidate.StartsWith(path + "\\", StringComparison.Ordinal);
            files = files.Where(file => Selected(file.RelativePath)).ToArray();
            // Include validated ancestors to preserve the original hierarchy.
            directories = directories.Where(directory => Selected(directory) || path.StartsWith(directory + "\\", StringComparison.Ordinal)).ToArray();
            using (WindowsProbeNative.CreatePlainDirectory(stage.Root, CloudCatalog.DataDirectory)) { }
            using (var data = WindowsProbeNative.OpenPlainDirectory(stage.Root, CloudCatalog.DataDirectory))
                FolderBatch.WithDirectories(data, Path.GetDirectoryName(path) ?? "", () => true);
            remote += "/" + path.Replace('\\', '/');
            local = Path.Combine(local, path);
        }
        LabWorkspace.RequireSpace(checked(files.Sum(file => file.Length) + files.Sum(file => (long)(file.Metadata?.ZoneData?.Length ?? 0))),
            LabWorkspace.AvailableSpace(output.Root));
        observe?.Invoke(new(WorkPhase.Restoring, 0, files.Length, selection?.RelativePath));
        await DownloadAsync(remote, local, token);
        string tree = Path.Combine(staging, CloudCatalog.DataDirectory);
        var inventory = FolderInventory.Scan(tree, BatchCatalog.VersionMaximumEntries, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (!inventory.Complete ||
            !inventory.Entries.Where(entry => entry.IsDirectory).Select(entry => entry.RelativePath).Order(StringComparer.Ordinal)
                .SequenceEqual(directories.Order(StringComparer.Ordinal)) ||
            !inventory.Entries.Where(entry => !entry.IsDirectory).Select(entry => (entry.RelativePath, entry.Length)).OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .SequenceEqual(files.Select(file => (file.RelativePath, file.Length)).OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)))
            throw new InvalidDataException("Los datos recuperados difieren del catálogo remoto. Se conservan para revisión.");
        using (var source = new DirectoryFence(tree))
            WindowsProbeNative.RequireSupportedData(source.Root, "carpeta recuperada");
        foreach (string directory in directories)
        {
            token.ThrowIfCancellationRequested();
            using var child = new DirectoryFence(Path.Combine(tree, directory));
            WindowsProbeNative.RequireSupportedData(child.Root, directory);
        }
        // Check every downloaded file before exposing any under the final paths.
        byte[] buffer = new byte[64 * 1024];
        for (int index = 0; index < files.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var file = files[index];
            observe?.Invoke(new(WorkPhase.Verifying, index, files.Length, file.RelativePath));
            string path = Path.Combine(tree, file.RelativePath);
            using var fence = new DirectoryFence(tree, path);
            using var input = WindowsProbeNative.OpenPlainFile(fence.Parent(path), path, FileAccess.ReadWrite, FileShare.None);
            RequireData(input, file, allowZone: false, buffer, token);
            file.Metadata?.Apply(input.SafeFileHandle);
            input.Flush(flushToDisk: true);
            file.Metadata?.RequireMatch(input.SafeFileHandle);
        }
        foreach (string directory in directories)
        {
            token.ThrowIfCancellationRequested();
            FolderBatch.WithDirectories(output.Root, directory, () => true);
        }
        for (int index = 0; index < files.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var file = files[index];
            observe?.Invoke(new(WorkPhase.Publishing, index, files.Length, file.RelativePath));
            string path = Path.Combine(tree, file.RelativePath);
            using var fence = new DirectoryFence(tree, path);
            using var input = WindowsProbeNative.OpenPlainFile(fence.Parent(path), path, FileAccess.Read, FileShare.Read,
                rename: true, writeAttributes: file.Metadata?.CreationUtc is not null);
            RequireData(input, file, file.Metadata?.ZoneData is not null, buffer, token);
            file.Metadata?.RequireMatch(input.SafeFileHandle);
            FolderBatch.WithDirectories(output.Root, Path.GetDirectoryName(file.RelativePath) ?? "", parent =>
            {
                CopyRecovery.PublishData(input, parent, file.RelativePath, file.Metadata);
                return true;
            });
        }
        foreach (string directory in directories.OrderByDescending(path => path.Count(character => character == '\\')))
        {
            string path = Path.Combine(tree, directory);
            using var parent = new DirectoryFence(Path.GetDirectoryName(path)!);
            using var child = WindowsProbeNative.OpenDirectoryForDeletion(parent.Root, Path.GetFileName(path));
            WindowsProbeNative.DeleteEmptyDirectory(child);
        }
        using (var child = WindowsProbeNative.OpenDirectoryForDeletion(stage.Root, CloudCatalog.DataDirectory))
            WindowsProbeNative.DeleteEmptyDirectory(child);
        using (var document = WindowsProbeNative.OpenPlainFile(stage.Root, CloudCatalog.Name, FileAccess.Read, FileShare.None, rename: true))
        {
            WindowsProbeNative.RequireSupportedData(document.SafeFileHandle, CloudCatalog.Name);
            WindowsProbeNative.DeletePlainFile(document.SafeFileHandle);
        }
        stage.Dispose();
        using (var child = WindowsProbeNative.OpenDirectoryForDeletion(output.Root, CloudCatalog.RecoveryDirectory))
            WindowsProbeNative.DeleteEmptyDirectory(child);
        observe?.Invoke(new(WorkPhase.Restoring, files.Length, files.Length));
    }

    private static void RequireData(FileStream input, BatchFile file, bool allowZone, byte[] buffer, CancellationToken token)
    {
        WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, file.RelativePath, allowZone);
        if (input.Length != file.Length)
            throw new InvalidDataException("El tamaño recuperado no coincide con el catálogo.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(file.Hash))
            throw new InvalidDataException("El contenido recuperado no coincide con su SHA-256. Se conserva para revisión.");
    }
}
