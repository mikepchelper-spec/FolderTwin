using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal sealed record BatchFile(string RelativePath, long Length, byte[] Hash, FileMetadata? Metadata = null)
{
    internal int CatalogVersion => Metadata is null ? 1 : Metadata.CreationUtc is null ? 2 : 3;
}
internal sealed record BatchPlan(string FixtureRoot, string Source, string Destination, byte[] SourceIdentity,
    DestinationBinding Target, string[] Directories, BatchFile[] Files, DestinationBinding? SourceBinding = null)
{
    public long Bytes => Files.Sum(file => file.Length);
}

internal enum BatchStatus { Copied, Recovered, AlreadyVerified, DirectoryReady, Failed, Cancelled }
internal sealed record BatchEntryResult(string RelativePath, bool IsDirectory, BatchStatus Status, string? Detail = null, bool InUse = false)
{
    public bool Succeeded => Status is BatchStatus.Copied or BatchStatus.Recovered or BatchStatus.AlreadyVerified or BatchStatus.DirectoryReady;
}
internal sealed record BatchProgress(int ProcessedFiles, int TotalFiles, int VerifiedFiles, string? CurrentPath);

internal sealed record BatchResult(BatchEntryResult[] Entries, string? Problem = null, string? Note = null, bool Cancelled = false)
{
    public bool Complete => !Cancelled && Problem is null && Entries.All(entry => entry.Succeeded);
    public int VerifiedFiles => Entries.Count(entry => !entry.IsDirectory && entry.Succeeded);
}

// Synthetic NTFS batches only. PendingBatch persists copy intent;
// BatchCatalog separately confirms completed data for independent restoration.
internal static class FolderBatch
{
    private static readonly Encoding PathEncoding = new UTF8Encoding(false, true);
    internal const string ControlPrefix = ".foldertwin-";
    internal const string LockName = ControlPrefix + "batch.lock";

