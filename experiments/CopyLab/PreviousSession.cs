using System.IO;

namespace CopyLab;

internal sealed class PreviousSession
{
    internal DestinationBinding Binding { get; }
    internal string Root { get; }
    internal string Source => Path.Combine(Root, "Origen");
    internal string Destination => Path.Combine(Root, "Destino");
    internal bool Confirmed { get; }

    internal PreviousSession(string root)
    {
        Root = root;
        BatchFixture.ValidateRoot(Root);
        using var backup = new DirectoryFence(Destination);
        Confirmed = WindowsProbeNative.PlainFileExists(backup.Root, BatchCatalog.CommitName);
        if (Confirmed)
        {
            var catalog = BatchCatalog.Read(Destination);
            BatchFixture.ValidateSamples(catalog.Files, catalog.Directories);
            Binding = catalog.Binding;
        }
        else
        {
            var plan = PendingBatch.Read(Root, Destination);
            ValidateSamples(plan);
            Binding = plan.Target;
        }
    }

    internal BatchResult Resume(CancellationToken cancellationToken, IProgress<BatchProgress>? progress) =>
        PendingBatch.Resume(Root, Destination, cancellationToken, validate: plan =>
        {
            ValidateSamples(plan);
            RequireSameDestination(plan.Target);
        }, finishCurrentFileOnCancel: true, progress: progress);

    internal BatchResult Restore(BatchFixture output, IProgress<BatchProgress>? progress)
    {
        var catalog = BatchCatalog.Read(Destination);
        RequireSameDestination(catalog.Binding);
        BatchFixture.ValidateSamples(catalog.Files, catalog.Directories);
        // Both roots are separate synthetic fixtures directly under the same temp directory.
        string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var result = FolderBatch.Run(catalog.RestoreTo(temporaryRoot, Destination, output.Restored, output.RestoreBinding), progress: progress);
        return catalog.Degraded
            ? result with { Note = "Se usó una réplica íntegra. La otra copia del catálogo necesita revisión." }
            : result;
    }

    private void RequireSameDestination(DestinationBinding current)
    {
        if (Binding.Marker != current.Marker || !Binding.Identity.AsSpan().SequenceEqual(current.Identity))
            throw new InvalidDataException("El destino de la sesión cambió desde que se abrió.");
    }

    private void ValidateSamples(BatchPlan plan)
    {
        if (plan.Source != Source)
            throw new InvalidDataException("El descriptor no apunta al origen de esta sesión de laboratorio.");
        BatchFixture.ValidateSamples(plan.Files, plan.Directories);
    }
}
