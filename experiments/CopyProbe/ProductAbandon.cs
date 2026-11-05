internal sealed record ProductAbandonReview(int Version, byte[] IntentHash, string Generation, bool ResumePending,
    VersionAbandonReview? Proof);

internal sealed partial class ProductJobRegistry
{
    private static void ValidateAbandonReview(ProductAbandonReview review, ProductRunIntent intent, string? generation)
    {
        if (review.Version != 1 || intent.Operation is not (ProductRunOperation.ReviewAbandon or ProductRunOperation.AbandonVersion) ||
            !review.IntentHash.AsSpan().SequenceEqual(IntentHash(intent)) || review.Generation != generation ||
            review.ResumePending != (review.Proof is null))
            throw new InvalidDataException("La revisión del plan pertenece a otro intento.");
        _ = VersionStore.GenerationTime(review.Generation);
        if (review.Proof is not { } proof) return;
        if (proof.Generation != generation || proof.DocumentHash is not { Length: 32 } || proof.RegistryHash is not { Length: 32 } ||
            proof.SourceIdentity is not { Length: 24 } || proof.Files < 0 || proof.Artifacts.Length > 5 ||
            proof.Artifacts.Any(file => file.Hash is not { Length: 32 } || file.Identity is not { Length: 24 } ||
                file.Length is < 0 or > BatchCatalog.VersionMaximumBytes))
            throw new InvalidDataException("La revisión del plan está dañada.");
    }
}

internal static partial class ProductRunner
{
    internal static ProductRunResult ReviewAbandon(ProductJobRegistry registry, Guid id, Action<WorkProgress>? checkpoint = null)
    {
        using var run = registry.StartRun(id, operation: ProductRunOperation.ReviewAbandon);
        var control = new Controller(run, checkpoint);
        try
        {
            if (run.Job.Options.Mode != ProductBackupMode.Versions)
                throw new InvalidDataException("La conservación de planes está disponible para versionado. Los otros modos conservan su diario para reanudar.");
            control.Observe(new(WorkPhase.Preparing, 0, 0));
            var store = VersionStore.OpenProduct(run.Job);
            var interrupted = store.ListVersionAbandons().Where(item => !item.Complete).ToArray();
            if (interrupted.Length > 1) throw new InvalidDataException("Hay varios diarios incompletos que requieren revisión.");
            string generation;
            VersionAbandonReview? proof = null;
            if (interrupted.Length == 1) generation = interrupted[0].Generation;
            else
            {
                var pending = store.ListPending();
                if (pending.Length != 1) throw new InvalidDataException(pending.Length == 0 ?
                    "No hay un plan completo pendiente de conservar." : "Hay varios planes pendientes que requieren revisión.");
                generation = pending[0].Id;
                proof = store.PreviewAbandonVersion(generation);
            }
            run.SetGeneration(generation);
            control.Observe(new(WorkPhase.Publishing, 0, 1));
            run.SaveAbandonReview(new(1, ProductJobRegistry.IntentHash(run.Intent), generation, interrupted.Length == 1, proof));
            return run.Finish(ProductRunState.Confirmed);
        }
        catch (OperationCanceledException) { return run.Finish(ProductRunState.Cancelled); }
        catch (Exception error) when (VersionJob.Expected(error))
        { return run.Finish(VersionJob.SharingViolation(error) ? ProductRunState.Busy : Availability(run.Job), errorCode: error.HResult, detail: error.Message); }
    }

    internal static ProductRunResult AbandonVersion(ProductJobRegistry registry, Guid id, Guid reviewRun,
        Action<WorkProgress>? checkpoint = null)
    {
        using var run = registry.StartRun(id, operation: ProductRunOperation.AbandonVersion);
        var control = new Controller(run, checkpoint);
        try
        {
            if (run.Job.Options.Mode != ProductBackupMode.Versions)
                throw new InvalidDataException("La revisión no corresponde a un trabajo de versionado.");
            var reviewed = run.Previous.Runs.SingleOrDefault(item => item.Intent.Id == reviewRun);
            if (reviewed is not { Intent.Operation: ProductRunOperation.ReviewAbandon, Result.State: ProductRunState.Confirmed, Abandon: { } review })
                throw new InvalidDataException("Revisa y acepta el plan pendiente antes de conservarlo.");
            run.SetGeneration(review.Generation);
            run.SaveAbandonReview(review with { IntentHash = ProductJobRegistry.IntentHash(run.Intent) });
            control.Observe(new(WorkPhase.Preparing, 0, 7));
            var store = VersionStore.OpenProduct(run.Job);
            void Observe(AbandonStep step) => control.ObserveBoundary(new(WorkPhase.Publishing, (int)step + 1, 7));
            if (review.ResumePending) store.ResumeAbandonVersion(review.Generation, Observe);
            else store.BeginAbandonVersion(review.Proof!, Observe);
            return run.Finish(ProductRunState.Confirmed);
        }
        catch (OperationCanceledException) { return run.Finish(ProductRunState.Cancelled); }
        catch (Exception error) when (VersionJob.Expected(error))
        { return run.Finish(VersionJob.SharingViolation(error) ? ProductRunState.Busy : Availability(run.Job), errorCode: error.HResult, detail: error.Message); }
    }

    internal static int AbandonCommand(string location, Guid id, Guid? reviewRun = null)
    {
        try
        {
            using var registry = new ProductJobRegistry(location);
            return (reviewRun is { } reviewed ? AbandonVersion(registry, id, reviewed) : ReviewAbandon(registry, id)).State == ProductRunState.Confirmed ? 0 : 1;
        }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            Console.Error.WriteLine("El plan pendiente se conserva para revisión. " + error.Message);
            return VersionJob.SharingViolation(error) ? 2 : 1;
        }
    }
}
