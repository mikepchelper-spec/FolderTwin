using System.Security.Cryptography;

// Disposable, fixed synthetic data shared by the console and WPF laboratories.
internal sealed class BatchFixture : IDisposable
{
    internal const string Prefix = "FolderTwin-Batch-";
    internal static readonly string[] SampleNames = ["muestra.txt", Path.Combine("Documentos á漢字", "notas.bin"), "vacío.bin"];
    internal static readonly string[] DirectoryNames = ["Documentos á漢字", "Carpeta vacía"];
    private readonly byte[] sourceIdentity;
    private static byte[][] Samples()
    {
        byte[] content = new byte[65_537];
        new Random(42).NextBytes(content);
        return ["FolderTwin · lote sintético de demostración.\n"u8.ToArray(), content, []];
    }

    private static readonly BatchFile[] ExpectedFiles = Samples().Select((content, index) =>
        new BatchFile(SampleNames[index], content.LongLength, SHA256.HashData(content))).ToArray();
    internal DestinationBinding DestinationBinding { get; }
    internal DestinationBinding RestoreBinding { get; }
    internal string Root { get; } = Directory.CreateTempSubdirectory(Prefix).FullName;
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
        byte[][] samples = Samples();
        for (int index = 0; index < samples.Length; index++)
            File.WriteAllBytes(Path.Combine(Source, SampleNames[index]), samples[index]);
        using (var target = new DirectoryFence(Destination))
            DestinationBinding = global::DestinationBinding.Register(target.Root);
        using (var target = new DirectoryFence(Restored))
            RestoreBinding = global::DestinationBinding.Register(target.Root);
    }

    internal BatchPlan Analyze()
    {
        var plan = FolderBatch.Prepare(Root, Source, Destination, DestinationBinding);
        ValidateSamples(plan.Files, plan.Directories);
        return Plan = plan;
    }

    internal static void ValidateSamples(BatchFile[] files, string[] directories)
    {
        if (files.Length != ExpectedFiles.Length || !ExpectedFiles.All(expected => files.Any(file =>
            file.RelativePath == expected.RelativePath && file.Length == expected.Length && file.Hash.AsSpan().SequenceEqual(expected.Hash))) ||
            !directories.Order(StringComparer.Ordinal).SequenceEqual(DirectoryNames.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Este laboratorio solo admite los archivos sintéticos generados para la sesión.");
    }
    internal BatchResult Copy(CancellationToken cancellationToken = default, Action<string, CopyStep, string>? checkpoint = null,
        IProgress<BatchProgress>? progress = null, Action<BatchProgress>? waitBetweenFiles = null)
    {
        return Plan is null ? PendingBatch.Resume(Root, Destination, cancellationToken, checkpoint, finishCurrentFileOnCancel: true,
                progress: progress, waitBetweenFiles: waitBetweenFiles)
            : PendingBatch.Start(Plan, cancellationToken, checkpoint, progress, waitBetweenFiles);
    }
    internal BatchResult Restore(IProgress<BatchProgress>? progress = null)
    {
        var catalog = BatchCatalog.Read(Destination);
        var result = FolderBatch.Run(catalog.RestoreTo(Root, Destination, Restored, RestoreBinding), progress: progress);
        return catalog.Degraded ? result with { Note = "Se usó una réplica íntegra. La otra copia del catálogo necesita revisión." } : result;
    }
    internal void ForgetPlan() => Plan = null;

    internal void CheckRestored()
    {
        foreach (var file in ExpectedFiles)
            if (!SHA256.HashData(File.ReadAllBytes(Path.Combine(Restored, file.RelativePath))).AsSpan().SequenceEqual(file.Hash))
                throw new InvalidDataException("El archivo restaurado no coincide con el contenido analizado.");
        foreach (string directory in DirectoryNames)
            if (!Directory.Exists(Path.Combine(Restored, directory)))
                throw new InvalidDataException("Falta una carpeta restaurada.");
    }

    public void Dispose()
    {
        string markerPath = Path.Combine(Destination, global::DestinationBinding.MarkerName);
        using (var root = new DirectoryFence(Root, markerPath))
        using (DestinationBinding.OpenVerifiedMarker(root.Parent(markerPath)))
            File.Delete(Path.Combine(Root, AttemptHistory.Name));
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
                {
                    File.Delete(Path.Combine(root, FolderBatch.LockName));
                    foreach (string name in new[] { BatchCatalog.PrimaryName, BatchCatalog.BackupName, BatchCatalog.CommitName,
                        PendingBatch.MetadataName, PendingBatch.LocationName })
                    {
                        File.Delete(Path.Combine(root, name));
                        File.Delete(Path.Combine(root, name + ".tmp"));
                    }
                }
            }
            if (binding is not null)
                File.Delete(Path.Combine(root, global::DestinationBinding.MarkerName));
        }
        foreach (string relative in DirectoryNames.Reverse())
            if (Directory.Exists(Path.Combine(root, relative)))
                Directory.Delete(Path.Combine(root, relative));
        Directory.Delete(root);
    }

    internal static void ValidateRoot(string root)
    {
        string relative = Path.GetRelativePath(Path.GetTempPath(), root);
        if (!Path.IsPathFullyQualified(root) || Path.GetFullPath(root) != root || relative != Path.GetFileName(root) ||
            !relative.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected synthetic batch fixture.");
    }
}
