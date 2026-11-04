using System.Security.Cryptography;

internal enum CopyStep { Written, Flushed, Verified, Published }

internal static class VerifiedCopy
{
    // The caller supplies paths inside a fresh synthetic fixture on one volume.
    public static void Run(
        string source, string destination, string temporary,
        Action<CopyStep, string>? checkpoint = null, byte[]? expectedHash = null, bool recoverable = false)
    {
        using var operation = recoverable ? CopyRecovery.Begin(temporary, destination) : null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] buffer = new byte[64 * 1024];
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                output.Write(buffer.AsSpan(0, count));
                hash.AppendData(buffer.AsSpan(0, count));
            }
            checkpoint?.Invoke(CopyStep.Written, temporary);
            output.Flush(flushToDisk: true);
        }
        checkpoint?.Invoke(CopyStep.Flushed, temporary);

        byte[] sourceHash = hash.GetHashAndReset();
        using (var input = File.OpenRead(temporary))
        {
            if (!SHA256.HashData(input).AsSpan().SequenceEqual(sourceHash) ||
                (expectedHash is not null && !sourceHash.AsSpan().SequenceEqual(expectedHash)))
                throw new InvalidDataException("The copy does not match the expected SHA-256.");
            if (recoverable)
                CopyRecovery.RecordVerified(temporary, destination, sourceHash);
        }
        checkpoint?.Invoke(CopyStep.Verified, temporary);
        File.Move(temporary, destination);
        checkpoint?.Invoke(CopyStep.Published, destination);
    }
}
