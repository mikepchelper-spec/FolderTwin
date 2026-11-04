using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal enum RecoveryResult
{
    NoReceipt,
    InvalidReceipt,
    WrongDirectory,
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
    private static ReadOnlySpan<byte> Magic => "FTREADY2"u8;
    private const int DataHashEnd = 8 + SHA256.HashSizeInBytes;
    private const int NameHashEnd = DataHashEnd + SHA256.HashSizeInBytes;
    private const int PayloadSize = NameHashEnd + 24;
    private const int ReceiptSize = PayloadSize + SHA256.HashSizeInBytes;

    public static string ReceiptPath(string temporary) => temporary + ".ready";
    public static string ReceiptDraftPath(string temporary) => ReceiptPath(temporary) + ".tmp";
    public static string LockPath(string temporary) => temporary + ".lock";

    public static FileStream Begin(SafeFileHandle directory, string temporary, string destination)
    {
        CheckParents(temporary, destination);
        var operation = WindowsProbeNative.OpenPlainFile(directory, LockPath(temporary), FileAccess.ReadWrite, FileShare.None,
            FileMode.OpenOrCreate);
        try
        {
            if (WindowsProbeNative.PlainFileExists(directory, ReceiptPath(temporary)) ||
                WindowsProbeNative.PlainFileExists(directory, ReceiptDraftPath(temporary)) ||
                WindowsProbeNative.PlainFileExists(directory, temporary) || WindowsProbeNative.PlainFileExists(directory, destination))
                throw new IOException("This operation already has files. Inspect recovery first.");
            return operation;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    public static void RecordVerified(string temporary, string destination, ReadOnlySpan<byte> hash, SafeFileHandle directory,
        Action<CopyStep, string>? checkpoint)
    {
        Span<byte> record = stackalloc byte[ReceiptSize];
        Magic.CopyTo(record);
        hash.CopyTo(record[Magic.Length..DataHashEnd]);
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(destination)), record[DataHashEnd..NameHashEnd]);
        WindowsProbeNative.DirectoryIdentity(directory).CopyTo(record[NameHashEnd..PayloadSize]);
        SHA256.HashData(record[..PayloadSize], record[PayloadSize..]);
        string draft = ReceiptDraftPath(temporary);
        using (var receipt = WindowsProbeNative.OpenPlainFile(directory, draft, FileAccess.Write, FileShare.None,
            FileMode.CreateNew, rename: true))
        {
            checkpoint?.Invoke(CopyStep.WritingReceipt, draft);
            receipt.Write(record);
            checkpoint?.Invoke(CopyStep.FlushingReceipt, draft);
            receipt.Flush(flushToDisk: true);
            checkpoint?.Invoke(CopyStep.ReceiptFlushed, draft);
            WindowsProbeNative.RenameNew(receipt.SafeFileHandle, directory, Path.GetFileName(ReceiptPath(temporary)));
        }
    }

    public static RecoveryResult Recover(string root, string temporary, string destination, DestinationBinding? binding = null,
        byte[]? expectedHash = null)
    {
        CheckParents(temporary, destination);
        using var fence = new DirectoryFence(root, temporary, destination);
        var directory = fence.Parent(destination);
        using var marker = binding?.OpenVerifiedMarker(directory);
        string receiptPath = ReceiptPath(temporary);
        if (!WindowsProbeNative.PlainFileExists(directory, LockPath(temporary)))
            return WindowsProbeNative.PlainFileExists(directory, receiptPath) ? RecoveryResult.InvalidReceipt : RecoveryResult.NoReceipt;
        using var operation = WindowsProbeNative.OpenPlainFile(directory, LockPath(temporary), FileAccess.ReadWrite, FileShare.None);
        if (!WindowsProbeNative.PlainFileExists(directory, receiptPath))
            return RecoveryResult.NoReceipt;

        Span<byte> record = stackalloc byte[ReceiptSize];
        using (var receipt = WindowsProbeNative.OpenPlainFile(directory, receiptPath, FileAccess.Read, FileShare.Read))
        {
            if (receipt.ReadAtLeast(record, record.Length, throwOnEndOfStream: false) != record.Length ||
                receipt.ReadByte() != -1 ||
                !record[..Magic.Length].SequenceEqual(Magic) ||
                !SHA256.HashData(record[..PayloadSize]).AsSpan().SequenceEqual(record[PayloadSize..]) ||
                !SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(destination))).AsSpan()
                    .SequenceEqual(record[DataHashEnd..NameHashEnd]))
                return RecoveryResult.InvalidReceipt;
        }
        if (!WindowsProbeNative.DirectoryIdentity(directory).AsSpan().SequenceEqual(record[NameHashEnd..PayloadSize]))
            return RecoveryResult.WrongDirectory;
        if (expectedHash is not null && !record[Magic.Length..DataHashEnd].SequenceEqual(expectedHash))
            return RecoveryResult.HashMismatch;

        bool hasTemporary = WindowsProbeNative.PlainFileExists(directory, temporary);
        bool hasFinal = WindowsProbeNative.PlainFileExists(directory, destination);
        if (hasTemporary && hasFinal)
            return RecoveryResult.Conflict;
        if (!hasTemporary && !hasFinal)
            return RecoveryResult.MissingData;

        using var data = WindowsProbeNative.OpenPlainFile(directory, hasTemporary ? temporary : destination,
            FileAccess.Read, FileShare.Read, rename: hasTemporary);
        if (!SHA256.HashData(data).AsSpan().SequenceEqual(record[Magic.Length..DataHashEnd]))
            return RecoveryResult.HashMismatch;

        if (hasFinal)
            return RecoveryResult.AlreadyPublished;

        WindowsProbeNative.RenameNew(data.SafeFileHandle, directory, Path.GetFileName(destination));
        return RecoveryResult.Published;
    }

    private static void CheckParents(string temporary, string destination)
    {
        if (Path.GetDirectoryName(temporary) != Path.GetDirectoryName(destination))
            throw new ArgumentException("Recovery requires both files in the same directory.");
    }

}
