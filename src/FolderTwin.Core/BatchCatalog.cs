using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace FolderTwin.Core;


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
    internal const int VersionMaximumBytes = 512 * 1024 * 1024;
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
    private sealed record FileRecordV4(string Kind, string Path, long Size, byte[] Hash,
        DateTime LastWriteUtc, DateTime CreationUtc, bool Hidden, string? ZoneData, string? ZoneHash);
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
            WindowsProbeNative.FlushDirectory(commit.Parent);
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
                if (!MatchesDocument(existing, generation, expected))
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
                if (!MatchesDocument(verified, generation, expected))
                    throw new InvalidDataException("El catálogo pendiente no corresponde al lote.");
                checkpoint?.Invoke(flushed);
                WindowsProbeNative.RenameNew(verified.SafeFileHandle, replica.Parent, replica.Name);
                WindowsProbeNative.FlushDirectory(replica.Parent);
            }
            checkpoint?.Invoke(published);
        }
    }

    // The expected digest is produced by the canonical serializer after plan
    // validation. Re-read the bytes without allocating a second parsed tree.
    internal static bool MatchesDocument(Stream input, string generation, byte[] expected) =>
        input.Length is > 0 && input.Length <= ByteLimit(generation) &&
        SHA256.HashData(input).AsSpan().SequenceEqual(expected);

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

    internal static CatalogSnapshot ReadLocked(SafeFileHandle root, CatalogFiles files, string generation, bool requireBoth = false)
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
        // Cleanup requires identical replicas. When primary already matches the
        // commit digest, check the backup via streaming SHA-256 before falling back to full parse.
        var secondary = requireBoth ? ReadIdenticalReplica()
            : primary is not null && TryMatchReplica(files.Backup, generation, primary.DocumentHash) ? primary
            : TryRead(files.Backup);
        if (primary is not null && secondary is not null && !primary.DocumentHash.AsSpan().SequenceEqual(secondary.DocumentHash))
            throw new InvalidDataException("Ambas copias del catálogo son válidas pero diferentes.");
        var selected = primary ?? secondary ?? throw new InvalidDataException("Ninguna copia del catálogo es válida.");
        if (!selected.DocumentHash.AsSpan().SequenceEqual(commit.AsSpan(8, 32)) || selected.Binding.Marker != binding.Marker ||
            !selected.Binding.Identity.AsSpan().SequenceEqual(binding.Identity))
            throw new InvalidDataException("El catálogo no corresponde a la confirmación o al destino.");
        return selected with { Binding = binding, Degraded = primary is null || secondary is null };

        CatalogSnapshot ReadIdenticalReplica()
        {
            if (primary is null)
                throw new InvalidDataException("El catálogo primario no es válido para retención.");
            using var replica = WindowsProbeNative.OpenPlainFile(files.Backup.Parent, files.Backup.Name, FileAccess.Read, FileShare.Read);
            if (replica.Length < 1 || replica.Length > ByteLimit(generation) ||
                !SHA256.HashData(replica).AsSpan().SequenceEqual(primary.DocumentHash))
                throw new InvalidDataException("Las dos réplicas del catálogo deben coincidir para retención.");
            return primary;
        }

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

    internal static (int FileCount, bool Degraded) ReadSummaryLocked(SafeFileHandle root, CatalogFiles files, string generation)
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
        var primary = TryReadSummary(files.Primary);
        var secondary = primary is not null && TryMatchReplica(files.Backup, generation, primary.Value.DocumentHash)
            ? primary
            : TryReadSummary(files.Backup);
        if (primary is not null && secondary is not null &&
            !primary.Value.DocumentHash.AsSpan().SequenceEqual(secondary.Value.DocumentHash))
            throw new InvalidDataException("Ambas copias del catálogo son válidas pero diferentes.");
        var selected = primary ?? secondary ?? throw new InvalidDataException("Ninguna copia del catálogo es válida.");
        if (!selected.DocumentHash.AsSpan().SequenceEqual(commit.AsSpan(8, 32)) ||
            selected.Binding.Marker != binding.Marker ||
            !selected.Binding.Identity.AsSpan().SequenceEqual(binding.Identity))
            throw new InvalidDataException("El catálogo no corresponde a la confirmación o al destino.");
        return (selected.FileCount, primary is null || secondary is null);

        (DestinationBinding Binding, int FileCount, byte[] DocumentHash)? TryReadSummary(CatalogFile candidate)
        {
            try
            {
                using var file = WindowsProbeNative.OpenPlainFile(candidate.Parent, candidate.Name, FileAccess.Read, FileShare.Read);
                var (_, count, parsed) = ParseCore(file, generation, retainEntries: false);
                return (parsed.Binding, count, parsed.DocumentHash);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or Win32Exception or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static bool TryMatchReplica(CatalogFile candidate, string generation, byte[] expectedHash)
    {
        try
        {
            using var file = WindowsProbeNative.OpenPlainFile(candidate.Parent, candidate.Name, FileAccess.Read, FileShare.Read);
            return MatchesDocument(file, generation, expectedHash);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or Win32Exception or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static byte[] WriteMetadata(Stream output, BatchPlan plan, string generation = "initial") =>
        WriteMetadata(output, plan, generation, out _);

    internal static byte[] WriteMetadata(Stream output, BatchPlan plan, string generation, out int byteCount)
    {
        if (plan.Files.Length + plan.Directories.Length > EntryLimit(generation))
            throw new InvalidDataException("Demasiadas entradas en el catálogo.");
        int byteLimit = ByteLimit(generation);
        using var body = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var document = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int total = 0;
        int version = plan.Files.Length == 0 ? 1 : plan.Files.Max(file => file.CatalogVersion);
        Line(new Header("header", version, plan.Target.Marker, generation, plan.Target.Identity));
        foreach (string directory in plan.Directories.Order(StringComparer.Ordinal))
            Line(new DirectoryRecord("directory", directory));
        foreach (var file in plan.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            if (file.Metadata is not { } metadata)
                Line(new FileRecord("file", file.RelativePath, file.Length, file.Hash));
            else if (version == 4 && metadata.CreationUtc is { } zoneCreation && metadata.Hidden is { } zoneHidden)
                Line(new FileRecordV4("file", file.RelativePath, file.Length, file.Hash, metadata.LastWriteUtc, zoneCreation, zoneHidden, metadata.ZoneData, metadata.ZoneHash));
            else if (metadata.CreationUtc is { } creation && metadata.Hidden is { } hidden)
                Line(new FileRecordV3("file", file.RelativePath, file.Length, file.Hash, metadata.LastWriteUtc, creation, hidden));
            else
                Line(new DatedFileRecord("file", file.RelativePath, file.Length, file.Hash, metadata.LastWriteUtc));
        }
        Line(new Footer("footer", plan.Files.Length, plan.Directories.Length, body.GetHashAndReset()), footer: true);
        byteCount = total;
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
    internal static CatalogSnapshot Parse(Stream input, string generation = "initial") =>
        ParseCore(input, generation, retainEntries: true).Snapshot;

    private static (int DirectoryCount, int FileCount, CatalogSnapshot Snapshot) ParseCore(Stream input, string generation, bool retainEntries)
    {
        if (input.Length < 1 || input.Length > ByteLimit(generation))
            throw new InvalidDataException("Tamaño de catálogo no válido.");
        int entryLimit = EntryLimit(generation);
        using var body = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var document = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[MaximumLineBytes - 1];
        byte[] readBuffer = new byte[64 * 1024];
        int position = 0, available = 0;
        byte[] headerLine = Line() ?? throw new InvalidDataException("Falta la cabecera.");
        var header = Decode<Header>(headerLine);
        if (header.Kind != "header" || header.Version is not (1 or 2 or 3 or 4) || header.GenerationId != generation || header.JobId == Guid.Empty ||
            header.Identity is not { Length: 24 })
            throw new InvalidDataException("Cabecera no soportada.");
        HashBody(headerLine);
        var directories = retainEntries ? new List<string>() : null;
        var files = retainEntries ? new List<BatchFile>() : null;
        var summaryNames = retainEntries ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var summaryDirectories = retainEntries ? null : new HashSet<string>(StringComparer.Ordinal);
        int directoryCount = 0, fileCount = 0;
        int? firstCatalogVersion = null;
        long totalBytes = 0;
        string? lastDirectory = null;
        string? lastFile = null;
        while (true)
        {
            byte[] line = Line() ?? throw new InvalidDataException("Falta el cierre del catálogo.");
            string? kind = PeekKind(line);
            if (kind is null)
                throw new InvalidDataException("Registro sin tipo.");
            switch (kind)
            {
                case "directory":
                    var directory = Decode<DirectoryRecord>(line);
                    if (fileCount != 0 || directory.Path is null || lastDirectory is not null && string.CompareOrdinal(lastDirectory, directory.Path) >= 0)
                        throw new InvalidDataException("Orden de carpetas no válido.");
                    if (retainEntries) directories!.Add(directory.Path);
                    else
                    {
                        ValidateRelativeEntry(directory.Path, isDirectory: true, summaryNames!, summaryDirectories!);
                        summaryDirectories!.Add(directory.Path);
                    }
                    directoryCount++;
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
                    else if (header.Version == 3)
                    {
                        var entry = Decode<FileRecordV3>(line);
                        file = new(entry.Path, entry.Size, entry.Hash, new(entry.LastWriteUtc, entry.CreationUtc, entry.Hidden));
                    }
                    else
                    {
                        var entry = Decode<FileRecordV4>(line);
                        file = new(entry.Path, entry.Size, entry.Hash, new(entry.LastWriteUtc, entry.CreationUtc, entry.Hidden, entry.ZoneData, entry.ZoneHash));
                    }
                    if (file.RelativePath is null || lastFile is not null && string.CompareOrdinal(lastFile, file.RelativePath) >= 0)
                        throw new InvalidDataException("Orden de archivos no válido.");
                    if (retainEntries) files!.Add(file);
                    else
                    {
                        ValidateRelativeEntry(file.RelativePath, isDirectory: false, summaryNames!, summaryDirectories!);
                        if (file.Hash is not { Length: SHA256.HashSizeInBytes } || file.Length < 0)
                            throw new ArgumentException("Hash o tamaño de lote no válido.");
                        firstCatalogVersion ??= file.CatalogVersion;
                        if ((file.CatalogVersion < 3 || firstCatalogVersion.Value < 3) && file.CatalogVersion != firstCatalogVersion.Value ||
                            file.Metadata is { } metadata && !metadata.IsValid())
                            throw new InvalidDataException("Los archivos del lote tienen metadatos inválidos o de versiones distintas.");
                        try { totalBytes = checked(totalBytes + file.Length); }
                        catch (OverflowException error) { throw new ArgumentException("El tamaño total del lote no es válido.", error); }
                    }
                    fileCount++;
                    lastFile = file.RelativePath;
                    break;
                case "footer":
                    var footer = Decode<Footer>(line);
                    if (footer.Files != fileCount || footer.Directories != directoryCount || footer.Hash is not { Length: 32 } ||
                        !body.GetHashAndReset().AsSpan().SequenceEqual(footer.Hash) || Line() is not null)
                        throw new InvalidDataException("El cierre, recuento o hash del catálogo no es válido.");
                    if (!retainEntries)
                    {
                        foreach (string dir in summaryDirectories!)
                        {
                            string? parent = Path.GetDirectoryName(dir);
                            if (!string.IsNullOrEmpty(parent) && !summaryDirectories.Contains(parent))
                                throw new ArgumentException("Falta una carpeta padre en el lote.");
                        }
                        return (directoryCount, fileCount, new(
                            new DestinationBinding { Version = 1, Marker = header.JobId, Identity = header.Identity, LastVolumePath = "" },
                            [], [], document.GetHashAndReset()));
                    }
                    var validatedFiles = files!.ToArray();
                    var validatedDirectories = directories!.ToArray();
                    FolderBatch.ValidateEntries(validatedFiles, validatedDirectories, entryLimit);
                    return (directoryCount, fileCount, new(
                        new DestinationBinding { Version = 1, Marker = header.JobId, Identity = header.Identity, LastVolumePath = "" },
                        validatedDirectories, validatedFiles, document.GetHashAndReset()));
                default:
                    throw new InvalidDataException("Tipo de registro no soportado.");
            }
            if (directoryCount + fileCount > entryLimit)
                throw new InvalidDataException("Demasiadas entradas en el catálogo.");
            HashBody(line);
        }

        static void ValidateRelativeEntry(string relative, bool isDirectory, HashSet<string> names, HashSet<string> directoryNames)
        {
            _ = Utf8.GetByteCount(relative);
            if (Path.IsPathRooted(relative) || !names.Add(relative) || FolderBatch.IsControl(relative))
                throw new ArgumentException("Ruta de lote no soportada.");
            foreach (string component in relative.Split(Path.DirectorySeparatorChar))
            {
                DirectoryFence.CheckName(component);
                if (component is "." or "..")
                    throw new ArgumentException("Ruta de lote fuera de alcance.");
            }
            if (!isDirectory)
            {
                string? parent = Path.GetDirectoryName(relative);
                if (!string.IsNullOrEmpty(parent) && !directoryNames.Contains(parent))
                    throw new ArgumentException("Falta una carpeta padre en el lote.");
            }
            int components = relative.Count(character => character == Path.DirectorySeparatorChar) + 1;
            if (components > (isDirectory ? 64 : 65))
                throw new ArgumentException("La ruta supera el límite de profundidad.");
        }

        byte[]? Line()
        {
            int length = 0;
            while (true)
            {
                if (position == available)
                {
                    available = input.Read(readBuffer);
                    position = 0;
                    if (available == 0)
                    {
                        if (length != 0) throw new InvalidDataException("Línea truncada en el catálogo.");
                        return null;
                    }
                }
                var remaining = readBuffer.AsSpan(position, available - position);
                int newline = remaining.IndexOf((byte)10);
                var part = newline < 0 ? remaining : remaining[..newline];
                if (part.Length > buffer.Length - length || part.Contains((byte)13))
                    throw new InvalidDataException("Separador o tamaño de registro no válido.");
                position += part.Length;
                if (newline >= 0)
                {
                    position++;
                    if (length + part.Length == 0) throw new InvalidDataException("Línea vacía en el catálogo.");
                    byte[] bytes;
                    if (length == 0) bytes = part.ToArray();
                    else
                    {
                        part.CopyTo(buffer.AsSpan(length));
                        bytes = buffer.AsSpan(0, length + part.Length).ToArray();
                    }
                    _ = Utf8.GetCharCount(bytes);
                    document.AppendData(bytes);
                    document.AppendData([10]);
                    return bytes;
                }
                part.CopyTo(buffer.AsSpan(length));
                length += part.Length;
            }
        }
        void HashBody(ReadOnlySpan<byte> bytes)
        {
            body.AppendData(bytes);
            body.AppendData([10]);
        }
    }

    private static string? PeekKind(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = 16 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) break;
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                bool isKind = reader.ValueTextEquals("kind"u8);
                if (!reader.Read()) return null;
                if (isKind) return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                reader.Skip();
            }
        }
        return null;
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
        WindowsProbeNative.RequireSupportedData(file.SafeFileHandle, item.RelativePath, item.Metadata?.ZoneData is not null);
        if (file.Length != item.Length || !SHA256.HashData(file).AsSpan().SequenceEqual(item.Hash))
            throw new InvalidDataException("El contenido del respaldo no coincide con el catálogo.");
        item.Metadata?.RequireMatch(file.SafeFileHandle);
    }
}
