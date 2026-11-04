using System.Security.Cryptography;

// Disposable, fixed synthetic data shared by the console and WPF laboratories.
internal sealed class BatchFixture : IDisposable
{
    internal static readonly string[] SampleNames = ["muestra.txt", Path.Combine("Documentos á漢字", "notas.bin"), "vacío.bin"];
    internal static readonly string[] DirectoryNames = ["Documentos á漢字", "Carpeta vacía"];
    private readonly byte[] sourceIdentity;
    private readonly Dictionary<string, byte[]> expectedHashes = new(StringComparer.Ordinal);
    internal DestinationBinding DestinationBinding { get; }
    internal DestinationBinding RestoreBinding { get; }
    internal string Root { get; } = Directory.CreateTempSubdirectory("FolderTwin-Batch-").FullName;
    internal string Source => Path.Combine(Root, "Origen");
    internal string Destination => Path.Combine(Root, "Destino");
    internal string Restored => Path.Combine(Root, "Restaurado");
    internal string SourceFile => Path.Combine(Source, SampleNames[0]);
    internal string CopyFile => Path.Combine(Destination, SampleNames[0]);
    internal string RestoredFile => Path.Combine(Restored, SampleNames[0]);
    internal BatchPlan? Plan { get; private set; }

    internal BatchFixture()
    {
        foreach (string directory in new[] { Source, Destination, Restored })
            Directory.CreateDirectory(directory);
        foreach (string directory in DirectoryNames)
            Directory.CreateDirectory(Path.Combine(Source, directory));
        using (var source = new DirectoryFence(Source))
            sourceIdentity = WindowsProbeNative.DirectoryIdentity(source.Root);
        Seed(SampleNames[0], "FolderTwin · lote sintético de demostración.\n"u8.ToArray());
        byte[] content = new byte[65_537];
        new Random(42).NextBytes(content);
        Seed(SampleNames[1], content);
        Seed(SampleNames[2], []);
        using (var target = new DirectoryFence(Destination))
            DestinationBinding = global::DestinationBinding.Register(target.Root);
        using (var target = new DirectoryFence(Restored))
            RestoreBinding = global::DestinationBinding.Register(target.Root);
    }

    private void Seed(string relative, byte[] content)
    {
        File.WriteAllBytes(Path.Combine(Source, relative), content);
        expectedHashes.Add(relative, SHA256.HashData(content));
    }

    internal BatchPlan Analyze()
    {
        var plan = FolderBatch.Prepare(Root, Source, Destination, DestinationBinding);
        if (plan.Files.Length != expectedHashes.Count || plan.Files.Any(file => !expectedHashes.TryGetValue(file.RelativePath, out var hash) ||
            !file.Hash.AsSpan().SequenceEqual(hash)) || !plan.Directories.Order(StringComparer.Ordinal)
                .SequenceEqual(DirectoryNames.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Este laboratorio solo admite los archivos sintéticos generados para la sesión.");
        return Plan = plan;
    }
    internal BatchResult Copy() => FolderBatch.Run(Plan ?? throw new InvalidOperationException("Analiza las carpetas primero."));
    internal BatchResult Restore() => FolderBatch.Run((Plan ?? throw new InvalidOperationException("Analiza las carpetas primero."))
        .RestoreTo(Restored, RestoreBinding));

    internal void CheckRestored()
    {
        foreach (string relative in SampleNames)
            if (!SHA256.HashData(File.ReadAllBytes(Path.Combine(Restored, relative))).AsSpan()
                .SequenceEqual(Plan!.Files.Single(file => file.RelativePath == relative).Hash))
                throw new InvalidDataException("El archivo restaurado no coincide con el contenido analizado.");
        foreach (string directory in DirectoryNames)
            if (!Directory.Exists(Path.Combine(Restored, directory)))
                throw new InvalidDataException("Falta una carpeta restaurada.");
    }

    public void Dispose()
    {
        Clear(Source, sourceIdentity, null);
        Clear(Destination, DestinationBinding.Identity, DestinationBinding);
        Clear(Restored, RestoreBinding.Identity, RestoreBinding);
        Directory.Delete(Root);
    }

    private void Clear(string root, byte[] identity, DestinationBinding? binding)
    {
        using (var fence = new DirectoryFence(root))
        {
            if (!WindowsProbeNative.DirectoryIdentity(fence.Root).AsSpan().SequenceEqual(identity))
                throw new InvalidDataException("Una carpeta temporal cambió y requiere revisión.");
            using (binding?.OpenVerifiedMarker(fence.Root))
            {
                foreach (string relative in SampleNames)
                {
                    string path = Path.Combine(root, relative);
                    if (!Directory.Exists(Path.GetDirectoryName(path)))
                        continue;
                    using var parent = new DirectoryFence(root, path);
                    File.Delete(path);
                    if (binding is not null)
                    {
                        string temporary = FolderBatch.TemporaryPath(root, relative);
                        foreach (string file in new[] { temporary, CopyRecovery.LockPath(temporary),
                            CopyRecovery.ReceiptPath(temporary), CopyRecovery.ReceiptDraftPath(temporary) })
                            File.Delete(file);
                    }
                }
                if (binding is not null)
                    File.Delete(Path.Combine(root, FolderBatch.LockName));
            }
            if (binding is not null)
                File.Delete(Path.Combine(root, global::DestinationBinding.MarkerName));
        }
        foreach (string relative in DirectoryNames.Reverse())
            if (Directory.Exists(Path.Combine(root, relative)))
                Directory.Delete(Path.Combine(root, relative));
        Directory.Delete(root);
    }
}
