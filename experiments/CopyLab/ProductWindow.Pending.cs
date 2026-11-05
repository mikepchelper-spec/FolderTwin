using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private Guid? pendingReviewRun;

    private void RefreshPendingReviewDisplay(ProductJob? job, bool executing)
    {
        if (PendingVersionPanel is null) return;
        bool available = !busy && !executing && job?.Options.Mode == ProductBackupMode.Versions;
        PendingVersionPanel.Visibility = job?.Options.Mode == ProductBackupMode.Versions ? Visibility.Visible : Visibility.Collapsed;
        ReviewPendingButton.IsEnabled = available;
        var latest = job is not null && runSnapshots.TryGetValue(job.Id, out var history) ? history.Runs.FirstOrDefault(run =>
            run.Intent.Operation is ProductRunOperation.Backup or ProductRunOperation.ReviewAbandon or ProductRunOperation.AbandonVersion) : null;
        var review = latest is { Intent.Operation: ProductRunOperation.ReviewAbandon, Result.State: ProductRunState.Confirmed } ? latest.Abandon : null;
        Guid? reviewed = review is not null ? latest!.Intent.Id : null;
        if (pendingReviewRun != reviewed)
        {
            pendingReviewRun = reviewed;
            AcceptPendingPlan.IsChecked = false;
        }
        PendingReviewText.Text = review is null ? "" : review.ResumePending ?
            "Hay una conservación de plan interrumpida. Reanúdala desde su diario antes de copiar o limpiar. No necesita el origen." :
            $"Plan del {VersionStore.GenerationTime(review.Generation).ToLocalTime():g} · {review.Proof!.Files:N0} archivos previstos.\n" +
            "Se conservan el inventario y los archivos ya copiados. No se eliminan objetos ni se publica un respaldo completo. " +
            "Podrás crear un respaldo nuevo después; la retención manual revisará por separado los objetos parciales.";
        AcceptPendingPlan.Visibility = review is not { ResumePending: false } ? Visibility.Collapsed : Visibility.Visible;
        ApplyPendingButton.Visibility = review is null ? Visibility.Collapsed : Visibility.Visible;
        ApplyPendingButton.Content = review?.ResumePending == true ? "Reanudar conservación del plan" : "Conservar plan anterior";
        ApplyPendingButton.IsEnabled = available && review is not null && (review.ResumePending || AcceptPendingPlan.IsChecked == true);
    }

    private void PendingChoiceChanged(object sender, RoutedEventArgs e)
    {
        var job = (JobList.SelectedItem as JobRow)?.Job;
        RefreshPendingReviewDisplay(job, job is not null && IsJobRunning(job.Id));
    }

    private void ReviewPendingClick(object sender, RoutedEventArgs e)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { Options.Mode: ProductBackupMode.Versions } job) return;
        Start(() => StartWorkerAsync(job, location => ProductWorker.StartPendingReview(location, job.Id)));
    }

    private void ApplyPendingClick(object sender, RoutedEventArgs e)
    {
        if (busy || !ApplyPendingButton.IsEnabled || pendingReviewRun is not { } reviewed ||
            (JobList.SelectedItem as JobRow)?.Job is not { Options.Mode: ProductBackupMode.Versions } job) return;
        Start(() => StartWorkerAsync(job, location => testAbandonWorker?.Invoke(location, job.Id, reviewed) ??
            ProductWorker.StartAbandonVersion(location, job.Id, reviewed)));
    }
}
