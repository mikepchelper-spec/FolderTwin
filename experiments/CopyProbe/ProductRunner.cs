using System.ComponentModel;
using System.Diagnostics;

internal sealed record ProductMirrorApproval(int Version, Guid Job, Guid Id, byte[] JobStamp, string Generation,
    byte[] DocumentHash, byte[] RegistryHash, byte[] CurrentIdentity, byte[] SourceIdentity, byte[] PreviousHash,
    string? Previous, int MaximumChangePercent, bool EmptyReviewed, bool MassReviewed);

internal sealed partial class ProductJobRegistry
{
    internal Guid ApproveMirror(Guid id, MirrorPreview review, bool emptyReviewed, bool massReviewed)
    {
        using var job = OpenJob(id);
        var definition = ReadConfirmed(job, ReadIntent(job, id));
        if (definition.Options.Mode != ProductBackupMode.ProtectedMirror || review.Conflicts.Length != 0 ||
            review.RequiresEmptyReview && !emptyReviewed || review.RequiresMassReview && !massReviewed)
            throw new InvalidDataException("Revisa el origen, los cambios y los conflictos antes de aprobar el espejo.");
        _ = VersionStore.GenerationTime(review.Generation);
        Guid approvalId = Guid.NewGuid();
        var approval = new ProductMirrorApproval(1, id, approvalId, JobStamp(job), review.Generation, review.DocumentHash,
            review.RegistryHash, review.CurrentIdentity, review.SourcePlan.SourceIdentity, review.PreviousHash,
            review.Previous, review.MaximumChangePercent, emptyReviewed, massReviewed);
        using var reviews = WindowsProbeNative.OpenOrCreatePlainDirectory(job, "reviews");
        RetentionJournal.WriteFile(reviews, approvalId.ToString("N"), Encode(approval));
        return approvalId;
    }

    internal ProductMirrorApproval ReadApproval(Guid id, Guid approvalId)
    {
        using var job = OpenJob(id);
        _ = ReadConfirmed(job, ReadIntent(job, id));
        using var reviews = WindowsProbeNative.OpenPlainDirectory(job, "reviews");
        var approval = Decode<ProductMirrorApproval>(reviews, approvalId.ToString("N"));
        if (approval.Version != 1 || approval.Job != id || approval.Id != approvalId ||
            !approval.JobStamp.AsSpan().SequenceEqual(JobStamp(job)) || approval.DocumentHash is not { Length: 32 } ||
            approval.RegistryHash is not { Length: 32 } || approval.CurrentIdentity is not { Length: 24 } ||
            approval.SourceIdentity is not { Length: 24 } || approval.PreviousHash is not { Length: 32 } ||
            approval.MaximumChangePercent is < 1 or > 100)
            throw new InvalidDataException("La aprobación del espejo está dañada o corresponde a otro trabajo.");
        _ = VersionStore.GenerationTime(approval.Generation);
        if (approval.Previous is not null) _ = VersionStore.GenerationTime(approval.Previous);
        return approval;
    }
}