    internal static BatchPlan Prepare(string fixtureRoot, string source, string destination, DestinationBinding binding)
    {
        ValidateRoots(fixtureRoot, source, destination);
        using var sourceFence = new DirectoryFence(source);
        using var targetFence = new DirectoryFence(destination);
        using var marker = binding.OpenVerifiedMarker(targetFence.Root);
        var inventory = FolderInventory.Scan(source);
        if (!inventory.Complete || inventory.Entries.Any(entry => IsControl(entry.RelativePath)))
            throw new InvalidDataException("El origen tiene omisiones o nombres reservados. Revisa el análisis.");
        var destinationInventory = FolderInventory.Scan(destination);
        if (!destinationInventory.Complete || destinationInventory.Entries.Any(entry => entry.RelativePath != DestinationBinding.MarkerName))
            throw new InvalidDataException("El destino tiene archivos o datos pendientes que necesitan revisión.");
        string[] directories = inventory.Entries.Where(entry => entry.IsDirectory).Select(entry => entry.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        CheckDirectoryData(source, directories);
        var files = new List<BatchFile>();
        foreach (var entry in inventory.Entries.Where(entry => !entry.IsDirectory).OrderBy(entry => entry.RelativePath, StringComparer.Ordinal))
        {
            string path = Path.Combine(source, entry.RelativePath);
            using var fence = new DirectoryFence(source, path);
            using var file = WindowsProbeNative.OpenPlainFile(fence.Parent(path), path, FileAccess.Read, FileShare.Read);
            WindowsProbeNative.RequireSupportedData(file.SafeFileHandle, entry.RelativePath);
            if (file.Length != entry.Length)
                throw new InvalidDataException("El origen cambió durante el análisis.");
            var metadata = FileMetadata.Read(file.SafeFileHandle);
            byte[] hash = SHA256.HashData(file);
            if (!metadata.IsValid())
                throw new InvalidDataException($"Las fechas de «{entry.RelativePath}» están fuera del alcance.");
            metadata.RequireMatch(file.SafeFileHandle);
            files.Add(new(entry.RelativePath, entry.Length, hash, metadata));
        }
        return new(fixtureRoot, source, destination, WindowsProbeNative.DirectoryIdentity(sourceFence.Root), binding,
            directories, files.ToArray());
    }

    internal static BatchResult Run(BatchPlan plan, CancellationToken cancellationToken = default,
        Action<string, CopyStep, string>? checkpoint = null, bool resume = false, bool finishCurrentFileOnCancel = false,
        IProgress<BatchProgress>? progress = null, Action<BatchProgress>? waitBetweenFiles = null)
    {
        var results = new List<BatchEntryResult>();
        DirectoryFence? source = null;
        FileStream? sourceMarker = null;
        FileStream? sourceOperation = null;
        Exception? sourceFailure = null;
        bool sourceReady = false;
        try
        {
            ValidateRoots(plan.FixtureRoot, plan.Source, plan.Destination);
            ValidateEntries(plan.Files, plan.Directories);
            var expectedFiles = plan.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
            using var destination = new DirectoryFence(plan.Destination);
            using var targetMarker = plan.Target.OpenVerifiedMarker(destination.Root);
            cancellationToken.ThrowIfCancellationRequested();
            if (!resume)
                EnsureSource();
            using var operation = WindowsProbeNative.OpenPlainFile(destination.Root, LockName, FileAccess.ReadWrite,
                FileShare.None, FileMode.OpenOrCreate);

            foreach (string directory in plan.Directories)
                Execute(directory, true, () => WithDirectories(destination.Root, directory, () => BatchStatus.DirectoryReady));
            int processedFiles = 0;
            int verifiedFiles = 0;
            foreach (var item in plan.Files)
            {
                if (!cancellationToken.IsCancellationRequested)
                    waitBetweenFiles?.Invoke(new(processedFiles, plan.Files.Length, verifiedFiles, CurrentPath: null));
                if (!cancellationToken.IsCancellationRequested)
                    progress?.Report(new(processedFiles, plan.Files.Length, verifiedFiles, item.RelativePath));
                Execute(item.RelativePath, false, () => WithDirectories(destination.Root,
                    Path.GetDirectoryName(item.RelativePath) ?? "", () => CopyOne(item)));
                if (results[^1].Status != BatchStatus.Cancelled)
                    processedFiles++;
                if (results[^1].Succeeded)
                    verifiedFiles++;
            }
            cancellationToken.ThrowIfCancellationRequested();
            waitBetweenFiles?.Invoke(new(processedFiles, plan.Files.Length, verifiedFiles, CurrentPath: null));
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(processedFiles, plan.Files.Length, verifiedFiles, CurrentPath: null));
            if (sourceReady)
                RequireSameInventory(plan);
            for (int index = 0; index < results.Count; index++)
            {
                var entry = results[index];
                if (!entry.Succeeded)
                    continue;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string output = Path.Combine(plan.Destination, entry.RelativePath);
                    if (entry.IsDirectory)
                    {
                        using var directory = new DirectoryFence(output);
                        WindowsProbeNative.RequireSupportedData(directory.Root, entry.RelativePath);
                    }
                    else
                    {
                        var item = expectedFiles[entry.RelativePath];
                        using var fence = new DirectoryFence(plan.Destination, output);
                        using var file = WindowsProbeNative.OpenPlainFile(fence.Parent(output), output, FileAccess.Read, FileShare.Read);
                        WindowsProbeNative.RequireSupportedData(file.SafeFileHandle, entry.RelativePath);
                        if (file.Length != item.Length || !SHA256.HashData(file).AsSpan().SequenceEqual(item.Hash))
                            throw new InvalidDataException("La copia cambió antes de terminar el lote.");
                        item.Metadata?.RequireMatch(file.SafeFileHandle);
                    }
                }
                catch (Exception error) when (IsExpectedFailure(error))
                {
                    results[index] = entry with { Status = BatchStatus.Failed, Detail = error.Message,
                        InUse = FileUseDiagnostics.IsSharingViolation(error) };
                }
            }
            return new(results.ToArray());

            void Execute(string relative, bool isDirectory, Func<BatchStatus> action)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    results.Add(new(relative, isDirectory, BatchStatus.Cancelled, "Operación cancelada."));
                    return;
                }
                try
                {
                    results.Add(new(relative, isDirectory, action()));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    results.Add(new(relative, isDirectory, BatchStatus.Cancelled, "Operación cancelada. Se conservan los datos pendientes."));
                }
                catch (Exception error) when (IsExpectedFailure(error))
                {
                    string detail = FileUseDiagnostics.IsSharingViolation(error)
                        ? "El archivo está en uso. Cierra el archivo y reintenta."
                        : error.Message;
                    results.Add(new(relative, isDirectory, BatchStatus.Failed, detail, FileUseDiagnostics.IsSharingViolation(error)));
                }
            }

            BatchStatus CopyOne(BatchFile item)
            {
                string input = Path.Combine(plan.Source, item.RelativePath);
                string output = Path.Combine(plan.Destination, item.RelativePath);
                string temporary = TemporaryPath(plan.Destination, item.RelativePath);
                BatchStatus? known = resume ? RecoverKnown() : null;
                if (known.HasValue)
                    return known.Value;
                EnsureSource();
                using var sourceFileFence = new DirectoryFence(plan.Source, input);
                using var sourceFile = WindowsProbeNative.OpenPlainFile(sourceFileFence.Parent(input), input, FileAccess.Read, FileShare.Read);
                WindowsProbeNative.RequireSupportedData(sourceFile.SafeFileHandle, item.RelativePath);
                if (sourceFile.Length != item.Length || !SHA256.HashData(sourceFile).AsSpan().SequenceEqual(item.Hash))
                    throw new InvalidDataException("El contenido cambió desde el análisis. Revisa el archivo.");
                if (!resume)
                {
                    known = RecoverKnown();
                    if (known.HasValue)
                        return known.Value;
                }
                VerifiedCopy.Run(input, output, temporary,
                    (step, path) => checkpoint?.Invoke(item.RelativePath, step, path), expectedHash: item.Hash,
                    recoveryRoot: plan.FixtureRoot,
                    cancellationToken: finishCurrentFileOnCancel ? CancellationToken.None : cancellationToken,
                    metadata: item.Metadata);
                return BatchStatus.Copied;

                BatchStatus? RecoverKnown()
                {
                    var recovered = CopyRecovery.Recover(plan.FixtureRoot, temporary, output,
                        expectedHash: item.Hash, metadata: item.Metadata);
                    if (recovered == RecoveryResult.NoReceipt)
                    {
                        using var pending = new DirectoryFence(plan.Destination, temporary, output);
                        var parent = pending.Parent(output);
                        if (WindowsProbeNative.PlainFileExists(parent, temporary) ||
                            WindowsProbeNative.PlainFileExists(parent, CopyRecovery.ReceiptDraftPath(temporary)) ||
                            WindowsProbeNative.PlainFileExists(parent, output))
                            throw new InvalidDataException("Hay datos sin verificar o un conflicto en destino. Se conservan para revisión.");
                    }
                    return recovered switch
                    {
                        RecoveryResult.Published => BatchStatus.Recovered,
                        RecoveryResult.AlreadyPublished => BatchStatus.AlreadyVerified,
                        RecoveryResult.NoReceipt => null,
                        _ => throw new InvalidDataException($"Datos pendientes requieren revisión: {recovered}.")
                    };
                }
            }

            void EnsureSource()
            {
                if (sourceReady)
                    return;
                if (sourceFailure is null)
                {
                    try
                    {
                        source = new DirectoryFence(plan.Source);
                        if (!WindowsProbeNative.DirectoryIdentity(source.Root).AsSpan().SequenceEqual(plan.SourceIdentity))
                            throw new InvalidDataException("La carpeta de origen cambió de identidad.");
                        sourceMarker = plan.SourceBinding?.OpenVerifiedMarker(source.Root);
                        sourceOperation = plan.SourceBinding is null ? null : WindowsProbeNative.OpenPlainFile(source.Root,
                            LockName, FileAccess.Read, FileShare.None);
                        RequireSameInventory(plan);
                        sourceReady = true;
                        return;
                    }
                    catch (Exception error) when (IsExpectedFailure(error))
                    {
                        sourceFailure = error;
                    }
                }
                throw new InvalidDataException("Se necesita el origen del análisis para copiar los archivos que faltan. " + sourceFailure.Message,
                    sourceFailure);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(results.ToArray(), Cancelled: true);
        }
        catch (Exception error) when (IsExpectedFailure(error))
        {
            return new(results.ToArray(), error.Message);
        }
        finally
        {
            sourceOperation?.Dispose();
            sourceMarker?.Dispose();
            source?.Dispose();
        }
    }

    internal static string TemporaryPath(string destination, string relative) => Path.Combine(
        destination, Path.GetDirectoryName(relative) ?? "",
        ControlPrefix + Convert.ToHexString(SHA256.HashData(PathEncoding.GetBytes(relative))) + ".tmp");

    private static T WithDirectories<T>(SafeFileHandle root, string relative, Func<T> action)
    {
        var opened = new List<SafeFileHandle>();
        try
        {
            var parent = root;
            foreach (string name in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var child = WindowsProbeNative.OpenOrCreatePlainDirectory(parent, name);
                opened.Add(child);
                parent = child;
            }
            return action();
        }
        finally
        {
            foreach (var directory in opened.AsEnumerable().Reverse())
                directory.Dispose();
        }
    }

    private static void RequireSameInventory(BatchPlan plan)
    {
        // A stored file can retain its data, temporary, lock, receipt, and draft.
        int limit = plan.SourceBinding is null ? 10_000 : plan.Files.Length * 5 + plan.Directories.Length + 16;
        var current = FolderInventory.Scan(plan.Source, maximumEntries: limit);
        if (!current.Complete)
            throw new InvalidDataException("El origen no pudo enumerarse por completo. Repite el análisis.");
        var entries = current.Entries.Where(entry => plan.SourceBinding is null || !IsControl(entry.RelativePath)).ToArray();
        if (entries.Length != plan.Files.Length + plan.Directories.Length ||
            !entries.Where(entry => entry.IsDirectory).Select(entry => entry.RelativePath).Order(StringComparer.Ordinal)
                .SequenceEqual(plan.Directories.Order(StringComparer.Ordinal)) ||
            !entries.Where(entry => !entry.IsDirectory).Select(entry => (entry.RelativePath, entry.Length)).OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .SequenceEqual(plan.Files.Select(file => (file.RelativePath, file.Length)).OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)))
            throw new InvalidDataException("El inventario del origen cambió. Repite el análisis.");
        CheckDirectoryData(plan.Source, plan.Directories);
    }

    private static void CheckDirectoryData(string root, string[] directories)
    {
        using var rootFence = new DirectoryFence(root);
        WindowsProbeNative.RequireSupportedData(rootFence.Root, Path.GetFileName(root));
        foreach (string relative in directories)
        {
            using var directory = new DirectoryFence(Path.Combine(root, relative));
            WindowsProbeNative.RequireSupportedData(directory.Root, relative);
        }
    }

    internal static void ValidateRoots(string fixture, string source, string destination)
    {
        using var fence = new DirectoryFence(fixture);
        foreach (string root in new[] { source, destination })
        {
            if (!Path.IsPathFullyQualified(root) || Path.GetFullPath(root) != root)
                throw new ArgumentException("La raíz del lote no es canónica.");
            string child = Path.GetRelativePath(fixture, root);
            if (child == "." || !Within(child))
                throw new ArgumentException("La raíz del lote está fuera del laboratorio.");
            foreach (string component in child.Split(Path.DirectorySeparatorChar))
                DirectoryFence.CheckName(component);
        }
        string relative = Path.GetRelativePath(source, destination);
        string reverse = Path.GetRelativePath(destination, source);
        if (Within(relative) || Within(reverse))
            throw new ArgumentException("El origen y el destino no pueden coincidir ni estar anidados.");
        static bool Within(string relative) => relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    internal static void ValidateEntries(BatchFile[] files, string[] directories)
    {
        if (files.Length + directories.Length > 10_000)
            throw new ArgumentException("El lote supera el límite del laboratorio.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryNames = directories.ToHashSet(StringComparer.Ordinal);
        foreach (string relative in files.Select(file => file.RelativePath).Concat(directories))
        {
            _ = PathEncoding.GetByteCount(relative);
            if (Path.IsPathRooted(relative) || !names.Add(relative) || IsControl(relative))
                throw new ArgumentException("Ruta de lote no soportada.");
            foreach (string component in relative.Split(Path.DirectorySeparatorChar))
            {
                DirectoryFence.CheckName(component);
                if (component is "." or "..")
                    throw new ArgumentException("Ruta de lote fuera de alcance.");
            }
            string? parent = Path.GetDirectoryName(relative);
            if (!string.IsNullOrEmpty(parent) && !directoryNames.Contains(parent))
                throw new ArgumentException("Falta una carpeta padre en el lote.");
            int components = relative.Count(character => character == Path.DirectorySeparatorChar) + 1;
            if (components > (directoryNames.Contains(relative) ? 64 : 65))
                throw new ArgumentException("La ruta supera el límite de profundidad.");
        }
        if (files.Any(file => file.Hash is not { Length: SHA256.HashSizeInBytes } || file.Length < 0))
            throw new ArgumentException("Hash o tamaño de lote no válido.");
        if (files.Length != 0 && files.Any(file => file.CatalogVersion != files[0].CatalogVersion ||
            file.Metadata is { } metadata && !metadata.IsValid()))
            throw new InvalidDataException("Los archivos del lote tienen metadatos inválidos o de versiones distintas.");
        try { _ = files.Sum(file => file.Length); }
        catch (OverflowException error) { throw new ArgumentException("El tamaño total del lote no es válido.", error); }
    }

    private static bool IsControl(string relative) => relative.Split(Path.DirectorySeparatorChar)
        .Any(name => name.StartsWith(ControlPrefix, StringComparison.OrdinalIgnoreCase));
    private static bool IsExpectedFailure(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or ArgumentException;
}
