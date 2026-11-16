using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CopyLab;

public partial class ProductWindow
{
    private readonly DispatcherTimer runtimeTimer;
    private readonly Dictionary<Guid, ProductRunHistory> runSnapshots = [];
    private readonly Dictionary<Guid, CloudJobStatus> cloudSnapshots = [];
    private long? cloudRefreshedAt;
    private readonly ProductJobRegistry.DisplayRunHistory displayHistory = new();
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

    private async Task RefreshRuntimeAsync(bool rereadHistory = false)
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
            bool readCloud = rereadHistory || cloudRefreshedAt is null ||
                Stopwatch.GetElapsedTime(cloudRefreshedAt.Value) >= TimeSpan.FromSeconds(10) ||
                jobs.Any(row => !cloudSnapshots.ContainsKey(row.Id));
            var snapshots = await Task.Run(() =>
            {
                using var registry = openRegistry();
                if (rereadHistory) displayHistory.Invalidate();
                return jobs.Select(row =>
                {
                    var history = displayHistory.Read(registry, row.Id);
                    return (row.Id, History: history, Cloud: readCloud ? CloudJobStatus.Read(registry, row.Id, history) : null);
                }).ToArray();
            });
            if (closed) return;
            if (readCloud)
            {
                cloudSnapshots.Clear();
                cloudRefreshedAt = Stopwatch.GetTimestamp();
            }
            foreach (var (id, history, cloud) in snapshots)
            {
                runSnapshots[id] = history;
                if (cloud is not null) cloudSnapshots[id] = cloud;
                var row = jobs.Single(row => row.Id == id);
                string cloudLabel = cloudSnapshots.TryGetValue(id, out var currentCloud) && currentCloud.Configured
                    ? "\nNube · " + currentCloud.Label : "";
                row.SetRuntime("\n" + Brief(history) + (history.Unreadable > 0 ? " · Historial requiere revisión" : "") + cloudLabel,
                    RouteState(row.Job!, history));
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
        var correct = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Backup &&
            run.Result is { State: ProductRunState.Confirmed, Scoped: false });
        var recoverable = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Backup &&
            run.Result?.State == ProductRunState.Confirmed);
        var verified = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Verify &&
            run.Result?.State == ProductRunState.Confirmed && run.Generation is { } point && !VersionStore.IsPartialPoint(point));
        var reconciled = history.Runs.FirstOrDefault(run => run.Intent.Automatic &&
            run.Result is { Scoped: false, State: ProductRunState.Confirmed or ProductRunState.Unchanged });
        LastReconciliationText.Text = job?.WatchMode == ProductWatchMode.Off || job is null ? "" :
            "Última comparación automática · " + RouteDate(reconciled?.Result?.FinishedUtc);
        var restored = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Restore &&
            run.Result?.State == ProductRunState.Confirmed && run.Restore is not null);
        var lastBackup = history.Runs.FirstOrDefault(run => run.Intent.Operation is ProductRunOperation.Backup or ProductRunOperation.ReviewMirror);
        OpenRestoredFolderButton.IsEnabled = OpenRecoveryOutputButton.IsEnabled = !busy && restored is not null;
        NextProtectionStep.Text = job is null ? "" : active is not null ?
            "Siguiente paso · Espera el resultado de la operación o usa Pausar y Cancelar ejecución." :
            !job.Enabled || job.Retired ? "Trabajo deshabilitado. Puedes recuperar sus puntos y revisar la configuración para habilitar futuras copias." :
            LabWorkspace.IsDiskFull(lastBackup?.Result?.ErrorCode) ?
            "Siguiente paso · Libera espacio fuera de los archivos internos del respaldo y pulsa Reintentar respaldo." :
            correct is null ?
            (job.WatchMode == ProductWatchMode.Watching ? "Siguiente paso · Consulta el monitor mientras se prepara la primera copia." :
                "Siguiente paso · Pulsa Respaldar ahora para crear la primera copia.") :
            recoverable?.Result is { Scoped: true } && recoverable.Intent.Sequence > (reconciled?.Intent.Sequence ?? 0) &&
                verified?.Generation != recoverable.Generation ?
            "Se comprobaron los cambios del último punto. El monitor hará una comparación completa periódica." :
            verified is null || verified.Generation != recoverable?.Generation ?
            "Siguiente paso · Abre Recuperar archivos y verifica este respaldo. Después recupera una muestra en una carpeta nueva." :
            restored is null ? "Siguiente paso · Recupera una muestra en una carpeta nueva y abre los archivos para comprobarlos." :
            "Copia verificada. Puedes abrir la última carpeta recuperada de este trabajo.";
        BackupJobButton.Content = lastBackup is { Running: false, Result.State: not (ProductRunState.Confirmed or ProductRunState.Unchanged) } or { Running: false, Result: null }
            ? "↻ _Reintentar respaldo" : "▷ _Respaldar ahora";
        if (job is not null)
        {
            var badge = RouteState(job, history);
            RouteBadgeText.Text = badge.Label;
            RouteBadge.Tag = badge.Tone;
        }
        RouteBackupDate.Text = RouteDate(correct?.Result?.FinishedUtc);
        RouteVerifyDate.Text = RouteDate(verified?.Result?.FinishedUtc);
        var cloud = job is not null ? cloudSnapshots.GetValueOrDefault(job.Id) : null;
        string cloudText = cloud is { Configured: true }
            ? "Copia en nube · " + cloud.Label +
                (cloud.LastConfirmed is { } date ? " · Último envío " + RouteDate(date) : "") +
                (cloud.Outstanding > 0 ? $" · Pendientes {cloud.Outstanding:N0}" : "") +
                (cloud.Guidance.Length > 0 ? "\n" + cloud.Guidance : "")
            : "";
        if (RouteCloudStatus.Text != cloudText) RouteCloudStatus.Text = cloudText;
        RouteCloudStatus.Visibility = cloudText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshRouteNextAttempt();
        RuntimeStatusText.Text = job is null ? "" : Brief(history) +
            (recoverable?.Generation is { } generation ? $"\nPunto recuperable del {VersionStore.GenerationTime(generation).ToLocalTime():g}" : "") +
            (verified?.Generation is { } verifiedGeneration ? $"\nPunto verificado del {VersionStore.GenerationTime(verifiedGeneration).ToLocalTime():g}" : "") +
            (active?.Progress is { } progress ? $"\n{Phase(progress.Work.Phase)} · {progress.Work.Processed:N0}/{progress.Work.Total:N0}" +
                (progress.Paused ? " · Pausado" : "") + (progress.Work.RelativePath is { } path ? "\n" + path : "") : "") +
            (active?.Restore is { } restoring ? "\nCarpeta de recuperación · " +
                Path.Combine(restoring.Target.Parent.VolumePath, restoring.Target.FolderName) : "") +
            (history.Runs.FirstOrDefault(run => run.RetryRunning)?.Retry is { } retry ? $"\nPróximo reintento · {retry.NextUtc.ToLocalTime():g}" : "") +
            (history.Unreadable > 0 ? $"\n{history.Unreadable} registros ilegibles conservados para revisión." : "");
        RefreshHistoryPage(job?.Id, history);
        PauseRunButton.IsEnabled = CancelRunButton.IsEnabled = !busy && active is not null;
        RunControlsPanel.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        OperationStatusText.Text = active?.Progress is { } current ?
            Phase(current.Work.Phase) + $" · {current.Work.Processed:N0}/{current.Work.Total:N0}" + (current.Paused ? " · Pausado" : "") : "";
        OperationStatusText.Visibility = OperationStatusText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PauseRunButton.Content = active?.Progress?.Paused == true ? "_Continuar" : "_Pausar";
        RunProgress.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        RunProgress.IsIndeterminate = active?.Progress?.Paused != true && active?.Progress?.Work.Total is not > 0;
        RunProgress.Maximum = Math.Max(1, active?.Progress?.Work.Total ?? 1);
        RunProgress.Value = active?.Progress?.Work.Processed ?? 0;
        if (latest is null && job is not null && IsJobRunning(job.Id)) RuntimeStatusText.Text = "Iniciando ejecutor…";
    }

    private static string RouteDate(DateTimeOffset? date) =>
        date?.ToLocalTime().ToString("d MMM yyyy · HH:mm", System.Globalization.CultureInfo.GetCultureInfo("es-CO")) ?? "Todavía no disponible";

    private void RefreshRouteNextAttempt()
    {
        var job = (JobList.SelectedItem as JobRow)?.Job;
        const string next = "Próximo horario · ";
        string schedule = ScheduleStatusText.Text;
        RouteNextAttempt.Text = job is null ? "" : !job.Enabled || job.Retired ? "Desactivado" :
            runSnapshots.TryGetValue(job.Id, out var history) &&
                history.Runs.FirstOrDefault(run => run.RetryRunning)?.Retry is { } retry ? RouteDate(retry.NextUtc) :
            schedule.StartsWith(next, StringComparison.Ordinal) ? schedule[next.Length..].Replace(" · Requiere sesión iniciada", "", StringComparison.Ordinal) :
            job.WatchMode == ProductWatchMode.Watching ? "Al detectar cambios" :
            job.WatchMode == ProductWatchMode.Paused ? "Vigilancia pausada" :
            job.Options.DailyAt is null ? "A demanda" : "Consulta el horario";
    }

    private static (string Label, string Tone) RouteState(ProductJob job, ProductRunHistory history)
    {
        if (job.Retired || !job.Enabled) return (job.Retired ? "Retirado" : "Deshabilitado", "Attention");
        string activity = ActivityBrief(history);
        if (history.Unreadable > 0 || Brief(history) != activity) return ("Requiere atención", "Attention");
        return activity switch
        {
            "Verificación completa" => (history.Runs.FirstOrDefault(run =>
                run.Intent.Operation == ProductRunOperation.Backup && run.Result?.State == ProductRunState.Confirmed)?.Generation
                == history.Runs[0].Generation ? "✓ Verificado" : "✓ Punto verificado", ""),
            "Respaldo completo" => ("✓ Completo", ""),
            "Ediciones comprobadas" => ("Ediciones comprobadas", ""),
            "Archivos observados sin cambios" => ("Comparación acotada", ""),
            "Sin cambios desde el último punto" => ("Sin cambios", ""),
            "Recuperación completa" => ("✓ Recuperado", ""),
            "Esperando disco de destino" => ("Esperando disco", "Attention"),
            "Espacio insuficiente" => ("Espacio insuficiente", "Attention"),
            "Revisión del espejo lista" => ("Revisión pendiente", "Attention"),
            "Pausado" or "En ejecución" => (activity, ""),
            _ when history.Runs.Length == 0 => ("Sin respaldos", ""),
            _ => ("Requiere atención", "Attention")
        };
    }
    internal static string Brief(ProductRunHistory history)
    {
        string text = ActivityBrief(history);
        foreach (var (operation, label) in new[] { (ProductRunOperation.Backup, "respaldo"), (ProductRunOperation.Verify, "verificación") })
        {
            var latest = history.Runs.FirstOrDefault(run => run.Intent.Operation == operation);
            if (latest is { Running: false } && latest != history.Runs.FirstOrDefault() &&
                (latest.Result is null || latest.Result.State is not (ProductRunState.Confirmed or ProductRunState.Unchanged) || latest.Result.Degraded))
                text += $" · Último intento de {label} requiere atención";
        }
        return text;
    }

    private static string ActivityBrief(ProductRunHistory history) => history.Runs.FirstOrDefault() is not { } last ? "Sin respaldos · Todavía sin ejecutar" :
        last.Running ? last.Progress?.Paused == true ? "Pausado" : "En ejecución" :
        last.Result is { } result ? result.State switch
        {
            ProductRunState.Confirmed when result.Scoped => "Ediciones comprobadas",
            ProductRunState.Unchanged when result.Scoped => "Archivos observados sin cambios",
            ProductRunState.Confirmed => result.Degraded ? "Verificado; catálogo requiere atención" : last.Intent.Operation switch
            {
                ProductRunOperation.ReviewMirror => last.Mirror?.ResumePending == true ? "Actualización pendiente de reanudar" : "Revisión del espejo lista",
                ProductRunOperation.ReviewAbandon => "Plan pendiente revisado",
                ProductRunOperation.AbandonVersion => "Plan anterior conservado; crea un respaldo nuevo",
                ProductRunOperation.Restore => "Recuperación completa",
                ProductRunOperation.Verify when last.Generation is { } point && VersionStore.IsPartialPoint(point) => "Punto parcial verificado",
                ProductRunOperation.Verify => "Verificación completa",
                _ => "Respaldo completo"
            },
            ProductRunState.Cancelled => "Parcial · Cancelado; puede haber trabajo pendiente",
            ProductRunState.WaitingForSource => "Esperando origen",
            ProductRunState.WaitingForDestination => "Esperando disco de destino",
            ProductRunState.Busy => "Archivo o destino en uso",
            ProductRunState.Unchanged => "Sin cambios desde el último punto",
            _ when LabWorkspace.IsDiskFull(result.ErrorCode) => "Espacio insuficiente",
            _ => "Requiere atención"
        } : "Interrumpido; revisa o reintenta";

    private static string Phase(WorkPhase phase) => phase switch
    {
        WorkPhase.Analyzing => "Analizando", WorkPhase.Copying => "Copiando contenido", WorkPhase.Verifying => "Verificando",
        WorkPhase.Publishing => "Publicando", WorkPhase.Preparing => "Preparando trabajo", _ => "Recuperando"
    };

    private static string ResultText(ProductRunView run, ProductRunResult result) => (result.State switch
    {
        ProductRunState.Confirmed when result.Scoped => "Se respaldaron y comprobaron los archivos observados. La comparación completa se registra por separado.",
        ProductRunState.Unchanged when result.Scoped => "Los archivos observados coinciden con el punto anterior.",
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
        ProductRunState.Confirmed when run.Intent.Operation == ProductRunOperation.Verify &&
            run.Generation is { } point && VersionStore.IsPartialPoint(point) => "Se verificaron los archivos del punto parcial.",
        ProductRunState.Confirmed => run.Intent.Operation == ProductRunOperation.Verify ? "Verificación completa del contenido." +
            (result.Degraded ? " Una réplica del catálogo necesita revisión." : "") : "Respaldo completo y verificado.",
        ProductRunState.Cancelled when run.Intent.Operation == ProductRunOperation.Restore =>
            "Recuperación cancelada. Revisa una carpeta nueva para reintentar; los archivos ya recuperados se conservan.",
        ProductRunState.Cancelled when run.Intent.Operation == ProductRunOperation.ReviewMirror =>
            "Revisión cancelada. Repite el análisis para revisar el espejo antes de aplicarlo.",
        ProductRunState.Cancelled => "Operación cancelada en un límite recuperable. Reintenta para continuar el trabajo pendiente.",
        ProductRunState.WaitingForSource => "El origen no está disponible. Los puntos anteriores se conservan. Reconecta la misma carpeta y reintenta.",
        ProductRunState.WaitingForDestination => "El disco o carpeta de destino no está disponible. Los resultados anteriores se conservan. Reconecta el destino registrado y reintenta.",
        ProductRunState.Busy when result.Generation is { } partial && VersionStore.IsPartialPoint(partial) =>
            "Respaldo completo pendiente. Abre Recuperar para consultar el punto parcial con los archivos legibles.",
        ProductRunState.Busy => "Un archivo o el destino está en uso. Cierra la aplicación que lo usa o espera a que termine la otra operación. Luego reintenta.",
        ProductRunState.Unchanged => "La comparación no encontró cambios. La fecha del último respaldo completo y la última verificación se conservan.",
        _ when LabWorkspace.IsDiskFull(result.ErrorCode) =>
            "Espacio insuficiente. Libera espacio fuera de los archivos internos del respaldo y vuelve a intentar la operación.",
        _ => (result.Detail ?? "La operación necesita revisión antes de continuar.") +
            " Conserva los datos y el historial. Comprueba las carpetas registradas y resuelve la incidencia antes de reintentar."
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
