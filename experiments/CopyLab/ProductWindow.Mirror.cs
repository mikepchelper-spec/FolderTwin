using System.Diagnostics;
using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private Process StartMirrorReviewProcess(string location, Guid id) =>
        testMirrorReviewWorker?.Invoke(location, id) ?? ProductWorker.StartMirrorReview(location, id);

    private void RefreshMirrorReviewDisplay(ProductJob? job, bool executing)
    {
        var latest = job is not null && runSnapshots.TryGetValue(job.Id, out var history) ?
            history.Runs.FirstOrDefault(run => run.Intent.Operation is ProductRunOperation.Backup or ProductRunOperation.ReviewMirror) : null;
        if (job is null || job.Options.Mode != ProductBackupMode.ProtectedMirror || executing ||
            latest is not { Intent.Operation: ProductRunOperation.ReviewMirror, Result.State: ProductRunState.Confirmed, Mirror: { } review })
        {
            if (mirrorReviewRun is not null) ResetProductActions();
            return;
        }
        if (mirrorReviewRun != latest.Intent.Id)
        {
            ResetProductActions();
            mirrorReview = review;
            mirrorJobId = job.Id;
            mirrorReviewRun = latest.Intent.Id;
        }
        MirrorReviewPanel.Visibility = Visibility.Visible;
        MirrorReviewText.Text = review.ResumePending ?
            "Hay una actualización previamente autorizada pendiente de terminar. Se reanudará desde su diario, conservando el punto anterior." :
            $"Revisión del espejo\nArchivos: {review.Added:N0} nuevos · {review.Replaced:N0} modificados · {review.Removed:N0} retirados.\n" +
            $"Carpetas: {review.AddedDirectories:N0} nuevas · {review.RemovedDirectories:N0} retiradas.\n" +
            $"{review.Conflicts:N0} conflictos · Estimación: {review.EstimatedBytes:N0} bytes · Espacio observado: {review.AvailableBytes / (1024d * 1024 * 1024):N1} GiB.\n" +
            (review.Conflicts > 0 ? "Conserva y resuelve las ediciones externas antes de repetir el análisis." +
                (review.FirstConflict is { } path ? "\nPrimera ruta: " + path : "") :
                "El estado anterior queda recuperable antes de reemplazar o retirar archivos. Revisa los cambios antes de aplicar.");
        AcceptEmptySource.Visibility = !review.ResumePending && review.RequiresEmptyReview ? Visibility.Visible : Visibility.Collapsed;
        AcceptMassChanges.Visibility = !review.ResumePending && review.RequiresMassReview ? Visibility.Visible : Visibility.Collapsed;
        ApplyMirrorButton.Content = review.ResumePending ? "Reanudar _actualización" : "Aplicar _espejo revisado";
        ApplyMirrorButton.IsEnabled = !busy && job.Enabled && !job.Retired && review.Conflicts == 0;
    }
}
