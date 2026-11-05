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
    private sealed record RestoreChoice(string Generation, DedicatedDestinationReview Destination, string? RelativeFile = null);
    private ProductMirrorReview? mirrorReview;
    private Guid? mirrorJobId;
    private Guid? mirrorReviewRun;
    private ProductJob? recoveryJob;
    private RestoreChoice? restoreChoice;

    private void ResetProductActions()
    {
        mirrorReview = null;
        mirrorJobId = null;
        mirrorReviewRun = null;
        MirrorReviewPanel.Visibility = Visibility.Collapsed;
        AcceptEmptySource.IsChecked = AcceptMassChanges.IsChecked = false;
    }

    private void RefreshProductActions()
    {
        var job = (JobList.SelectedItem as JobRow)?.Job;
        bool executing = job is not null && IsJobRunning(job.Id);
        bool available = !busy && job is not null && !executing;
        SelectedJobTitle.Text = job?.Options.Name ?? "Selecciona un trabajo";
        SelectedJobDetails.Visibility = job is null ? Visibility.Collapsed : Visibility.Visible;
        RecoverJobButton.IsEnabled = available;
        BackupJobButton.IsEnabled = available && job is { Enabled: true, Retired: false };
        SaveSettingsButton.IsEnabled = available;
        ExportConfigurationButton.IsEnabled = available;
        ReviewImportedSourceButton.IsEnabled = available && job is { Imported: true, Enabled: false };
        ApplyScheduleButton.IsEnabled = available && job is { Enabled: true, Retired: false, Options.DailyAt: not null };
        SelectedJobText.Text = job is null ? "" : $"Origen\n{job.Source.VolumePath}\nDestino\n{job.Destination.LastVolumePath}";
        if (mirrorJobId != job?.Id) ResetProductActions();
        RefreshMirrorReviewDisplay(job, executing);
        RefreshPendingReviewDisplay(job, executing);
        RefreshRuntimeDisplay();
        RefreshProductRetentionActions();
        ApplicationChoiceChanged(this, new RoutedEventArgs());
        if (RecoveryPanel.Visibility == Visibility.Visible && recoveryJob?.Id == job?.Id)
        {
            bool ready = available && RecoveryDates.SelectedItem is RecoveryDate { Point.Expired: false, Point.Problem: null };
            RecoveryDates.IsEnabled = !busy && !executing;
            VerifyPointButton.IsEnabled = ReviewRestoreButton.IsEnabled = ready;
            RestorePointButton.IsEnabled = ready && restoreChoice is not null;
            RefreshRecoverySelection();
        }
    }

    private void BackupJobClick(object sender, RoutedEventArgs e)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { Enabled: true, Retired: false } job) return;
        ResetProductActions();
        Start(async () =>
        {
            await StartWorkerAsync(job, location => job.Options.Mode == ProductBackupMode.ProtectedMirror ?
                StartMirrorReviewProcess(location, job.Id) : StartBackupProcess(location, job.Id));
        });
    }

    private void ApplyMirrorClick(object sender, RoutedEventArgs e)
    {
        if (busy || mirrorReview is not { } review || mirrorReviewRun is not { } reviewRun ||
            (JobList.SelectedItem as JobRow)?.Job is not { } job || job.Id != mirrorJobId) return;
        bool empty = AcceptEmptySource.IsChecked == true, mass = AcceptMassChanges.IsChecked == true;
        Start(async () =>
        {
            if (review.ResumePending)
            {
                await StartWorkerAsync(job, location => StartBackupProcess(location, job.Id));
                ResetProductActions();
                return;
            }
            Guid approval = await Task.Run(() =>
            {
                using var registry = openRegistry();
                return registry.ApproveMirror(job.Id, reviewRun, empty, mass);
            });
            await StartWorkerAsync(job, location => StartBackupProcess(location, job.Id, approval));
            ResetProductActions();
        });
    }

    private void RecoverJobClick(object sender, RoutedEventArgs e)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { } job) return;
        Start(async () =>
        {
            var points = await Task.Run(() =>
            {
                var store = VersionStore.OpenProduct(job);
                var available = store.ListPoints();
                return job.Options.Mode == ProductBackupMode.Accumulative ?
                    available.Where(point => point.Id == store.RegisteredGenerations().MirrorGeneration).ToArray() : available;
            });
            if (points.Length == 0) throw new InvalidDataException("Este trabajo todavía no tiene un respaldo confirmado.");
            recoveryJob = job;
            JobListPanel.Visibility = Visibility.Collapsed;
            RecoveryPanel.Visibility = Visibility.Visible;
            RecoveryDates.ItemsSource = points.Select(point => new RecoveryDate(point)).ToArray();
            RecoveryDates.SelectedIndex = 0;
            await LoadRecoveryPointAsync();
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
        RecoveryComparison.Text = "";
        VerifyPointButton.IsEnabled = ReviewRestoreButton.IsEnabled = SearchRecoveryButton.IsEnabled = false;
        PreviousRecoveryPageButton.IsEnabled = NextRecoveryPageButton.IsEnabled = RecoveryParentButton.IsEnabled = false;
        RecoveryOnlyChanges.IsEnabled = false;
        recoveryCatalogs = null;
        recoveryBrowser = null;
        recoveryIndexKey = null;
        recoveryComparisonId = null;
        recoveryDirectory = "";
        recoveryQuery = "";
        recoveryOffset = 0;
        RecoverySearch.Text = "";
        RecoveryOnlyChanges.IsChecked = false;
        if (row.Point.Problem is { } problem) throw new InvalidDataException(problem);
        var previous = RecoveryDates.Items.OfType<RecoveryDate>().SkipWhile(candidate => candidate.Point.Id != row.Point.Id)
            .Skip(1).FirstOrDefault(candidate => candidate.Point is { Problem: null, Expired: false });
        var loaded = await Task.Run(() =>
        {
            var store = VersionStore.OpenProduct(job);
            CatalogSnapshot current;
            CatalogSnapshot? before;
            string? previousId = previous?.Point.Id;
            try { (current, before) = store.ReadCatalogs(row.Point.Id, previousId); }
            catch (Exception error) when (previousId is not null && VersionJob.Expected(error))
            {
                (current, before) = store.ReadCatalogs(row.Point.Id, null);
                previousId = null;
            }
            string key = VersionIndex.Key(current, before);
            VersionBrowser Build() => new(current, before);
            try { productIndex.Ensure(key, Build); return (current, before, previousId, Key: (string?)key, Fallback: (VersionBrowser?)null); }
            catch (Exception error) when (VersionIndex.IsUnavailable(error))
            { return (current, before, previousId, Key: (string?)null, Fallback: (VersionBrowser?)Build()); }
        });
        recoveryCatalogs = (loaded.current, loaded.before);
        recoveryComparisonId = loaded.previousId;
        recoveryIndexKey = loaded.Key;
        recoveryBrowser = loaded.Fallback;
        SearchRecoveryButton.IsEnabled = true;
        VerifyPointButton.IsEnabled = ReviewRestoreButton.IsEnabled = !row.Point.Expired;
        RecoveryOnlyChanges.IsEnabled = loaded.previousId is not null;
        RecoveryComparison.Text = loaded.previousId is null ? "Sin un punto anterior disponible para comparar." :
            $"Comparando {row.Label} con {previous!.Label}. Los eliminados se recuperan desde el punto anterior.";
        RecoverySummary.Text = $"{loaded.current.Files.Length:N0} archivos · {loaded.current.Directories.Length:N0} carpetas. " +
            (row.Point.Expired ? "Punto expirado. Deshaz su cuarentena antes de recuperarlo." : "La recuperación crea una carpeta nueva.") +
            (loaded.current.Degraded ? " Solo una réplica del catálogo está íntegra." : "");
        await LoadRecoveryPageAsync();
        SetStatus("Busca un archivo o abre una carpeta para revisar su contenido." + IndexNote());
    }

    private void VerifyPointClick(object sender, RoutedEventArgs e)
    {
        if (busy || recoveryJob is not { } job || RecoveryDates.SelectedItem is not RecoveryDate { Point.Expired: false } row) return;
        Start(async () =>
        {
            await StartWorkerAsync(job, location => ProductWorker.StartVerification(location, job.Id, row.Point.Id));
        });
    }

    private void ReviewRestoreClick(object sender, RoutedEventArgs e) => ReviewRestore(relativeFile: null, fromPrevious: false);

    private void ReviewRestore(string? relativeFile, bool fromPrevious)
    {
        if (busy || recoveryJob is not { } job || RecoveryDates.SelectedItem is not RecoveryDate row || row.Point.Expired && !fromPrevious ||
            ChooseFolder("Elige dónde crear la carpeta recuperada") is not { } parentPath) return;
        Start(async () =>
        {
            var destination = await Task.Run(() => LocalJobRoots.ReviewDedicatedDestination(job.Destination.LastVolumePath, parentPath));
            string target = Path.Combine(destination.Parent.VolumePath, destination.FolderName);
            if (LocalJobRoots.Overlap(target, job.Source.VolumePath))
                throw new InvalidDataException("Elige una carpeta separada del origen para recuperar los archivos.");
            string generation = fromPrevious ? recoveryComparisonId ??
                throw new InvalidDataException("El punto anterior no está disponible para este archivo.") : row.Point.Id;
            restoreChoice = new(generation, destination, relativeFile);
            RestoreReviewText.Text = $"Se recuperará {(relativeFile ?? "el punto completo")} del {VersionStore.GenerationTime(generation).ToLocalTime():g} " +
                $"en una carpeta nueva\n{target}";
            RestorePointButton.IsEnabled = true;
            SetStatus("Revisa dónde se recuperarán los archivos antes de continuar.");
        });
    }

    private void RestorePointClick(object sender, RoutedEventArgs e)
    {
        if (busy || recoveryJob is not { } job || restoreChoice is not { } choice) return;
        Start(async () =>
        {
            Guid request = await Task.Run(() =>
            {
                using var registry = openRegistry();
                return registry.ApproveRestore(job.Id, choice.Generation, choice.Destination, choice.RelativeFile);
            });
            await StartWorkerAsync(job, location => StartRestoreProcess(location, job.Id, request));
            ClearRestoreReview();
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
