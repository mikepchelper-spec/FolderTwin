using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal enum CatalogStep { PrimaryFlushed, PrimaryPublished, BackupFlushed, BackupPublished, CommitFlushed, Committed }
internal sealed record CatalogSnapshot(DestinationBinding Binding, string[] Directories, BatchFile[] Files, byte[] DocumentHash, bool Degraded = false)
{
    internal BatchPlan RestoreTo(string fixture, string backup, string destination, DestinationBinding target) =>
        new(fixture, backup, destination, Binding.Identity, target, Directories, Files, Binding);
}
internal readonly record struct CatalogFile(SafeFileHandle Parent, string Name);
internal readonly record struct CatalogFiles(CatalogFile Primary, CatalogFile Backup, CatalogFile Commit);

// One initial batch per synthetic destination. See CATALOG.md before changing the format.
internal static class BatchCatalog
{
    internal const string PrimaryName = ".foldertwin-catalog-primary.jsonl";
    internal const string BackupName = ".foldertwin-catalog-backup.jsonl";
    internal const string CommitName = ".foldertwin-catalog.commit";
    internal const int MaximumBytes = 8 * 1024 * 1024;
    internal const int VersionMaximumBytes = 128 * 1024 * 1024;
    internal const int VersionMaximumEntries = 350_000;
    internal const int MaximumLineBytes = 64 * 1024;
    private const int CommitPayloadSize = 80;
    private const int CommitSize = CommitPayloadSize + 32;
    private static ReadOnlySpan<byte> CommitMagic => "FTCAT001"u8;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 16
    };
    private sealed record Header(string Kind, int Version, Guid JobId, string GenerationId, byte[] Identity);
    private sealed record DirectoryRecord(string Kind, string Path);
    private sealed record FileRecord(string Kind, string Path, long Size, byte[] Hash);
    private sealed record DatedFileRecord(string Kind, string Path, long Size, byte[] Hash, DateTime LastWriteUtc);
    private sealed record FileRecordV3(string Kind, string Path, long Size, byte[] Hash,
        DateTime LastWriteUtc, DateTime CreationUtc, bool Hidden);
    private sealed record Footer(string Kind, int Files, int Directories, byte[] Hash);

    private static int EntryLimit(string generation) => generation == "initial" ? FolderInventory.DefaultMaximumEntries : VersionMaximumEntries;
    private static int ByteLimit(string generation) => generation == "initial" ? MaximumBytes : VersionMaximumBytes;

    internal static void Publish(BatchPlan plan, Action<CatalogStep>? checkpoint = null)
    {
        FolderBatch.ValidateEntries(plan.Files, plan.Directories);
        using var root = new DirectoryFence(plan.Destination);
        using var marker = plan.Target.OpenVerifiedMarker(root.Root);
        using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.ReadWrite, FileShare.None);
        PublishLocked(plan, InitialFiles(root.Root), "initial", () => VerifyData(plan), checkpoint);
    }

    internal static void PublishLocked(BatchPlan plan, CatalogFiles files, string generation, Action verifyData,
        Action<CatalogStep>? checkpoint)
    {
        byte[] expected = WriteMetadata(Stream.Null, plan, generation);
        verifyData();
        PublishReplica(files.Primary, CatalogStep.PrimaryFlushed, CatalogStep.PrimaryPublished);
        PublishReplica(files.Backup, CatalogStep.BackupFlushed, CatalogStep.BackupPublished);
        verifyData();
        byte[] confirmation = Confirmation(plan.Target, expected);
        var commit = files.Commit;
        if (!WindowsProbeNative.PlainFileExists(commit.Parent, commit.Name))
        {
            string draft = commit.Name + ".tmp";
            if (!WindowsProbeNative.PlainFileExists(commit.Parent, draft))
            {
                using var output = WindowsProbeNative.OpenPlainFile(commit.Parent, draft, FileAccess.Write, FileShare.None, FileMode.CreateNew);
                output.Write(confirmation);
                output.Flush(flushToDisk: true);
            }
            using var verified = WindowsProbeNative.OpenPlainFile(commit.Parent, draft, FileAccess.Read, FileShare.Read, rename: true);
            if (!ReadConfirmation(verified).AsSpan().SequenceEqual(confirmation))
                throw new InvalidDataException("La confirmación pendiente no corresponde al catálogo.");
            checkpoint?.Invoke(CatalogStep.CommitFlushed);
            WindowsProbeNative.RenameNew(verified.SafeFileHandle, commit.Parent, commit.Name);
        }
        using (var committed = WindowsProbeNative.OpenPlainFile(commit.Parent, commit.Name, FileAccess.Read, FileShare.Read))
            if (!ReadConfirmation(committed).AsSpan().SequenceEqual(confirmation))
                throw new InvalidDataException("Ya existe una confirmación diferente.");
        checkpoint?.Invoke(CatalogStep.Committed);

        void PublishReplica(CatalogFile replica, CatalogStep flushed, CatalogStep published)
        {
            if (WindowsProbeNative.PlainFileExists(replica.Parent, replica.Name))
            {
                using var existing = WindowsProbeNative.OpenPlainFile(replica.Parent, replica.Name, FileAccess.Read, FileShare.Read);
                if (!Parse(existing, generation).DocumentHash.AsSpan().SequenceEqual(expected))
                    throw new InvalidDataException("Ya existe un catálogo diferente.");
            }
            else
            {
                string draft = replica.Name + ".tmp";
                if (!WindowsProbeNative.PlainFileExists(replica.Parent, draft))
                {
                    using var output = WindowsProbeNative.OpenPlainFile(replica.Parent, draft, FileAccess.Write, FileShare.None, FileMode.CreateNew);
                    if (!WriteMetadata(output, plan, generation).AsSpan().SequenceEqual(expected))
                        throw new InvalidDataException("El catálogo cambió durante la escritura.");
                    output.Flush(flushToDisk: true);
                }
                using var verified = WindowsProbeNative.OpenPlainFile(replica.Parent, draft, FileAccess.Read, FileShare.Read, rename: true);
                if (!Parse(verified, generation).DocumentHash.AsSpan().SequenceEqual(expected))
                    throw new InvalidDataException("El catálogo pendiente no corresponde al lote.");
                checkpoint?.Invoke(flushed);
                WindowsProbeNative.RenameNew(verified.SafeFileHandle, replica.Parent, replica.Name);
            }
            checkpoint?.Invoke(published);
        }
    }

    internal static CatalogSnapshot Read(string backup)
    {
        using var root = new DirectoryFence(backup);
        using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.Read, FileShare.None);
        return ReadLocked(root.Root, InitialFiles(root.Root), "initial");
    }

    internal static BatchResult Verify(string backup, DestinationBinding expected, IProgress<BatchProgress>? progress = null)
    {
        using var root = new DirectoryFence(backup);
        using var marker = expected.OpenVerifiedMarker(root.Root);
        WindowsProbeNative.RequireSupportedData(root.Root, Path.GetFileName(backup));
        using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.Read, FileShare.None);
        var catalog = ReadLocked(root.Root, InitialFiles(root.Root), "initial");
        var entries = new List<BatchEntryResult>();
        foreach (string relative in catalog.Directories)
            Check(relative, isDirectory: true, () =>
            {
                using var directory = new DirectoryFence(Path.Combine(backup, relative));
                WindowsProbeNative.RequireSupportedData(directory.Root, relative);
            });
        int verifiedFiles = 0;
        for (int index = 0; index < catalog.Files.Length; index++)
        {
            var file = catalog.Files[index];
            progress?.Report(new(index, catalog.Files.Length, verifiedFiles, file.RelativePath));
            Check(file.RelativePath, isDirectory: false, () => VerifyFile(backup, file));
            if (entries[^1].Succeeded)
                verifiedFiles++;
        }
        progress?.Report(new(catalog.Files.Length, catalog.Files.Length, verifiedFiles, CurrentPath: null));

        return new(entries.ToArray(), Note: catalog.Degraded
            ? "Se usó una réplica íntegra. La otra copia del catálogo necesita revisión." : null);

        void Check(string relative, bool isDirectory, Action verify)
        {
            try
            {
                verify();
                entries.Add(new(relative, isDirectory, isDirectory ? BatchStatus.DirectoryReady : BatchStatus.AlreadyVerified));
            }
            catch (FileNotFoundException)
            {
                entries.Add(new(relative, isDirectory, BatchStatus.Failed, "Falta en el respaldo."));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
            {
                bool inUse = FileUseDiagnostics.IsSharingViolation(error);
                entries.Add(new(relative, isDirectory, BatchStatus.Failed,
                    inUse ? "Está en uso. Reintenta cuando se cierre." : error.Message, inUse));
            }
        }
    }

    private static CatalogFiles InitialFiles(SafeFileHandle root) =>
        new(new(root, PrimaryName), new(root, BackupName), new(root, CommitName));

    internal static CatalogSnapshot ReadLocked(SafeFileHandle root, CatalogFiles files, string generation)
    {
        byte[] commit;
        using (var file = WindowsProbeNative.OpenPlainFile(files.Commit.Parent, files.Commit.Name, FileAccess.Read, FileShare.Read))
            commit = ReadConfirmation(file);
        var binding = new DestinationBinding
        {
            Version = 1, Marker = new Guid(commit.AsSpan(64, 16)), Identity = commit.AsSpan(40, 24).ToArray(),
            LastVolumePath = WindowsProbeNative.VolumePath(root)
        };
        using var marker = binding.OpenVerifiedMarker(root);
        var primary = TryRead(files.Primary);
        var secondary = TryRead(files.Backup);
        if (primary is not null && secondary is not null && !primary.DocumentHash.AsSpan().SequenceEqual(secondary.DocumentHash))
            throw new InvalidDataException("Ambas copias del catálogo son válidas pero diferentes.");
        var selected = primary ?? secondary ?? throw new InvalidDataException("Ninguna copia del catálogo es válida.");
        if (!selected.DocumentHash.AsSpan().SequenceEqual(commit.AsSpan(8, 32)) || selected.Binding.Marker != binding.Marker ||
            !selected.Binding.Identity.AsSpan().SequenceEqual(binding.Identity))
            throw new InvalidDataException("El catálogo no corresponde a la confirmación o al destino.");
        return selected with { Binding = binding, Degraded = primary is null || secondary is null };

        CatalogSnapshot? TryRead(CatalogFile candidate)
        {
            try
            {
                using var file = WindowsProbeNative.OpenPlainFile(candidate.Parent, candidate.Name, FileAccess.Read, FileShare.Read);
                return Parse(file, generation);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or Win32Exception or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    internal static byte[] WriteMetadata(Stream output, BatchPlan plan, string generation = "initial")
    {
        if (plan.Files.Length + plan.Directories.Length > EntryLimit(generation))
            throw new InvalidDataException("Demasiadas entradas en el catálogo.");
        int byteLimit = ByteLimit(generation);
        using var body = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var document = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int total = 0;
        int version = plan.Files.FirstOrDefault()?.CatalogVersion ?? 1;
        Line(new Header("header", version, plan.Target.Marker, generation, plan.Target.Identity));
        foreach (string directory in plan.Directories.Order(StringComparer.Ordinal))
            Line(new DirectoryRecord("directory", directory));
        foreach (var file in plan.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            if (file.Metadata is not { } metadata)
                Line(new FileRecord("file", file.RelativePath, file.Length, file.Hash));
            else if (metadata.CreationUtc is { } creation && metadata.Hidden is { } hidden)
                Line(new FileRecordV3("file", file.RelativePath, file.Length, file.Hash, metadata.LastWriteUtc, creation, hidden));
            else
                Line(new DatedFileRecord("file", file.RelativePath, file.Length, file.Hash, metadata.LastWriteUtc));
        }
        Line(new Footer("footer", plan.Files.Length, plan.Directories.Length, body.GetHashAndReset()), footer: true);
        return document.GetHashAndReset();

        void Line<T>(T value, bool footer = false)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            total = checked(total + bytes.Length + 1);
            if (bytes.Length + 1 > MaximumLineBytes || total > byteLimit)
                throw new InvalidDataException("El catálogo supera los límites del laboratorio.");
            output.Write(bytes);
            output.WriteByte(10);
            document.AppendData(bytes);
            document.AppendData([10]);
            if (!footer)
            {
                body.AppendData(bytes);
                body.AppendData([10]);
            }
        }
    }

    // Streaming framing with bounded records; only the validated entries are retained.
    internal static CatalogSnapshot Parse(Stream input, string generation = "initial")
    {
        if (input.Length < 1 || input.Length > ByteLimit(generation))
            throw new InvalidDataException("Tamaño de catálogo no válido.");
        int entryLimit = EntryLimit(generation);
        using var body = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var document = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[MaximumLineBytes - 1];
        byte[] headerLine = Line() ?? throw new InvalidDataException("Falta la cabecera.");
        var header = Decode<Header>(headerLine);
        if (header.Kind != "header" || header.Version is not (1 or 2 or 3) || header.GenerationId != generation || header.JobId == Guid.Empty ||
            header.Identity is not { Length: 24 })
            throw new InvalidDataException("Cabecera no soportada.");
        HashBody(headerLine);
        var directories = new List<string>();
        var files = new List<BatchFile>();
        string? lastDirectory = null;
        string? lastFile = null;
        while (true)
        {
            byte[] line = Line() ?? throw new InvalidDataException("Falta el cierre del catálogo.");
            using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("kind", out var kind) ||
                kind.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Registro sin tipo.");
            switch (kind.GetString())
            {
                case "directory":
                    var directory = Decode<DirectoryRecord>(line);
                    if (files.Count != 0 || directory.Path is null || lastDirectory is not null && string.CompareOrdinal(lastDirectory, directory.Path) >= 0)
                        throw new InvalidDataException("Orden de carpetas no válido.");
                    directories.Add(directory.Path);
                    lastDirectory = directory.Path;
                    break;
                case "file":
                    BatchFile file;
                    if (header.Version == 1)
                    {
                        var entry = Decode<FileRecord>(line);
                        file = new(entry.Path, entry.Size, entry.Hash);
                    }
                    else if (header.Version == 2)
                    {
                        var entry = Decode<DatedFileRecord>(line);
                        file = new(entry.Path, entry.Size, entry.Hash, new(entry.LastWriteUtc));
                    }
                    else
                    {
                        var entry = Decode<FileRecordV3>(line);
                        file = new(entry.Path, entry.Size, entry.Hash, new(entry.LastWriteUtc, entry.CreationUtc, entry.Hidden));
                    }
                    if (file.RelativePath is null || lastFile is not null && string.CompareOrdinal(lastFile, file.RelativePath) >= 0)
                        throw new InvalidDataException("Orden de archivos no válido.");
                    files.Add(file);
                    lastFile = file.RelativePath;
                    break;
                case "footer":
                    var footer = Decode<Footer>(line);
                    if (footer.Files != files.Count || footer.Directories != directories.Count || footer.Hash is not { Length: 32 } ||
                        !body.GetHashAndReset().AsSpan().SequenceEqual(footer.Hash) || Line() is not null)
                        throw new InvalidDataException("El cierre, recuento o hash del catálogo no es válido.");
                    var validatedFiles = files.ToArray();
                    var validatedDirectories = directories.ToArray();
                    FolderBatch.ValidateEntries(validatedFiles, validatedDirectories, entryLimit);
                    return new(new DestinationBinding { Version = 1, Marker = header.JobId, Identity = header.Identity, LastVolumePath = "" },
                        validatedDirectories, validatedFiles, document.GetHashAndReset());
                default:
                    throw new InvalidDataException("Tipo de registro no soportado.");
            }
            if (directories.Count + files.Count > entryLimit)
                throw new InvalidDataException("Demasiadas entradas en el catálogo.");
            HashBody(line);
        }

        byte[]? Line()
        {
            int length = 0;
            int value;
            while ((value = input.ReadByte()) != -1)
            {
                if (value == 10)
                {
                    if (length == 0)
                        throw new InvalidDataException("Línea vacía en el catálogo.");
                    var bytes = buffer.AsSpan(0, length).ToArray();
                    _ = Utf8.GetCharCount(bytes);
                    document.AppendData(bytes);
                    document.AppendData([10]);
                    return bytes;
                }
                if (length == buffer.Length || value == 13)
                    throw new InvalidDataException("Separador o tamaño de registro no válido.");
                buffer[length++] = (byte)value;
            }
            if (length != 0)
                throw new InvalidDataException("Línea truncada en el catálogo.");
            return null;
        }
        void HashBody(ReadOnlySpan<byte> bytes)
        {
            body.AppendData(bytes);
            body.AppendData([10]);
        }
    }

    private static T Decode<T>(byte[] line) => JsonSerializer.Deserialize<T>(line, Json)
        ?? throw new InvalidDataException("Registro nulo en el catálogo.");

    private static byte[] Confirmation(DestinationBinding binding, byte[] hash)
    {
        byte[] bytes = new byte[CommitSize];
        CommitMagic.CopyTo(bytes);
        hash.CopyTo(bytes, 8);
        binding.Identity.CopyTo(bytes, 40);
        binding.Marker.ToByteArray().CopyTo(bytes, 64);
        SHA256.HashData(bytes.AsSpan(0, CommitPayloadSize), bytes.AsSpan(CommitPayloadSize));
        return bytes;
    }
    private static byte[] ReadConfirmation(Stream file)
    {
        byte[] bytes = new byte[CommitSize];
        if (file.ReadAtLeast(bytes, bytes.Length, false) != bytes.Length || file.ReadByte() != -1 ||
            !bytes.AsSpan(0, 8).SequenceEqual(CommitMagic) ||
            !SHA256.HashData(bytes.AsSpan(0, CommitPayloadSize)).AsSpan().SequenceEqual(bytes.AsSpan(CommitPayloadSize)))
            throw new InvalidDataException("Confirmación ausente, truncada o dañada.");
        return bytes;
    }

    private static void VerifyData(BatchPlan plan)
    {
        using var root = new DirectoryFence(plan.Destination);
        WindowsProbeNative.RequireSupportedData(root.Root, Path.GetFileName(plan.Destination));
        foreach (string relative in plan.Directories)
        {
            using var directory = new DirectoryFence(Path.Combine(plan.Destination, relative));
            WindowsProbeNative.RequireSupportedData(directory.Root, relative);
        }
        foreach (var item in plan.Files)
            VerifyFile(plan.Destination, item);
    }

    internal static void VerifyFile(string backup, BatchFile item)
    {
        string path = Path.Combine(backup, item.RelativePath);
        using var parent = new DirectoryFence(backup, path);
        using var file = WindowsProbeNative.OpenPlainFile(parent.Parent(path), path, FileAccess.Read, FileShare.Read);
        WindowsProbeNative.RequireSupportedData(file.SafeFileHandle, item.RelativePath);
        if (file.Length != item.Length || !SHA256.HashData(file).AsSpan().SequenceEqual(item.Hash))
            throw new InvalidDataException("El contenido del respaldo no coincide con el catálogo.");
        item.Metadata?.RequireMatch(file.SafeFileHandle);
    }
}