internal static class ProductRunner
{
    internal static async Task<int> RunScheduledAsync(string registryPath, Guid id, Func<bool> canRun,
        Func<TimeSpan, Task>? delay = null)
    {
        try
        {
            using var registry = new ProductJobRegistry(registryPath);
            using var invocation = registry.AcquireScheduledLock(id);
            for (int retries = 0; ; retries++)
            {
                var job = registry.Get(id);
                if (!job.Enabled || job.Retired || job.Options.DailyAt is null || !canRun()) return 0;
                var result = await Task.Run(() => Run(registry, id));
                if (result.State == ProductRunState.Confirmed) return 0;
                if (retries == 3 || result.State is not
                    (ProductRunState.Busy or ProductRunState.WaitingForSource or ProductRunState.WaitingForDestination)) return 1;
                var attempt = registry.RunHistory(id).Runs.First(item => item.Result is { } saved &&
                    saved.IntentHash.AsSpan().SequenceEqual(result.IntentHash));
                using var waiting = registry.BeginRetry(id, attempt.Intent.Id, retries + 1);
                // Poll ownership during the wait so removing or disabling a task
                // stops its deferred retry without terminating its process.
                for (int remaining = 300; remaining > 0; remaining -= 5)
                {
                    await (delay ?? Task.Delay)(TimeSpan.FromSeconds(5));
                    job = registry.Get(id);
                    if (!job.Enabled || job.Retired || job.Options.DailyAt is null || !canRun()) return 0;
                }
            }
        }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            Console.Error.WriteLine("El horario necesita revisión. " + error.Message);
            return VersionJob.SharingViolation(error) ? 2 : 1;
        }
    }

    internal static ProductRunResult Run(ProductJobRegistry registry, Guid id, Guid? approvalId = null,
        Action<WorkProgress>? checkpoint = null, Func<DateTimeOffset>? clock = null)
    {
        using var run = registry.StartRun(id, clock);
        var control = new Controller(run, checkpoint);
        var job = run.Job;
        try
        {
            if (approvalId is not null && job.Options.Mode != ProductBackupMode.ProtectedMirror)
                throw new InvalidDataException("La aprobación no corresponde al modo de este trabajo.");
            control.Observe(new(WorkPhase.Analyzing, 0, 0));
            var store = VersionStore.OpenProduct(job, initialize: true, observe: control.Observe);
            if (job.Options.Mode == ProductBackupMode.Versions)
            {
                var pending = store.ListPending();
                if (pending.Length > 1) throw new InvalidDataException("Hay varios puntos pendientes de revisar.");
                string? previous = run.Previous.Runs.Where(item => item.Intent.Operation == ProductRunOperation.Backup)
                    .TakeWhile(item => item.Result?.State != ProductRunState.Confirmed)
                    .FirstOrDefault(item => item.Generation is not null)?.Generation;
                string generation = pending.SingleOrDefault()?.Id ?? previous ?? VersionStore.NewGenerationId();
                run.SetGeneration(generation);
                if (pending.Length == 1 || store.ListPoints().Any(point => point.Id == generation))
                    store.ResumePoint(generation, expectedSourceIdentity: job.Source.Identity);
                else store.CreatePoint(job.Source.VolumePath, generation);
            }
            else
            {
                store.InitializeMirror(resumeInitialization: true);
                var pending = store.ListMirrorOperations().Where(operation => !operation.Applied).ToArray();
                if (pending.Length > 1) throw new InvalidDataException("Hay varias actualizaciones pendientes de revisar.");
                if (pending.Length == 1)
                {
                    run.SetGeneration(pending[0].Generation);
                    store.ResumeMirror(pending[0].Generation);
                }
                else
                {
                    ProductMirrorApproval? approval = approvalId is { } approved ? registry.ReadApproval(id, approved) : null;
                    var review = store.PreviewMirror(job.Source.VolumePath, approval?.MaximumChangePercent ?? 25, approval?.Generation);
                    if (approval is not null && (!review.DocumentHash.AsSpan().SequenceEqual(approval.DocumentHash) ||
                        !review.RegistryHash.AsSpan().SequenceEqual(approval.RegistryHash) || review.Previous != approval.Previous ||
                        !review.PreviousHash.AsSpan().SequenceEqual(approval.PreviousHash) ||
                        !review.CurrentIdentity.AsSpan().SequenceEqual(approval.CurrentIdentity) ||
                        !review.SourcePlan.SourceIdentity.AsSpan().SequenceEqual(approval.SourceIdentity)))
                        throw new InvalidDataException("El espejo cambió desde su aprobación. Repite la revisión.");
                    if (review.Conflicts.Length > 0)
                        return run.Finish(ProductRunState.Review, review.Conflicts[0], clock: clock,
                            detail: "Hay ediciones externas en el destino. Consérvalas y resuelve el conflicto antes de reintentar.");
                    if (job.Options.Mode == ProductBackupMode.ProtectedMirror && approval is null &&
                        (review.Removed > 0 || review.RemovedDirectories > 0 || review.RequiresEmptyReview || review.RequiresMassReview))
                        return run.Finish(ProductRunState.Review, clock: clock,
                            detail: "Revisa las retiradas, el origen vacío o el alcance de los cambios antes de actualizar el espejo.");
                    run.SetGeneration(review.Generation);
                    store.BeginMirror(review, approval?.EmptyReviewed ?? false, approval?.MassReviewed ?? false);
                }
            }
            return run.Finish(ProductRunState.Confirmed, clock: clock);
        }
        catch (OperationCanceledException) { return run.Finish(ProductRunState.Cancelled, control.Current.RelativePath, clock: clock); }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            ProductRunState state = VersionJob.SharingViolation(error) ? ProductRunState.Busy : Availability(job);
            int code = error is Win32Exception native ? native.NativeErrorCode : error.InnerException is Win32Exception inner ?
                inner.NativeErrorCode : error.HResult;
            return run.Finish(state, control.Current.RelativePath, code, clock, error.Message);
        }
    }

    internal static int RunCommand(string registryPath, Guid id, Guid? approval = null)
    {
        try
        {
            using var registry = new ProductJobRegistry(registryPath);
            return Run(registry, id, approval).State == ProductRunState.Confirmed ? 0 : 1;
        }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            Console.Error.WriteLine("El trabajo no pudo completar su registro local. " + error.Message);
            return VersionJob.SharingViolation(error) ? 2 : 1;
        }
    }

    internal static ProductRunResult Verify(ProductJobRegistry registry, Guid id, string generation,
        Action<WorkProgress>? checkpoint = null)
    {
        _ = VersionStore.GenerationTime(generation);
        using var run = registry.StartRun(id, operation: ProductRunOperation.Verify);
        run.SetGeneration(generation);
        var control = new Controller(run, checkpoint);
        try
        {
            var store = VersionStore.OpenProduct(run.Job, observe: control.Observe);
            bool degraded = run.Job.Options.Mode == ProductBackupMode.Accumulative ||
                run.Job.Options.Mode == ProductBackupMode.ProtectedMirror && store.RegisteredGenerations().MirrorGeneration == generation
                ? store.VerifyCurrent(generation) : store.Verify(generation);
            return run.Finish(ProductRunState.Confirmed, degraded: degraded);
        }
        catch (OperationCanceledException) { return run.Finish(ProductRunState.Cancelled, control.Current.RelativePath); }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            return run.Finish(VersionJob.SharingViolation(error) ? ProductRunState.Busy : Availability(run.Job),
                control.Current.RelativePath, error.HResult, detail: error.Message);
        }
    }

    internal static int VerifyCommand(string registryPath, Guid id, string generation)
    {
        try
        {
            using var registry = new ProductJobRegistry(registryPath);
            return Verify(registry, id, generation).State == ProductRunState.Confirmed ? 0 : 1;
        }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            Console.Error.WriteLine("La verificación requiere revisión. " + error.Message);
            return VersionJob.SharingViolation(error) ? 2 : 1;
        }
    }

    private static ProductRunState Availability(ProductJob job)
    {
        try
        {
            using var destination = new DirectoryFence(job.Destination.LastVolumePath);
            using var marker = job.Destination.OpenVerifiedMarker(destination.Root);
        }
        catch (Exception error) when (Unavailable(error)) { return ProductRunState.WaitingForDestination; }
        catch (Exception error) when (VersionJob.Expected(error)) { return ProductRunState.Review; }
        try { using var source = new DirectoryFence(job.Source.VolumePath); }
        catch (Exception error) when (Unavailable(error)) { return ProductRunState.WaitingForSource; }
        catch (Exception error) when (VersionJob.Expected(error)) { return ProductRunState.Review; }
        return ProductRunState.Review;
    }

    private static bool Unavailable(Exception error) => VersionJob.Unavailable(error) ||
        error is Win32Exception { NativeErrorCode: 2 or 3 or 15 or 21 or 55 or 1167 } ||
        error.InnerException is Win32Exception { NativeErrorCode: 15 or 55 or 1167 };

    private sealed class Controller(ProductJobRegistry.ProductRunLease run, Action<WorkProgress>? checkpoint)
    {
        private readonly Stopwatch sincePoll = Stopwatch.StartNew();
        private WorkPhase? lastPhase;
        internal WorkProgress Current { get; private set; } = new(WorkPhase.Analyzing, 0, 0);

        internal void Observe(WorkProgress work)
        {
            Current = work;
            checkpoint?.Invoke(work);
            if (lastPhase == work.Phase && sincePoll.ElapsedMilliseconds < 200) return;
            sincePoll.Restart();
            lastPhase = work.Phase;
            var action = run.ReadAction();
            while (action == ProductRunAction.Pause)
            {
                Report(work, paused: true);
                Thread.Sleep(200);
                action = run.ReadAction();
            }
            if (action == ProductRunAction.Cancel) throw new OperationCanceledException();
            Report(work, paused: false);
        }

        private void Report(WorkProgress work, bool paused)
        {
            try { run.Progress(work, paused); }
            catch (Exception error) when (VersionJob.Expected(error)) { /* The final durable result remains mandatory. */ }
        }
    }
}
