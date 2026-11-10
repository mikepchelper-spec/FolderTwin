using System.IO;

namespace CopyLab;

internal sealed record TraySnapshot(string Status, string Age, Guid[] Jobs, string[] Attention)
{
    internal static TraySnapshot Read(LabWorkspace workspace)
    {
        var jobs = VersionJob.List(workspace);
        var attention = new List<string>();
        var dates = new List<DateTimeOffset>();
        int pending = 0;
        int busy = 0;
        if (jobs.Unreadable != 0)
            attention.Add("job-definitions");
        foreach (var job in jobs.Jobs)
        {
            try
            {
                var history = VersionJob.History(workspace, job.Id);
                if (history.Unreadable != 0)
                    attention.Add($"{job.Id:N}-history");
                var latest = history.Results.FirstOrDefault();
                if (latest is null)
                    pending++;
                else if (latest.State != VersionJobState.Confirmed)
                    attention.Add($"{job.Id:N}-{latest.State}");
                var confirmed = history.Results.FirstOrDefault(result => result.State == VersionJobState.Confirmed);
                if (confirmed?.Generation is not { } generation)
                    continue;
                var store = VersionStore.Open(workspace, Path.Combine(workspace.Root, job.Destination));
                if (store.Binding.Marker != job.TargetMarker ||
                    !store.Binding.Identity.AsSpan().SequenceEqual(job.TargetIdentity))
                    throw new InvalidDataException("El destino cambió de identidad.");
                var catalogs = store.ReadCatalogs(generation, null);
                if (catalogs.Current.Degraded)
                    attention.Add($"{job.Id:N}-replica");
                if (store.SupportsRetention && store.RegisteredGenerations().Expired.Contains(generation))
                {
                    attention.Add($"{job.Id:N}-expired");
                    continue;
                }
                dates.Add(confirmed.FinishedUtc);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception { NativeErrorCode: 32 } ||
                error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 })
            {
                busy++;
            }
            catch (Exception error) when (VersionJob.Expected(error))
            {
                attention.Add($"{job.Id:N}-unavailable");
            }
        }
        string age = dates.Count == 0 ? "Sin punto confirmado consultable." : AgeText(dates.Min());
        if (dates.Any(date => date > DateTimeOffset.UtcNow.AddMinutes(1)))
            attention.Add("clock");
        string status = attention.Count != 0 ? "Hay trabajos o registros que requieren atención." :
            busy != 0 ? $"{busy} trabajos en curso u ocupados." :
            pending != 0 ? $"{pending} trabajos aún sin un punto confirmado." :
            jobs.Jobs.Length == 0 ? "Sin trabajos de versiones guardados." :
            $"{jobs.Jobs.Length} trabajos con su último intento confirmado.";
        return new(status, age, jobs.Jobs.Select(job => job.Id).ToArray(), attention.Distinct().ToArray());
    }

    internal static TraySnapshot ReadProduct(ProductJobRegistry registry, ProductJobRegistry.DisplayRunHistory? displayHistory = null)
    {
        var list = registry.List();
        var jobs = list.Jobs.Where(job => job.Enabled && !job.Retired).ToArray();
        var attention = new List<string>();
        var dates = new List<DateTimeOffset>();
        var runnable = new List<Guid>();
        int busy = 0, pending = 0, lowSpace = 0;
        if (list.Unreadable != 0 || list.Pending.Length != 0) attention.Add("job-definitions");
        if (jobs.Any(job => job.WatchMode == ProductWatchMode.Watching) && registry.ReadMonitorProcess() is null)
            attention.Add("monitor-stopped");
        foreach (var job in jobs)
        {
            var history = displayHistory?.Read(registry, job.Id) ?? registry.RunHistory(job.Id);
            if (history.Unreadable != 0) attention.Add($"{job.Id:N}-history");
            bool running = history.Runs.Any(run => run.Running);
            if (running) busy++; else runnable.Add(job.Id);
            var latestBackup = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Backup);
            if (LabWorkspace.IsDiskFull(latestBackup?.Result?.ErrorCode)) lowSpace++;
            var latestVerification = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Verify);
            var activity = history.Runs.FirstOrDefault(run => run.Intent.Operation != ProductRunOperation.Restore);
            if (activity is { Running: false, Intent.Operation: ProductRunOperation.ReviewAbandon or ProductRunOperation.AbandonVersion })
                attention.Add($"{job.Id:N}-pending-plan");
            if (activity is { Intent.Operation: ProductRunOperation.ReviewMirror, Running: false } &&
                (activity.Result?.State != ProductRunState.Confirmed || activity.Mirror is { ResumePending: true } or { Conflicts: > 0 } ||
                    activity.Mirror is { RequiresEmptyReview: true } or { RequiresMassReview: true }))
                attention.Add($"{job.Id:N}-review");
            if (latestBackup is null) pending++;
            // Verifying a previous point does not complete a failed or interrupted backup.
            foreach (var latest in new[] { latestBackup, latestVerification })
            {
                if (latest?.Result is { State: not (ProductRunState.Confirmed or ProductRunState.Unchanged) } result)
                    attention.Add(LabWorkspace.IsDiskFull(result.ErrorCode) ? $"{job.Id:N}-space" : $"{job.Id:N}-{result.State}");
                else if (latest?.Result is { Degraded: true })
                    attention.Add($"{job.Id:N}-{latest.Intent.Operation}-replica");
                else if (latest is { Result: null, Running: false })
                    attention.Add($"{job.Id:N}-interrupted");
            }
            var confirmed = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Backup &&
                run.Result is { State: ProductRunState.Confirmed, Scoped: false });
            var recoverable = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Backup &&
                run.Result?.State == ProductRunState.Confirmed);
            if (recoverable is null) continue;
            try
            {
                var store = VersionStore.OpenProduct(job);
                var generations = store.RegisteredGenerations();
                if (generations.Expired.Contains(recoverable.Generation!))
                { attention.Add($"{job.Id:N}-expired"); continue; }
                if (store.ReadCatalogs(recoverable.Generation!, null).Current.Degraded)
                    attention.Add($"{job.Id:N}-replica");
                if (confirmed is not null && !generations.Expired.Contains(confirmed.Generation!))
                    dates.Add(confirmed.Result!.FinishedUtc);
            }
            catch (Exception error) when (VersionJob.SharingViolation(error)) { if (!running) busy++; }
            catch (Exception error) when (VersionJob.Expected(error)) { attention.Add($"{job.Id:N}-unavailable"); }
        }
        if (dates.Any(date => date > DateTimeOffset.UtcNow.AddMinutes(1))) attention.Add("clock");
        string status = lowSpace != 0 ? $"Espacio insuficiente en {lowSpace} {(lowSpace == 1 ? "trabajo" : "trabajos")}. Abre FolderTwin para revisar el destino y reintentar." :
            attention.Count != 0 ? "Hay trabajos o registros que requieren atención." :
            busy > 0 ? $"{busy} trabajos en curso u ocupados." : pending > 0 ? $"{pending} trabajos aún sin un punto confirmado." :
            jobs.Length == 0 ? "Sin trabajos habilitados." : $"{jobs.Length} trabajos con su último intento confirmado.";
        return new(status, dates.Count == 0 ? "Sin respaldo completo consultable." : AgeText(dates.Min()),
            runnable.ToArray(), attention.Distinct().OrderByDescending(key => key.EndsWith("-space", StringComparison.Ordinal)).ToArray());
    }

    private static string AgeText(DateTimeOffset date)
    {
        var age = DateTimeOffset.UtcNow - date;
        string elapsed = age.TotalMinutes < 0 ? "fecha futura, revisa el reloj" :
            age.TotalMinutes < 1 ? "hace menos de un minuto" :
            age.TotalHours < 1 ? $"hace {(int)age.TotalMinutes} min" :
            age.TotalDays < 1 ? $"hace {(int)age.TotalHours} h" : $"hace {(int)age.TotalDays} días";
        return "Últimos puntos por trabajo · el más antiguo " + elapsed + ".";
    }
}
