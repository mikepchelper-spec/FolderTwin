using System.IO;
using System.Security.Cryptography;

namespace CopyLab;

internal sealed class LabSession : IDisposable
{
    private static ReadOnlySpan<byte> Sample => "FolderTwin · archivo sintético de demostración.\n"u8;
    private const string SampleName = "muestra.txt";
    private const string PendingName = "muestra.tmp";
    private readonly DestinationBinding destination;
    private readonly DestinationBinding restoration;
    private readonly byte[] sourceIdentity;

    public string Root { get; } = Directory.CreateTempSubdirectory("FolderTwin-CopyLab-").FullName;
    public string Source => Path.Combine(Root, "Origen");
    public string Destination => Path.Combine(Root, "Destino");
    public string Restored => Path.Combine(Root, "Restaurado");
    public string SourceFile => Path.Combine(Source, SampleName);
    public string CopyFile => Path.Combine(Destination, SampleName);
    public string RestoredFile => Path.Combine(Restored, SampleName);

    public LabSession()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Destination);
        Directory.CreateDirectory(Restored);
        using (var source = new DirectoryFence(Source))
        {
            sourceIdentity = WindowsProbeNative.DirectoryIdentity(source.Root);
            using var sample = WindowsProbeNative.OpenPlainFile(source.Root, SampleName, FileAccess.Write,
                FileShare.None, FileMode.CreateNew);
            sample.Write(Sample);
            sample.Flush(flushToDisk: true);
        }
        using (var directory = new DirectoryFence(Destination))
            destination = DestinationBinding.Register(directory.Root);
        using (var directory = new DirectoryFence(Restored))
            restoration = DestinationBinding.Register(directory.Root);
    }

    public void Copy()
    {
        using var source = new DirectoryFence(Source);
        if (!WindowsProbeNative.DirectoryIdentity(source.Root).AsSpan().SequenceEqual(sourceIdentity))
            throw new InvalidDataException("La carpeta de origen cambió.");
        VerifiedCopy.Run(SourceFile, CopyFile, Path.Combine(Destination, PendingName),
            expectedHash: SHA256.HashData(Sample), recoveryRoot: Root, binding: destination);
    }

    public void Restore()
    {
        using var backup = new DirectoryFence(Destination);
        using var marker = destination.OpenVerifiedMarker(backup.Root);
        VerifiedCopy.Run(CopyFile, RestoredFile, Path.Combine(Restored, PendingName),
            expectedHash: SHA256.HashData(Sample), recoveryRoot: Root, binding: restoration);
    }

    public void Dispose()
    {
        Clear(Source, sourceIdentity, null);
        Clear(Destination, destination.Identity, destination);
        Clear(Restored, restoration.Identity, restoration);
        Directory.Delete(Root);
    }

    private static void Clear(string path, byte[] identity, DestinationBinding? binding)
    {
        using (var fence = new DirectoryFence(path))
        {
            if (!WindowsProbeNative.DirectoryIdentity(fence.Root).AsSpan().SequenceEqual(identity))
                throw new InvalidDataException("Una carpeta temporal cambió y requiere revisión.");
            using (binding?.OpenVerifiedMarker(fence.Root))
            {
                File.Delete(Path.Combine(path, SampleName));
                if (binding is not null)
                {
                    string pending = Path.Combine(path, PendingName);
                    foreach (string file in new[] { pending, CopyRecovery.ReceiptPath(pending),
                        CopyRecovery.ReceiptDraftPath(pending), CopyRecovery.LockPath(pending) })
                        File.Delete(file);
                }
            }
            if (binding is not null)
                File.Delete(Path.Combine(path, DestinationBinding.MarkerName));
        }
        Directory.Delete(path);
    }
}
