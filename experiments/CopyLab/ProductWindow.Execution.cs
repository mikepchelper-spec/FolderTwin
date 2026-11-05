using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CopyLab;

public partial class ProductWindow
{
    private sealed record RecoveryDate(VersionPoint Point)
    {
        public string Label => Point.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) +
            $" · {Point.FileCount:N0} archivos" + (Point.Problem is not null ? " · Requiere revisión" :
                Point.Expired ? " · Expirado" : Point.Degraded ? " · Una réplica íntegra" : "");
    }
    private sealed record RestoreChoice(string Generation, DedicatedDestinationReview Destination);
    private MirrorPreview? mirrorReview;
    private Guid? mirrorJobId;
    private ProductJob? recoveryJob;
    private RestoreChoice? restoreChoice;

    private void ResetProductActions()
    {
        mirrorReview = null;
        mirrorJobId = null;
        MirrorReviewPanel.Visibility = Visibility.Collapsed;
        AcceptEmptySource.IsChecked = AcceptMassChanges.IsChecked = false;
    }

    private void RefreshProductActions()
    {
        var job = (JobList.SelectedItem as JobRow)?.Job;
        bool available = !busy && job?.Options.Mode is ProductBackupMode.Versions or ProductBackupMode.ProtectedMirror;
        BackupJobButton.IsEnabled = RecoverJobButton.IsEnabled = available;
        SelectedJobText.Text = job is null ? "" : $"Origen\n{job.Source.VolumePath}\nDestino\n{job.Destination.LastVolumePath}" +
            (job.Options.Mode == ProductBackupMode.Accumulative ? "\nLa ejecución acumulativa está en preparación." : "");
        if (mirrorJobId != job?.Id) ResetProductActions();
    }

    private void BackupJobClick(object sender, RoutedEventArgs e)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { } job) return;
        ResetProductActions();
        Start(async () =>
        {
            if (job.Options.Mode == ProductBackupMode.Versions)
            {
                string generation = await Task.Run(() =>
                {
                    var store = VersionStore.OpenProduct(job, initialize: true);
                    var pending = store.ListPending();
                    if (pending.Length > 1)
                        throw new InvalidDataException("Hay varios respaldos pendientes. Revisa las incidencias antes de continuar.");
                    string id = pending.SingleOrDefault()?.Id ?? VersionStore.NewGenerationId();
                    if (pending.Length == 1) store.ResumePoint(id, expectedSourceIdentity: job.Source.Identity);
                    else store.CreatePoint(job.Source.VolumePath, id);
                    return id;
                });
                SetStatus($"Respaldo completo · {VersionStore.GenerationTime(generation).ToLocalTime():g}.");
            }
            else if (job.Options.Mode == ProductBackupMode.ProtectedMirror)
            {
                mirrorReview = await Task.Run<MirrorPreview?>(() =>
                {
                    var store = VersionStore.OpenProduct(job, initialize: true);
                    store.InitializeMirror(resumeInitialization: true);
                    var pending = store.ListMirrorOperations().Where(operation => !operation.Applied).ToArray();
                    if (pending.Length > 1)
                        throw new InvalidDataException("Hay varias actualizaciones del espejo pendientes de revisar.");
                    if (pending.Length == 1)
                    {
                        store.ResumeMirror(pending[0].Generation);
                        return null;
                    }
                    return store.PreviewMirror(job.Source.VolumePath);
                });
                if (mirrorReview is null)
                {
                    SetStatus("Actualización pendiente del espejo completada y verificada.");
                    return;
                }
                mirrorJobId = job.Id;
                MirrorReviewPanel.Visibility = Visibility.Visible;
                MirrorReviewText.Text = $"Revisión del espejo\n{mirrorReview.Added:N0} nuevos · {mirrorReview.Replaced:N0} modificados · " +
                    $"{mirrorReview.Removed:N0} retirados · {mirrorReview.Conflicts.Length:N0} conflictos. " +
                    "El estado anterior queda recuperable antes de reemplazar o retirar archivos.";
                AcceptEmptySource.Visibility = mirrorReview.RequiresEmptyReview ? Visibility.Visible : Visibility.Collapsed;
                AcceptMassChanges.Visibility = mirrorReview.RequiresMassReview ? Visibility.Visible : Visibility.Collapsed;
                ApplyMirrorButton.IsEnabled = mirrorReview.Conflicts.Length == 0;
                SetStatus(mirrorReview.Conflicts.Length == 0 ? "Revisa los cambios antes de actualizar el espejo." :
                    "Hay cambios externos en el destino. Consérvalos y resuelve el conflicto antes de repetir el análisis.");
            }
        });
    }

    private void ApplyMirrorClick(object sender, RoutedEventArgs e)
    {
        if (busy || mirrorReview is not { } review || (JobList.SelectedItem as JobRow)?.Job is not { } job || job.Id != mirrorJobId) return;
        bool empty = AcceptEmptySource.IsChecked == true, mass = AcceptMassChanges.IsChecked == true;
        Start(async () =>
        {
            await Task.Run(() => VersionStore.OpenProduct(job).BeginMirror(review, empty, mass));
            ResetProductActions();
            SetStatus("Espejo actualizado y verificado. El contenido anterior sigue disponible para recuperar.");
        });
    }

    private void RecoverJobClick(object sender, RoutedEventArgs e)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { } job) return;
        Start(async () =>
        {
            var points = await Task.Run(() => VersionStore.OpenProduct(job).ListPoints());
            if (points.Length == 0) throw new InvalidDataException("Este trabajo todavía no tiene un respaldo confirmado.");
            recoveryJob = job;
            JobListPanel.Visibility = Visibility.Collapsed;
            RecoveryPanel.Visibility = Visibility.Visible;
            RecoveryDates.ItemsSource = points.Select(point => new RecoveryDate(point)).ToArray();
            RecoveryDates.SelectedIndex = 0;
            await LoadRecoveryPointAsync();
            RecoveryDates.Focus();
        });
    }

    private void RecoveryDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!busy && recoveryJob is not null && RecoveryDates.SelectedItem is RecoveryDate)
            Start(LoadRecoveryPointAsync);
    }

    private async Task LoadRecoveryPointAsync()
    {
        ClearRestoreReview();
        if (recoveryJob is not { } job || RecoveryDates.SelectedItem is not RecoveryDate row) return;
        RecoveryFiles.ItemsSource = null;
        RecoverySummary.Text = "";
        if (row.Point.Problem is { } problem) throw new InvalidDataException(problem);
        var catalog = await Task.Run(() => VersionStore.OpenProduct(job).ReadCatalogs(row.Point.Id, null).Current);
        RecoveryFiles.ItemsSource = catalog.Files.Take(250).Select(file => file.RelativePath).ToArray();
        RecoverySummary.Text = $"{catalog.Files.Length:N0} archivos · {catalog.Directories.Length:N0} carpetas. " +
            "La recuperación de este punto crea una carpeta nueva." + (catalog.Files.Length > 250 ? " Vista previa de las primeras 250 rutas." : "") +
            (catalog.Degraded ? " Solo una réplica del catálogo está íntegra." : "");
        SetStatus("Elige una fecha para revisar o recuperar el respaldo completo.");
    }

    private void VerifyPointClick(object sender, RoutedEventArgs e)
    {
        if (busy || recoveryJob is not { } job || RecoveryDates.SelectedItem is not RecoveryDate row) return;
        Start(async () =>
        {
            bool degraded = await Task.Run(() => VersionStore.OpenProduct(job).Verify(row.Point.Id));
            SetStatus("Verificación completa del contenido." + (degraded ? " Una réplica del catálogo necesita revisión." : ""));
        });
    }

    private void ReviewRestoreClick(object sender, RoutedEventArgs e)
    {
        if (busy || recoveryJob is not { } job || RecoveryDates.SelectedItem is not RecoveryDate row ||
            ChooseFolder("Elige dónde crear la carpeta recuperada") is not { } parentPath) return;
        Start(async () =>
        {
            var destination = await Task.Run(() => LocalJobRoots.ReviewDedicatedDestination(job.Destination.LastVolumePath, parentPath));
            string target = Path.Combine(destination.Parent.VolumePath, destination.FolderName);
            if (LocalJobRoots.Overlap(target, job.Source.VolumePath))
                throw new InvalidDataException("Elige una carpeta separada del origen para recuperar los archivos.");
            restoreChoice = new(row.Point.Id, destination);
            RestoreReviewText.Text = $"Se recuperará el punto del {row.Point.CreatedUtc.ToLocalTime():g} en una carpeta nueva\n{target}";
            RestorePointButton.IsEnabled = true;
            SetStatus("Revisa dónde se recuperarán los archivos antes de continuar.");
        });
    }

    private void RestorePointClick(object sender, RoutedEventArgs e)
    {
        if (busy || recoveryJob is not { } job || restoreChoice is not { } choice) return;
        Start(async () =>
        {
            string output = await Task.Run(() =>
            {
                using var roots = LocalJobRoots.CreateDedicatedDestination(choice.Destination);
                string target = roots.Review.Destination.VolumePath;
                VersionStore.OpenProduct(job).Restore(choice.Generation, target);
                return target;
            });
            ClearRestoreReview();
            SetStatus($"Recuperación completa y verificada.\n{output}");
        });
    }

    private void ClearRestoreReview()
    {
        restoreChoice = null;
        RestorePointButton.IsEnabled = false;
        RestoreReviewText.Text = "";
    }

    private void ReturnToJobsClick(object sender, RoutedEventArgs e)
    {
        if (!busy) { ClearRestoreReview(); ShowJobs(); }
    }
}
