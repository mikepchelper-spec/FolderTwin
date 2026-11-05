using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

internal enum CopyStep { Writing, Written, Flushing, Flushed, WritingReceipt, FlushingReceipt, ReceiptFlushed, Verified, Published }

internal static class VerifiedCopy
{
    // The caller supplies paths inside a fresh synthetic fixture on one volume.
    public static void Run(
        string source, string destination, string temporary,
        Action<CopyStep, string>? checkpoint = null, byte[]? expectedHash = null, string? recoveryRoot = null,
        CancellationToken cancellationToken = default, DestinationBinding? binding = null, FileMetadata? metadata = null,
        bool resumeUnverifiedPrefix = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var fence = recoveryRoot is null ? null : new DirectoryFence(recoveryRoot, source, temporary, destination);
        Copy(source, destination, temporary, fence?.Parent(source), fence?.Parent(destination),
            checkpoint, expectedHash, cancellationToken, binding, metadata, resumeUnverifiedPrefix);
    }

    internal static void RunBound(LocalJobRoots roots, DestinationBinding binding, string sourceRelative, string destinationRelative,
        string temporaryRelative, byte[] expectedHash, Action<CopyStep, string>? checkpoint = null,
        CancellationToken cancellationToken = default, FileMetadata? metadata = null, bool resumeUnverifiedPrefix = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Path.IsPathRooted(sourceRelative) || Path.IsPathRooted(destinationRelative) || Path.IsPathRooted(temporaryRelative))
            throw new ArgumentException("El trabajo solo admite rutas relativas a sus carpetas verificadas.");
        using var marker = binding.OpenVerifiedMarker(roots.DestinationHandle);
        string source = Path.Combine(roots.Review.Source.VolumePath, sourceRelative);
        string destination = Path.Combine(roots.Review.Destination.VolumePath, destinationRelative);
        string temporary = Path.Combine(roots.Review.Destination.VolumePath, temporaryRelative);
        using var origin = new DirectoryFence(roots.Review.Source.VolumePath, source);
        using var target = new DirectoryFence(roots.Review.Destination.VolumePath, destination, temporary);
        if (!WindowsProbeNative.DirectoryIdentity(origin.Root).AsSpan().SequenceEqual(roots.Review.Source.Identity))
            throw new InvalidDataException("La raíz de origen cambió desde la revisión.");
        binding.RequireDirectoryIdentity(target.Root);
        Copy(source, destination, temporary, origin.Parent(source), target.Parent(destination),
            checkpoint, expectedHash, cancellationToken, binding: null, metadata, resumeUnverifiedPrefix);
    }

    private static void Copy(string source, string destination, string temporary,
        SafeFileHandle? sourceDirectory, SafeFileHandle? directory, Action<CopyStep, string>? checkpoint,
        byte[]? expectedHash, CancellationToken cancellationToken, DestinationBinding? binding, FileMetadata? metadata,
        bool resumeUnverifiedPrefix)
    {
        if (resumeUnverifiedPrefix && (directory is null || expectedHash is not { Length: 32 }))
            throw new ArgumentException("Continuar un temporal requiere una raíz comprobada y el SHA-256 esperado.");
        using var marker = binding?.OpenVerifiedMarker(
            directory ?? throw new ArgumentException("A configured copy requires a trusted fixture root."));
        using var operation = directory is null ? null : CopyRecovery.Begin(directory, temporary, destination, resumeUnverifiedPrefix);
        bool partial = resumeUnverifiedPrefix && WindowsProbeNative.PlainFileExists(directory!, temporary);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var input = sourceDirectory is null
            ? new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read)
            : WindowsProbeNative.OpenPlainFile(sourceDirectory, source, FileAccess.Read, FileShare.Read))
        {
            if (sourceDirectory is not null)
                WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, Path.GetFileName(source));
            if (partial)
            {
                // Keep the source handle denying writers throughout prefix validation
                // and append. Never mutate a partial using a changed source.
                if (!SHA256.HashData(input).AsSpan().SequenceEqual(expectedHash))
                    throw new InvalidDataException("El origen ya no coincide con el contenido esperado del temporal.");
                input.Position = 0;
            }
            using var output = directory is null
                ? new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                : WindowsProbeNative.OpenPlainFile(directory, temporary, partial ? FileAccess.ReadWrite : FileAccess.Write,
                    FileShare.None, partial ? FileMode.Open : FileMode.CreateNew);
            byte[] buffer = new byte[64 * 1024];
            if (partial)
            {
                WindowsProbeNative.RequireSupportedData(output.SafeFileHandle, Path.GetFileName(temporary));
                if (output.Length > input.Length) throw new InvalidDataException("El temporal excede el contenido esperado; se conserva.");
                byte[] existing = new byte[buffer.Length];
                long remaining = output.Length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int take = (int)Math.Min(buffer.Length, remaining);
                    input.ReadExactly(buffer.AsSpan(0, take));
                    output.ReadExactly(existing.AsSpan(0, take));
                    if (!buffer.AsSpan(0, take).SequenceEqual(existing.AsSpan(0, take)))
                        throw new InvalidDataException("El temporal no coincide con el prefijo esperado; se conserva sin cambios.");
                    hash.AppendData(buffer.AsSpan(0, take));
                    remaining -= take;
                }
            }
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
            if (metadata is not null)
            {
                output.Flush();
                metadata.Apply(output.SafeFileHandle);
            }
            output.Flush(flushToDisk: true);
            if (sourceDirectory is not null)
                WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, Path.GetFileName(source));
        }
        checkpoint?.Invoke(CopyStep.Flushed, temporary);

        byte[] sourceHash = hash.GetHashAndReset();
        using (var input = directory is null
            ? File.OpenRead(temporary)
            : WindowsProbeNative.OpenPlainFile(directory, temporary, FileAccess.Read, FileShare.Read, rename: true,
                writeAttributes: metadata?.CreationUtc is not null))
        {
            if (directory is not null)
                WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, Path.GetFileName(temporary));
            if (!SHA256.HashData(input).AsSpan().SequenceEqual(sourceHash) ||
                (expectedHash is not null && !sourceHash.AsSpan().SequenceEqual(expectedHash)))
                throw new InvalidDataException("The copy does not match the expected SHA-256.");
            metadata?.RequireMatch(input.SafeFileHandle);
            cancellationToken.ThrowIfCancellationRequested();
            if (directory is not null)
                CopyRecovery.RecordVerified(temporary, destination, sourceHash, directory, checkpoint);
            checkpoint?.Invoke(CopyStep.Verified, temporary);
            if (directory is not null)
            {
                WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, Path.GetFileName(temporary));
                metadata?.RequireMatch(input.SafeFileHandle);
                CopyRecovery.PublishData(input, directory, destination, metadata);
            }
        }
        if (directory is null)
            File.Move(temporary, destination);
        checkpoint?.Invoke(CopyStep.Published, destination);
    }
}
