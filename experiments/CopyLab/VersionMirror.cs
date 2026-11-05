using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;

namespace CopyLab;

public partial class VersionWindow
{
    private MirrorPreview? mirrorReview;
    private MirrorOperation? pendingMirror;
    private bool mirrorConfigured;
    private bool mirrorStateKnown;

    private void CreateMirrorClick(object sender, RoutedEventArgs e)
    {
        if (!busy && store is { } current)
            Start(async () =>
            {
                await Task.Run(current.InitializeMirror);
                await ShowMirrorAsync();
                SetStatus("Espejo preparado. Elige un origen y revisa la primera actualización.");
            });
    }

    private void ReviewMirrorClick(object sender, RoutedEventArgs e)
    {
        if (busy || store is not { } current || source is not { } origin)
            return;
        Start(async () =>
        {
            ResetMirrorReview();
            if (!int.TryParse(MirrorThreshold.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int threshold))
                throw new InvalidDataException("Escribe un porcentaje entero entre 1 y 100.");
            var preview = await Task.Run(() => current.PreviewMirror(origin, threshold));
            mirrorReview = preview;
            MirrorText.Text = $"{preview.Added:N0} archivos nuevos, {preview.Replaced:N0} reemplazos, {preview.Removed:N0} retiradas, " +
                $"{preview.Conflicts.Length:N0} conflictos. Carpetas nuevas: {preview.AddedDirectories:N0}. " +
                $"Carpetas retiradas: {preview.RemovedDirectories:N0}. Se prepararán {preview.StageBytes:N0} bytes. " +
                $"Estimación de datos: {preview.EstimatedBytes:N0} bytes más reserva. Disponible: {preview.AvailableBytes:N0} bytes. " +
                $"Umbral revisado: {preview.MaximumChangePercent} %." +
                (preview.Conflicts.Length == 0 ? "" : "\nConflictos: " + string.Join(", ", preview.Conflicts.Take(10))) +
                (preview.RequiresEmptyReview ? "\nEl origen está vacío." : "") +
                (preview.RequiresMassReview ? "\nEl alcance exige revisión adicional." : "");
            SetStatus(preview.Conflicts.Length == 0 ? "Revisión del espejo lista." : "El destino contiene conflictos que requieren revisión.");
        });
    }

    private void ApplyMirrorClick(object sender, RoutedEventArgs e)
    {
        if (busy || store is not { } current || mirrorReview is not { } preview || source != preview.SourcePlan.Source ||
            AcceptMirror.IsChecked != true || preview.Conflicts.Length != 0 || !MatchesMirrorThreshold(preview))
            return;
        bool empty = AcceptEmptyMirror.IsChecked == true;
        bool mass = AcceptMassMirror.IsChecked == true;
        Start(async () =>
        {
            string generation;
            try { generation = await Task.Run(() => current.BeginMirror(preview, empty, mass)); }
            finally { ResetMirrorReview(); await ShowMirrorAsync(); }
            await RefreshAsync(generation);
            SetStatus("Espejo actualizado y verificado. Puedes abrir la carpeta actual o recuperar una fecha anterior.");
        });
    }

    private void ResumeMirrorClick(object sender, RoutedEventArgs e)
    {
        if (!busy && store is { } current && pendingMirror is { } pending)
            Start(async () =>
            {
                try { await Task.Run(() => current.ResumeMirror(pending.Generation)); }
                finally { ResetMirrorReview(); await ShowMirrorAsync(); }
                await RefreshAsync(pending.Generation);
                SetStatus("Actualización del espejo reanudada y verificada.");
            });
    }

    private void OpenMirrorFolderClick(object sender, RoutedEventArgs e)
    {
        if (busy || store is not { } current || !mirrorConfigured || pendingMirror is not null)
            return;
        Start(async () =>
        {
            await ShowMirrorAsync();
            if (mirrorStateKnown && mirrorConfigured && pendingMirror is null)
                Process.Start(new ProcessStartInfo(current.MirrorCurrentPath) { UseShellExecute = true });
        });
    }

    private void ResetMirrorReview()
    {
        mirrorReview = null;
        AcceptMirror.IsChecked = AcceptEmptyMirror.IsChecked = AcceptMassMirror.IsChecked = false;
    }

    private async Task ShowMirrorAsync()
    {
        mirrorStateKnown = false;
        pendingMirror = null;
        mirrorConfigured = false;
        if (store is not { } current)
            return;
        try
        {
            (mirrorConfigured, pendingMirror) = await Task.Run(current.MirrorStatus);
            mirrorStateKnown = true;
            if (mirrorReview is null)
                MirrorText.Text = pendingMirror is { } pending ?
                    pending.Ready ? "Actualización interrumpida. Reanuda los movimientos preparados." :
                        "Actualización pendiente. Reanuda con el origen disponible y tal como fue revisado." :
                    mirrorConfigured ? current.MirrorCurrentPath : "Prepara un espejo en este almacén para empezar.";
        }
        catch (Exception error) when (IsExpected(error))
        {
            ResetMirrorReview();
            MirrorText.Text = "El espejo requiere revisión. " + error.Message;
        }
    }

    private void MirrorChoiceChanged(object sender, RoutedEventArgs e) => RefreshActions();

    private void RefreshMirrorActions()
    {
        if (ResumeMirrorButton is null)
            return;
        bool ready = !busy && mirrorStateKnown && store?.SupportsRetention == true;
        CreateMirrorButton.IsEnabled = ready && !mirrorConfigured;
        ReviewMirrorButton.IsEnabled = ready && mirrorConfigured && pendingMirror is null && source is not null;
        OpenMirrorFolderButton.IsEnabled = ready && mirrorConfigured && pendingMirror is null;
        ResumeMirrorButton.IsEnabled = ready && pendingMirror is not null;
        AcceptMirror.IsEnabled = ReviewMirrorButton.IsEnabled && mirrorReview is { Conflicts.Length: 0 } review &&
            review.SourcePlan.Source == source && MatchesMirrorThreshold(review);
        AcceptEmptyMirror.IsEnabled = AcceptMirror.IsEnabled && mirrorReview?.RequiresEmptyReview == true;
        AcceptMassMirror.IsEnabled = AcceptMirror.IsEnabled && mirrorReview?.RequiresMassReview == true;
        ApplyMirrorButton.IsEnabled = AcceptMirror.IsEnabled && AcceptMirror.IsChecked == true &&
            (!AcceptEmptyMirror.IsEnabled || AcceptEmptyMirror.IsChecked == true) &&
            (!AcceptMassMirror.IsEnabled || AcceptMassMirror.IsChecked == true);
    }

    private bool MatchesMirrorThreshold(MirrorPreview preview) =>
        int.TryParse(MirrorThreshold.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int threshold) &&
        threshold == preview.MaximumChangePercent;
}
