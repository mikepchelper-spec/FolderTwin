using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CopyLab;

public partial class ProductWindow
{
    private readonly DispatcherTimer runtimeTimer;
    private readonly Dictionary<Guid, ProductRunHistory> runSnapshots = [];
    private readonly Dictionary<Guid, Process> workerProcesses = [];
    private readonly Dictionary<Guid, long> reportedResults = [];
    private bool polling;
    private bool closed;

    private Process StartBackupProcess(string location, Guid id, Guid? approval = null) =>
        testWorker?.Invoke(location, id, approval) ?? ProductWorker.Start(location, id, approval);
    private Process StartRestoreProcess(string location, Guid id, Guid request) =>
        testRestoreWorker?.Invoke(location, id, request) ?? ProductWorker.StartRestoration(location, id, request);

    private bool IsJobRunning(Guid id) => workerProcesses.TryGetValue(id, out var process) && !process.HasExited ||
        runSnapshots.TryGetValue(id, out var history) && history.Runs.Any(run => run.Running);

    private async Task StartWorkerAsync(ProductJob job, Func<string, Process> start)
    {
        if (IsJobRunning(job.Id)) throw new IOException("Este trabajo ya tiene una operación activa.");
        string location = await Task.Run(() =>
        {
            using var registry = openRegistry();
            if (registry.RunHistory(job.Id).Runs.Any(run => run.Running))
                throw new IOException("Otra ventana o un horario ya está ejecutando este trabajo.");
            return registry.Location;
        });
        if (workerProcesses.Remove(job.Id, out var previous)) previous.Dispose();
        workerProcesses[job.Id] = start(location);
        SetStatus("Ejecutor iniciado. Puedes cerrar la ventana mientras continúa la operación.");
        await RefreshRuntimeAsync();
    }

    private async Task PollRuntimeAsync()
    {
        if (closed || busy || polling) return;
        try { await RefreshRuntimeAsync(); }
        catch (Exception error) when (VersionJob.Expected(error)) { SetStatus("No se pudo actualizar el estado local. " + error.Message); }
    }

    private async Task RefreshRuntimeAsync()
    {
        if (closed) return;
        // Explicit callers need a fresh snapshot even when the timer already
        // started a query. The timer itself skips overlap in PollRuntimeAsync.
        while (polling)
        {
            await Task.Delay(20);
            if (closed) return;
        }
        polling = true;
        try
        {
            var jobs = JobList.Items.OfType<JobRow>().Where(row => row.Job is not null).ToArray();
            var snapshots = await Task.Run(() =>
            {
                using var registry = openRegistry();
                return jobs.Select(row => (row.Id, History: registry.RunHistory(row.Id))).ToArray();
            });
            if (closed) return;
            foreach (var (id, history) in snapshots)
            {
                runSnapshots[id] = history;
                jobs.Single(row => row.Id == id).SetRuntime("\n" + Brief(history) +
                    (history.Unreadable > 0 ? " · Historial requiere revisión" : ""));
                if (history.Runs.FirstOrDefault() is { Result: { } result } latest &&
                    (!reportedResults.TryGetValue(id, out long sequence) || latest.Intent.Sequence > sequence))
                {
                    reportedResults[id] = latest.Intent.Sequence;
                    if ((JobList.SelectedItem as JobRow)?.Id == id) SetStatus(ResultText(latest, result));
                }
            }
            RefreshProductActions();
            await RefreshScheduleAsync();
        }
        finally { polling = false; }
    }

