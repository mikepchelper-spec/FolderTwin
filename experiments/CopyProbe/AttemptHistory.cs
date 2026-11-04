using System.Text.Json;
using System.Text.Json.Serialization;

internal enum AttemptIssueKind { InUse, Cancelled, Review }
internal enum AttemptOperation { Copy, Restore, Verify }
internal sealed record AttemptIssue(string Path, AttemptIssueKind Kind);
internal sealed record AttemptRecord(DateTimeOffset FinishedAt, bool Restore, bool Complete, int VerifiedFiles,
    bool HasProblem, bool Degraded, AttemptIssue[] Issues, bool Verify = false, bool Cancelled = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int OmittedIssues = 0);
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

    internal static void Append(string sessionRoot, DestinationBinding binding, AttemptOperation operation, BatchResult result,
        string? destination = null)
    {
        var issues = result.Entries.Where(entry => !entry.Succeeded && SafeRelativePath(entry.RelativePath)).Take(12)
            .Select(entry => new AttemptIssue(entry.RelativePath, entry.InUse ? AttemptIssueKind.InUse :
                entry.Status == BatchStatus.Cancelled ? AttemptIssueKind.Cancelled : AttemptIssueKind.Review)).ToArray();
        int failureCount = result.Entries.Count(entry => !entry.Succeeded);
        var record = new AttemptRecord(DateTimeOffset.UtcNow, operation == AttemptOperation.Restore,
            result.Complete, result.VerifiedFiles, result.Problem is not null, result.Note is not null, issues,
            Verify: operation == AttemptOperation.Verify, Cancelled: result.Cancelled, OmittedIssues: failureCount - issues.Length);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        while (bytes.Length >= MaxRecordBytes && record.Issues.Length != 0)
        {
            int keep = record.Issues.Length / 2;
            record = record with { Issues = record.Issues[..keep], OmittedIssues = failureCount - keep };
            bytes = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        }

        string markerPath = Path.Combine(destination ?? Path.Combine(sessionRoot, "Destino"), DestinationBinding.MarkerName);
        using var root = new DirectoryFence(sessionRoot, markerPath);
        using var marker = binding.OpenVerifiedMarker(root.Parent(markerPath));
        using var file = WindowsProbeNative.OpenPlainFile(root.Root, HistoryName(binding, destination), FileAccess.ReadWrite,
            FileShare.None, FileMode.OpenOrCreate);
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

    internal static AttemptLog Read(string sessionRoot, DestinationBinding binding, string? destination = null)
    {
        string markerPath = Path.Combine(destination ?? Path.Combine(sessionRoot, "Destino"), DestinationBinding.MarkerName);
        using var root = new DirectoryFence(sessionRoot, markerPath);
        using var marker = binding.OpenVerifiedMarker(root.Parent(markerPath));
        string name = HistoryName(binding, destination);
        if (!WindowsProbeNative.PlainFileExists(root.Root, name))
            return new([], 0);
        using var file = WindowsProbeNative.OpenPlainFile(root.Root, name, FileAccess.Read, FileShare.Read);
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
            return record is { Issues: not null, VerifiedFiles: >= 0 and <= 10_000, OmittedIssues: >= 0 and <= 10_000 } &&
                !(record.Restore && record.Verify) &&
                record.Issues.Length <= 12 &&
                (!record.Complete || !record.HasProblem && !record.Cancelled && record.Issues.Length == 0 && record.OmittedIssues == 0) &&
                record.Issues.All(issue => issue is not null && SafeRelativePath(issue.Path) && Enum.IsDefined(issue.Kind))
                ? record : null;
        }
        catch (JsonException) { return null; }
    }

    private static string HistoryName(DestinationBinding binding, string? destination) =>
        destination is null ? Name : $"{Name}.{binding.Marker:N}";

    private static bool SafeRelativePath(string? path)
    {
        if (path is null || path.Length is 0 or > 1024)
            return false;
        try
        {
            foreach (string component in path.Split(Path.DirectorySeparatorChar))
            {
                DirectoryFence.CheckName(component);
                if (component is "." or "..")
                    return false;
            }
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
