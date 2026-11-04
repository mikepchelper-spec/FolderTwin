using System.Security.Cryptography;

internal enum CopyStep { Writing, Written, Flushing, Flushed, WritingReceipt, FlushingReceipt, ReceiptFlushed, Verified, Published }

internal static class VerifiedCopy
{
    // The caller supplies paths inside a fresh synthetic fixture on one volume.
    public static void Run(
        string source, string destination, string temporary,
        Action<CopyStep, string>? checkpoint = null, byte[]? expectedHash = null, string? recoveryRoot = null,
        CancellationToken cancellationToken = default, DestinationBinding? binding = null, DateTime? lastWriteUtc = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var fence = recoveryRoot is null ? null : new DirectoryFence(recoveryRoot, source, temporary, destination);
        var directory = fence?.Parent(destination);
        using var marker = binding?.OpenVerifiedMarker(
            directory ?? throw new ArgumentException("A configured copy requires a trusted fixture root."));
        using var operation = directory is null ? null : CopyRecovery.Begin(directory, temporary, destination);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var input = fence is null
            ? new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read)
            : WindowsProbeNative.OpenPlainFile(fence.Parent(source), source, FileAccess.Read, FileShare.Read))
        {
            if (fence is not null)
                WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, Path.GetFileName(source));
            using var output = directory is null
                ? new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                : WindowsProbeNative.OpenPlainFile(directory, temporary, FileAccess.Write, FileShare.None, FileMode.CreateNew);
            byte[] buffer = new byte[64 * 1024];
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                checkpoint?.Invoke(CopyStep.Writing, temporary);
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer.AsSpan(0, count));
                hash.AppendData(buffer.AsSpan(0, count));
            }
            checkpoint?.Invoke(CopyStep.Written, temporary);
            cancellationToken.ThrowIfCancellationRequested();
            checkpoint?.Invoke(CopyStep.Flushing, temporary);
            if (lastWriteUtc is { } time)
            {
                output.Flush();
                File.SetLastWriteTimeUtc(output.SafeFileHandle, time);
            }
            output.Flush(flushToDisk: true);
            if (fence is not null)
                WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, Path.GetFileName(source));
        }
        checkpoint?.Invoke(CopyStep.Flushed, temporary);

        byte[] sourceHash = hash.GetHashAndReset();
        using (var input = directory is null
            ? File.OpenRead(temporary)
            : WindowsProbeNative.OpenPlainFile(directory, temporary, FileAccess.Read, FileShare.Read, rename: true))
        {
            if (directory is not null)
                WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, Path.GetFileName(temporary));
            if (!SHA256.HashData(input).AsSpan().SequenceEqual(sourceHash) ||
                (expectedHash is not null && !sourceHash.AsSpan().SequenceEqual(expectedHash)))
                throw new InvalidDataException("The copy does not match the expected SHA-256.");
            RequireLastWriteTime(input, lastWriteUtc);
            cancellationToken.ThrowIfCancellationRequested();
            if (directory is not null)
                CopyRecovery.RecordVerified(temporary, destination, sourceHash, directory, checkpoint);
            checkpoint?.Invoke(CopyStep.Verified, temporary);
            if (directory is not null)
            {
                WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, Path.GetFileName(temporary));
                RequireLastWriteTime(input, lastWriteUtc);
                WindowsProbeNative.RenameNew(input.SafeFileHandle, directory, Path.GetFileName(destination));
            }
        }
        if (directory is null)
            File.Move(temporary, destination);
        checkpoint?.Invoke(CopyStep.Published, destination);
    }

    internal static void RequireLastWriteTime(FileStream file, DateTime? expected)
    {
        if (expected is { } time && File.GetLastWriteTimeUtc(file.SafeFileHandle) != time)
            throw new InvalidDataException("La fecha de modificación del archivo no coincide con la registrada.");
    }
}