    private void RefreshRuntimeDisplay()
    {
        if (RuntimeStatusText is null) return;
        var job = (JobList.SelectedItem as JobRow)?.Job;
        var history = job is not null && runSnapshots.TryGetValue(job.Id, out var saved) ? saved : new ProductRunHistory([], 0);
        var latest = history.Runs.FirstOrDefault();
        var active = history.Runs.FirstOrDefault(run => run.Running);
        var correct = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Backup && run.Result?.State == ProductRunState.Confirmed);
        var verified = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Verify && run.Result?.State == ProductRunState.Confirmed);
        var lastBackup = history.Runs.FirstOrDefault(run => run.Intent.Operation is ProductRunOperation.Backup or ProductRunOperation.ReviewMirror);
        BackupJobButton.Content = lastBackup is { Running: false, Result.State: not ProductRunState.Confirmed } or { Running: false, Result: null }
            ? "_Reintentar respaldo" : "_Respaldar ahora";
        RuntimeStatusText.Text = job is null ? "" : Brief(history) +
            "\nÚltimo respaldo completo · " + Date(correct?.Result?.FinishedUtc) +
            "\nÚltima verificación completa · " + Date(verified?.Result?.FinishedUtc) +
            (correct?.Generation is { } generation ? $"\nPunto recuperable del {VersionStore.GenerationTime(generation).ToLocalTime():g}" : "") +
            (verified?.Generation is { } verifiedGeneration ? $"\nPunto verificado del {VersionStore.GenerationTime(verifiedGeneration).ToLocalTime():g}" : "") +
            (active?.Progress is { } progress ? $"\n{Phase(progress.Work.Phase)} · {progress.Work.Processed:N0}/{progress.Work.Total:N0}" +
                (progress.Paused ? " · Pausado" : "") + (progress.Work.RelativePath is { } path ? "\n" + path : "") : "") +
            (active?.Restore is { } restoring ? "\nCarpeta de recuperación · " +
                Path.Combine(restoring.Target.Parent.VolumePath, restoring.Target.FolderName) : "") +
            (history.Runs.FirstOrDefault(run => run.RetryRunning)?.Retry is { } retry ? $"\nPróximo reintento · {retry.NextUtc.ToLocalTime():g}" : "") +
            (history.Unreadable > 0 ? $"\n{history.Unreadable} registros ilegibles conservados para revisión." : "");
        RunHistoryList.ItemsSource = history.Runs.Take(30).Select(run =>
            $"Intento {run.Intent.Sequence} · {run.Intent.StartedUtc.ToLocalTime():g} · " +
            (run.Result is { } result ? ResultText(run, result) : run.Running ? "En ejecución" : "Interrumpido") +
            (run.Restore is { } restore && run.Result?.State != ProductRunState.Confirmed ?
                "\nCarpeta revisada · " + Path.Combine(restore.Target.Parent.VolumePath, restore.Target.FolderName) : "")).ToArray();
        PauseRunButton.IsEnabled = CancelRunButton.IsEnabled = !busy && active is not null;
        RunControlsPanel.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        OperationStatusText.Text = active?.Progress is { } current ?
            Phase(current.Work.Phase) + $" · {current.Work.Processed:N0}/{current.Work.Total:N0}" + (current.Paused ? " · Pausado" : "") : "";
        PauseRunButton.Content = active?.Progress?.Paused == true ? "_Continuar" : "_Pausar";
        RunProgress.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        RunProgress.IsIndeterminate = active?.Progress?.Paused != true && active?.Progress?.Work.Total is not > 0;
        RunProgress.Maximum = Math.Max(1, active?.Progress?.Work.Total ?? 1);
        RunProgress.Value = active?.Progress?.Work.Processed ?? 0;
        if (latest is null && job is not null && IsJobRunning(job.Id)) RuntimeStatusText.Text = "Iniciando ejecutor…";
    }

    private static string Date(DateTimeOffset? date) => date?.ToLocalTime().ToString("g") ?? "Todavía no disponible";
    private static string Brief(ProductRunHistory history) => history.Runs.FirstOrDefault() is not { } last ? "Todavía sin ejecutar" :
        last.Running ? last.Progress?.Paused == true ? "Pausado" : "En ejecución" :
        last.Result is { } result ? result.State switch
        {
            ProductRunState.Confirmed => result.Degraded ? "Verificado; catálogo requiere atención" : last.Intent.Operation switch
            {
                ProductRunOperation.ReviewMirror => last.Mirror?.ResumePending == true ? "Actualización pendiente de reanudar" : "Revisión del espejo lista",
                ProductRunOperation.ReviewAbandon => "Plan pendiente revisado",
                ProductRunOperation.AbandonVersion => "Plan anterior conservado; crea un respaldo nuevo",
                ProductRunOperation.Restore => "Recuperación completa",
                ProductRunOperation.Verify => "Verificación completa",
                _ => "Respaldo completo"
            },
            ProductRunState.Cancelled => "Cancelado; puede haber trabajo pendiente",
            ProductRunState.WaitingForSource => "Esperando origen",
            ProductRunState.WaitingForDestination => "Esperando destino",
            ProductRunState.Busy => "Archivo o destino en uso",
            _ => "Requiere atención"
        } : "Interrumpido; revisa o reintenta";

    private static string Phase(WorkPhase phase) => phase switch
    {
        WorkPhase.Analyzing => "Analizando", WorkPhase.Copying => "Copiando contenido", WorkPhase.Verifying => "Verificando",
        WorkPhase.Publishing => "Publicando", WorkPhase.Preparing => "Preparando trabajo", _ => "Recuperando"
    };

    private static string ResultText(ProductRunView run, ProductRunResult result) => (result.State switch
    {
        ProductRunState.Confirmed when run.Intent.Operation == ProductRunOperation.ReviewAbandon =>
            "Revisión terminada. Decide si conservar el plan incompleto y permitir uno nuevo; no se creó un respaldo.",
        ProductRunState.Confirmed when run.Intent.Operation == ProductRunOperation.AbandonVersion =>
            "Plan incompleto conservado. Ya puedes crear un respaldo nuevo; los datos parciales permanecen para la retención manual.",
        ProductRunState.Confirmed when run.Mirror is { } review => review.ResumePending ? "Reanuda la actualización pendiente desde su diario." :
            review.Conflicts > 0 ? "La revisión detectó conflictos. Conserva los cambios externos antes de repetir el análisis." :
                "Revisión terminada. Comprueba los cambios antes de aplicar el espejo.",
        ProductRunState.Confirmed when run.Restore is { } restore => "Recuperación completa y verificada.\n" +
            Path.Combine(restore.Target.Parent.VolumePath, restore.Target.FolderName) +
            (result.Degraded ? "\nUna réplica del catálogo necesita revisión." : ""),
        ProductRunState.Confirmed => run.Intent.Operation == ProductRunOperation.Verify ? "Verificación completa del contenido." +
            (result.Degraded ? " Una réplica del catálogo necesita revisión." : "") : "Respaldo completo y verificado.",
        ProductRunState.Cancelled when run.Intent.Operation == ProductRunOperation.Restore =>
            "Recuperación cancelada. Revisa una carpeta nueva para reintentar; los archivos ya recuperados se conservan.",
        ProductRunState.Cancelled when run.Intent.Operation == ProductRunOperation.ReviewMirror =>
            "Revisión cancelada. Repite el análisis para revisar el espejo antes de aplicarlo.",
        ProductRunState.Cancelled => "Operación cancelada en un límite recuperable. Reintenta para continuar el trabajo pendiente.",
        ProductRunState.WaitingForSource => "El origen no está disponible. Reconéctalo y reintenta.",
        ProductRunState.WaitingForDestination => "El destino no está disponible. Reconéctalo y reintenta.",
        ProductRunState.Busy => "Un archivo o el destino está en uso. Cierra la aplicación que lo usa o espera a que termine la otra operación. Luego reintenta.",
        _ => result.Detail ?? "La operación necesita revisión antes de continuar."
    }) + (result.RelativePath is { } path ? "\n" + path : "");

    private void PauseRunClick(object sender, RoutedEventArgs e) => SendRunAction(cancel: false);
    private void CancelRunClick(object sender, RoutedEventArgs e) => SendRunAction(cancel: true);
    private void SendRunAction(bool cancel)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { } job || !runSnapshots.TryGetValue(job.Id, out var history) ||
            history.Runs.FirstOrDefault(run => run.Running) is not { } active) return;
        var action = cancel ? ProductRunAction.Cancel : active.Progress?.Paused == true ? ProductRunAction.Resume : ProductRunAction.Pause;
        Start(async () =>
        {
            await Task.Run(() => { using var registry = openRegistry(); registry.RequestRun(job.Id, active.Intent.Id, action); });
            SetStatus("Solicitud enviada al ejecutor. Se atenderá en el siguiente límite seguro.");
            await RefreshRuntimeAsync();
        });
    }

    private async Task WaitForWorkersAsync()
    {
        foreach (var worker in workerProcesses.Values.ToArray()) await worker.WaitForExitAsync();
        while (polling) await Task.Delay(20);
        await RefreshRuntimeAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        runtimeTimer.Stop();
        productTray?.Dispose();
        foreach (var worker in workerProcesses.Values) worker.Dispose();
        workerProcesses.Clear();
        base.OnClosed(e);
    }
}
