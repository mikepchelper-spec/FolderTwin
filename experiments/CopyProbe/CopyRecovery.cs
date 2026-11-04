using System.Security.Cryptography;
using System.Text;

internal enum RecoveryResult
{
    NoReceipt,
    InvalidReceipt,
    MissingData,
    Conflict,
    HashMismatch,
    Published,
    AlreadyPublished
}

// Single-file protocol for a dedicated synthetic directory. All callers for this
// operation use the same temporary and final paths and cooperate with its lock.
internal static class CopyRecovery
{
    private static ReadOnlySpan<byte> Magic => "FTREADY1"u8;
    private const int DataHashEnd = 8 + SHA256.HashSizeInBytes;
    private const int PayloadSize = DataHashEnd + SHA256.HashSizeInBytes;
    private const int ReceiptSize = PayloadSize + SHA256.HashSizeInBytes;

    public static string ReceiptPath(string temporary) => temporary + ".ready";
    public static string LockPath(string temporary) => temporary + ".lock";

    public static FileStream Begin(string temporary, string destination)
    {
        var operation = Acquire(temporary, destination);
        try
        {
            if (IsRegularFile(ReceiptPath(temporary)) || IsRegularFile(temporary) || IsRegularFile(destination))
                throw new IOException("This operation already has files. Inspect recovery first.");
            return operation;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    public static void RecordVerified(string temporary, string destination, ReadOnlySpan<byte> hash)
    {
        Span<byte> record = stackalloc byte[ReceiptSize];
        Magic.CopyTo(record);
        hash.CopyTo(record[Magic.Length..DataHashEnd]);
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(destination)), record[DataHashEnd..PayloadSize]);
        SHA256.HashData(record[..PayloadSize], record[PayloadSize..]);
        using var receipt = new FileStream(ReceiptPath(temporary), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        receipt.Write(record);
        receipt.Flush(flushToDisk: true);
    }

    public static RecoveryResult Recover(string temporary, string destination)
    {
        using var operation = Acquire(temporary, destination);
        string receiptPath = ReceiptPath(temporary);
        if (!IsRegularFile(receiptPath))
            return RecoveryResult.NoReceipt;

        Span<byte> record = stackalloc byte[ReceiptSize];
        using (var receipt = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (receipt.ReadAtLeast(record, record.Length, throwOnEndOfStream: false) != record.Length ||
                receipt.ReadByte() != -1 ||
                !record[..Magic.Length].SequenceEqual(Magic) ||
                !SHA256.HashData(record[..PayloadSize]).AsSpan().SequenceEqual(record[PayloadSize..]) ||
                !SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(destination))).AsSpan()
                    .SequenceEqual(record[DataHashEnd..PayloadSize]))
                return RecoveryResult.InvalidReceipt;
        }

        bool hasTemporary = IsRegularFile(temporary);
        bool hasFinal = IsRegularFile(destination);
        if (hasTemporary && hasFinal)
            return RecoveryResult.Conflict;
        if (!hasTemporary && !hasFinal)
            return RecoveryResult.MissingData;

        using (var data = new FileStream(hasTemporary ? temporary : destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            if (!SHA256.HashData(data).AsSpan().SequenceEqual(record[Magic.Length..DataHashEnd]))
                return RecoveryResult.HashMismatch;

        if (hasFinal)
            return RecoveryResult.AlreadyPublished;

        File.Move(temporary, destination);
        return RecoveryResult.Published;
    }

    private static FileStream Acquire(string temporary, string destination)
    {
        if (Path.GetDirectoryName(temporary) != Path.GetDirectoryName(destination))
            throw new ArgumentException("Recovery requires both files in the same directory.");
        string path = LockPath(temporary);
        IsRegularFile(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("An operation path is a directory or reparse point.");
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }
}
