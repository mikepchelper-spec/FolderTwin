internal sealed record CloudPoint(Guid Job, string Generation, long Bytes, int Files, int Directories);

internal sealed partial class VersionStore
{
    internal static CloudPoint ExportConfirmedPoint(ProductJobRegistry registry, Guid jobId, string generation,
        string output, CancellationToken cancellationToken = default)
    {
        if (IsPartialPoint(generation) || !registry.RunHistory(jobId).Runs.Any(run =>
            run.Intent.Operation == ProductRunOperation.Backup && run.Result is
            { State: ProductRunState.Confirmed, Generation: var point } && point == generation))
            throw new InvalidDataException("Solo se pueden enviar puntos locales confirmados.");

        var job = registry.Get(jobId);
        var store = OpenProduct(job, observe: _ => cancellationToken.ThrowIfCancellationRequested());
        var catalog = store.Restore(generation, output, requireRegisteredPoint: true);
        return new(jobId, generation, catalog.Files.Sum(file => file.Length), catalog.Files.Length, catalog.Directories.Length);
    }
}
