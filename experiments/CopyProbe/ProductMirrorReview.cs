internal sealed record ProductMirrorReview(int Version, byte[] IntentHash, string Generation, bool ResumePending,
    ProductMirrorApproval? Proof, int Added, int Replaced, int Removed, int AddedDirectories, int RemovedDirectories,
    int Conflicts, string? FirstConflict, long StageBytes, long EstimatedBytes, ulong AvailableBytes,
    bool RequiresEmptyReview, bool RequiresMassReview);

internal sealed partial class ProductJobRegistry
{
    private static void ValidateMirrorReview(ProductMirrorReview review, ProductRunIntent intent, string? generation)
    {
        if (review.Version != 1 || intent.Operation != ProductRunOperation.ReviewMirror ||
            !review.IntentHash.AsSpan().SequenceEqual(IntentHash(intent)) || review.Generation != generation ||
            review.Added < 0 || review.Replaced < 0 || review.Removed < 0 || review.AddedDirectories < 0 || review.RemovedDirectories < 0 ||
            review.Conflicts < 0 || !ValidRelative(review.FirstConflict) || review.FirstConflict?.Length > 2048 ||
            review.StageBytes < 0 || review.EstimatedBytes < 0 || review.ResumePending != (review.Proof is null))
            throw new InvalidDataException("La revisión del espejo está dañada o pertenece a otro intento.");
        _ = VersionStore.GenerationTime(review.Generation);
        if (review.Proof is not { } proof) return;
        if (proof.Version != 1 || proof.Job != intent.Job || proof.Id != intent.Id ||
            !proof.JobStamp.AsSpan().SequenceEqual(intent.JobStamp) || proof.Generation != generation ||
            proof.DocumentHash is not { Length: 32 } || proof.RegistryHash is not { Length: 32 } ||
            proof.PreviousHash is not { Length: 32 } || proof.CurrentIdentity is not { Length: 24 } ||
            proof.SourceIdentity is not { Length: 24 } || proof.MaximumChangePercent is < 1 or > 100 ||
            proof.EmptyReviewed || proof.MassReviewed)
            throw new InvalidDataException("La prueba de revisión del espejo no corresponde al trabajo.");
        if (proof.Previous is not null) _ = VersionStore.GenerationTime(proof.Previous);
    }

    internal Guid ApproveMirror(Guid id, Guid reviewRun, bool emptyReviewed, bool massReviewed)
    {
        using var job = OpenJob(id);
        var definition = ReadConfirmed(job, ReadIntent(job, id));
        using var runs = WindowsProbeNative.OpenPlainDirectory(job, "runs");
        var run = ReadRuns(job, runs, id).Runs.SingleOrDefault(run => run.Intent.Id == reviewRun);
        if (definition.Options.Mode != ProductBackupMode.ProtectedMirror || run?.Result?.State != ProductRunState.Confirmed ||
            run.Mirror is not { Proof: { } proof, ResumePending: false, Conflicts: 0 } review ||
            review.RequiresEmptyReview && !emptyReviewed || review.RequiresMassReview && !massReviewed)
            throw new InvalidDataException("Revisa el origen, el alcance de los cambios y los conflictos antes de aplicar el espejo.");
        Guid approvalId = Guid.NewGuid();
        using var reviews = WindowsProbeNative.OpenOrCreatePlainDirectory(job, "reviews");
        RetentionJournal.WriteFile(reviews, approvalId.ToString("N"), Encode(proof with
        { Id = approvalId, EmptyReviewed = emptyReviewed, MassReviewed = massReviewed }));
        return approvalId;
    }
}

internal static partial class ProductRunner
{
    internal static ProductRunResult ReviewMirror(ProductJobRegistry registry, Guid id, Action<WorkProgress>? checkpoint = null)
    {
        using var run = registry.StartRun(id, operation: ProductRunOperation.ReviewMirror);
        var control = new Controller(run, checkpoint);
        try
        {
            if (run.Job.Options.Mode != ProductBackupMode.ProtectedMirror)
                throw new InvalidDataException("Este trabajo no utiliza espejo protegido.");
            control.Observe(new(WorkPhase.Preparing, 0, 0));
            var store = VersionStore.OpenProduct(run.Job, initialize: true, observe: control.Observe);
            store.InitializeMirror(resumeInitialization: true);
            var pending = store.ListMirrorOperations().Where(operation => !operation.Applied).ToArray();
            if (pending.Length > 1) throw new InvalidDataException("Hay varias actualizaciones del espejo pendientes de revisar.");
            ProductMirrorReview summary;
            if (pending.Length == 1)
            {
                run.SetGeneration(pending[0].Generation);
                summary = new(1, ProductJobRegistry.IntentHash(run.Intent), pending[0].Generation, true,
                    null, 0, 0, 0, 0, 0, 0, null, 0, 0, 0, false, false);
            }
            else
            {
                var review = store.PreviewMirror(run.Job.Source.VolumePath);
                run.SetGeneration(review.Generation);
                var proof = new ProductMirrorApproval(1, id, run.Intent.Id, run.Intent.JobStamp, review.Generation,
                    review.DocumentHash, review.RegistryHash, review.CurrentIdentity, review.SourcePlan.SourceIdentity,
                    review.PreviousHash, review.Previous, review.MaximumChangePercent, false, false);
                string? first = review.Conflicts.FirstOrDefault();
                if (first?.Length > 2048) first = null;
                summary = new(1, ProductJobRegistry.IntentHash(run.Intent), review.Generation, false, proof,
                    review.Added, review.Replaced, review.Removed, review.AddedDirectories, review.RemovedDirectories,
                    review.Conflicts.Length, first, review.StageBytes, review.EstimatedBytes, review.AvailableBytes,
                    review.RequiresEmptyReview, review.RequiresMassReview);
            }
            control.Observe(new(WorkPhase.Publishing, 0, 1));
            run.SaveMirrorReview(summary);
            return run.Finish(ProductRunState.Confirmed);
        }
        catch (OperationCanceledException) { return run.Finish(ProductRunState.Cancelled, control.Current.RelativePath); }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            return run.Finish(VersionJob.SharingViolation(error) ? ProductRunState.Busy : Availability(run.Job),
                control.Current.RelativePath, error.HResult, detail: error.Message);
        }
    }

    internal static int ReviewMirrorCommand(string location, Guid id)
    {
        try
        {
            using var registry = new ProductJobRegistry(location);
            return ReviewMirror(registry, id).State == ProductRunState.Confirmed ? 0 : 1;
        }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            Console.Error.WriteLine("La revisión del espejo no pudo completar su registro. " + error.Message);
            return VersionJob.SharingViolation(error) ? 2 : 1;
        }
    }
}
