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

    internal static TraySnapshot ReadProduct(ProductJobRegistry registry)
    {
        var list = registry.List();
        var jobs = list.Jobs.Where(job => job.Enabled && !job.Retired).ToArray();
        var attention = new List<string>();
        var dates = new List<DateTimeOffset>();
        var runnable = new List<Guid>();
        int busy = 0, pending = 0;
        if (list.Unreadable != 0 || list.Pending.Length != 0) attention.Add("job-definitions");
        foreach (var job in jobs)
        {
            var history = registry.RunHistory(job.Id);
            if (history.Unreadable != 0) attention.Add($"{job.Id:N}-history");
            bool running = history.Runs.Any(run => run.Running);
            if (running) busy++; else runnable.Add(job.Id);
            var latest = history.Runs.FirstOrDefault();
            var result = history.Runs.FirstOrDefault(run => run.Result is not null)?.Result;
            if (latest is null) pending++;
            else if (result is not null && result.State != ProductRunState.Confirmed)
                attention.Add($"{job.Id:N}-{result.State}");
            else if (latest.Result is null && !running) attention.Add($"{job.Id:N}-interrupted");
            var confirmed = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Backup &&
                run.Result?.State == ProductRunState.Confirmed);
            if (confirmed is null) continue;
            try
            {
                var store = VersionStore.OpenProduct(job);
                if (store.RegisteredGenerations().Expired.Contains(confirmed.Generation!))
                { attention.Add($"{job.Id:N}-expired"); continue; }
                if (store.ReadCatalogs(confirmed.Generation!, null).Current.Degraded)
                    attention.Add($"{job.Id:N}-replica");
                dates.Add(confirmed.Result!.FinishedUtc);
            }
            catch (Exception error) when (VersionJob.SharingViolation(error)) { if (!running) busy++; }
            catch (Exception error) when (VersionJob.Expected(error)) { attention.Add($"{job.Id:N}-unavailable"); }
        }
        if (dates.Any(date => date > DateTimeOffset.UtcNow.AddMinutes(1))) attention.Add("clock");
        string status = attention.Count != 0 ? "Hay trabajos o registros que requieren atención." :
            busy > 0 ? $"{busy} trabajos en curso u ocupados." : pending > 0 ? $"{pending} trabajos aún sin un punto confirmado." :
            jobs.Length == 0 ? "Sin trabajos habilitados." : $"{jobs.Length} trabajos con su último intento confirmado.";
        return new(status, dates.Count == 0 ? "Sin respaldo completo consultable." : AgeText(dates.Min()),
            runnable.ToArray(), attention.Distinct().ToArray());
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
