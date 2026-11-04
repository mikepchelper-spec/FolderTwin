using System.Text.Json;
using System.Text.Json.Serialization;

internal enum AttemptIssueKind { InUse, Cancelled, Review }
internal enum AttemptOperation { Copy, Restore, Verify }
internal sealed record AttemptIssue(string Path, AttemptIssueKind Kind);
internal sealed record AttemptRecord(DateTimeOffset FinishedAt, bool Restore, bool Complete, int VerifiedFiles,
    bool HasProblem, bool Degraded, AttemptIssue[] Issues, bool Verify = false, bool Cancelled = false);
internal sealed record AttemptLog(AttemptRecord[] Records, int Unreadable);

// Diagnostic only. Receipts and catalog confirmation remain authoritative.
internal static class AttemptHistory
{
    internal const string Name = ".foldertwin-history-v1.jsonl";
    private const int MaxBytes = 256 * 1024;
    private const int MaxRecordBytes = 4096;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    internal static void Append(string sessionRoot, DestinationBinding binding, AttemptOperation operation, BatchResult result)
    {
        var issues = result.Entries.Where(entry => !entry.Succeeded && KnownPath(entry.RelativePath))
            .Select(entry => new AttemptIssue(entry.RelativePath, entry.InUse ? AttemptIssueKind.InUse :
                entry.Status == BatchStatus.Cancelled ? AttemptIssueKind.Cancelled : AttemptIssueKind.Review)).ToArray();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new AttemptRecord(DateTimeOffset.UtcNow, operation == AttemptOperation.Restore,
            result.Complete, result.VerifiedFiles, result.Problem is not null, result.Note is not null, issues,
            Verify: operation == AttemptOperation.Verify, Cancelled: result.Cancelled), Json);
        if (bytes.Length >= MaxRecordBytes)
            throw new InvalidDataException("El registro supera el tamaño permitido.");

        string markerPath = Path.Combine(sessionRoot, "Destino", DestinationBinding.MarkerName);
        using var root = new DirectoryFence(sessionRoot, markerPath);
        using var marker = binding.OpenVerifiedMarker(root.Parent(markerPath));
        using var file = WindowsProbeNative.OpenPlainFile(root.Root, Name, FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
        if (file.Length + bytes.Length + 3 > MaxBytes)
            throw new InvalidDataException("El historial alcanzó su límite de tamaño.");
        if (file.Length != 0)
        {
            file.Seek(-1, SeekOrigin.End);
            // A torn line remains visibly invalid. Never trim or overwrite older records.
            if (file.ReadByte() != '\n')
                file.Write("!\n"u8);
        }
        file.Write(bytes);
        file.WriteByte((byte)'\n');
        file.Flush(flushToDisk: true);
    }

    internal static AttemptLog Read(string sessionRoot, DestinationBinding binding)
    {
        string markerPath = Path.Combine(sessionRoot, "Destino", DestinationBinding.MarkerName);
        using var root = new DirectoryFence(sessionRoot, markerPath);
        using var marker = binding.OpenVerifiedMarker(root.Parent(markerPath));
        if (!WindowsProbeNative.PlainFileExists(root.Root, Name))
            return new([], 0);
        using var file = WindowsProbeNative.OpenPlainFile(root.Root, Name, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxBytes)
            throw new InvalidDataException("El historial supera el tamaño permitido.");
        byte[] bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);

        var records = new List<AttemptRecord>();
        int unreadable = 0;
        var remaining = bytes.AsSpan();
        while (!remaining.IsEmpty)
        {
            int end = remaining.IndexOf((byte)'\n');
            if (end < 0)
            {
                unreadable++;
                break;
            }
            if (end is > 0 and < MaxRecordBytes && TryRead(remaining[..end]) is { } record)
                records.Add(record);
            else
                unreadable++;
            remaining = remaining[(end + 1)..];
        }
        records.Reverse();
        return new(records.ToArray(), unreadable);
    }

    private static AttemptRecord? TryRead(ReadOnlySpan<byte> line)
    {
        try
        {
            var record = JsonSerializer.Deserialize<AttemptRecord>(line, Json);
            return record is { Issues: not null } && record.VerifiedFiles >= 0 && record.VerifiedFiles <= BatchFixture.MaximumSampleFiles &&
                !(record.Restore && record.Verify) &&
                record.Issues.Length <= BatchFixture.MaximumSampleFiles + BatchFixture.DirectoryNames.Length &&
                (!record.Complete || record.VerifiedFiles >= BatchFixture.SampleNames.Length &&
                    !record.HasProblem && !record.Cancelled && record.Issues.Length == 0) &&
                record.Issues.All(issue => issue is not null && KnownPath(issue.Path) && Enum.IsDefined(issue.Kind))
                ? record : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool KnownPath(string path) => BatchFixture.IsSamplePath(path);
}
