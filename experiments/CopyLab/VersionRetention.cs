using System.Globalization;
using System.IO;
using System.Windows;

namespace CopyLab;

public partial class VersionWindow
{
    private sealed record CleanupRow(RetentionOperation Operation)
    {
        public string Label => Operation.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " · " + (Operation.State switch
        {
            "moving" => "movimientos pendientes",
            "quarantined" => "en cuarentena",
            "undoing" => "deshacer pendiente",
            "undone" => "deshecha",
            "purging" => "purga pendiente",
            _ => "purgada"
        }) + $" · {Operation.Objects:N0} objetos";
    }
    private RetentionPreview? retentionReview;

    private void PreviewRetentionClick(object sender, RoutedEventArgs e)
    {
        if (busy || store is not { } current)
            return;
        Start(async () =>
        {
            retentionReview = null;
            AcceptRetention.IsChecked = false;
            int Number(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value :
                throw new InvalidDataException("Escribe números enteros para la política de retención.");
            var policy = new RetentionPolicy(Number(RetentionDays.Text), Number(RetentionWeeks.Text),
                Number(RetentionMonths.Text), Number(RetentionGrace.Text));
            var preview = await Task.Run(() => current.PreviewRetention(policy));
            retentionReview = preview;
            RetentionText.Text = $"Se conservarán {preview.Keep.Length:N0} fechas y expirarán {preview.Expire.Length:N0}. " +
                $"{preview.Objects.Length:N0} objetos ({preview.QuarantineBytes:N0} bytes) irán a cuarentena. " +
                $"Gracia: {policy.GraceDays} días desde que terminen los movimientos. Calendario UTC.\n" +
                string.Join("\n", preview.Expire.Take(10).Select(id => VersionStore.GenerationTime(id).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))) +
                (preview.Expire.Length > 10 ? $"\nY {preview.Expire.Length - 10:N0} fechas adicionales." : "");
            SetStatus("Revisión terminada. Los objetos compartidos por fechas conservadas permanecen en el almacén.");
        });
    }

    private void ApplyRetentionClick(object sender, RoutedEventArgs e)
    {
        if (!busy && store is { } current && retentionReview is { } review && AcceptRetention.IsChecked == true)
            Start(async () =>
            {
                try { await Task.Run(() => current.BeginRetention(review)); }
                finally { retentionReview = null; AcceptRetention.IsChecked = false; await RefreshAsync(); }
                SetStatus("Cuarentena terminada. Puedes deshacerla; no se ha liberado espacio.");
            });
    }

    private void ResumeRetentionClick(object sender, RoutedEventArgs e) => RunCleanup((store, id) => store.ResumeRetention(id),
        "Movimientos reanudados. La cuarentena conserva los objetos retirados.");
    private void UndoRetentionClick(object sender, RoutedEventArgs e) => RunCleanup((store, id) => store.UndoRetention(id),
        "Cuarentena deshecha. Las fechas expiradas vuelven a estar retenidas.");
    private void PurgeRetentionClick(object sender, RoutedEventArgs e)
    {
        if (AcceptPurge.IsChecked == true)
            RunCleanup((store, id) => store.PurgeRetention(id), "Purga terminada. Los objetos borrados ya no son recuperables desde esta cuarentena.");
    }

    private void RunCleanup(Action<VersionStore, string> action, string message)
    {
        if (!busy && store is { } current && RetentionChoice.SelectedItem is CleanupRow row)
            Start(async () =>
            {
                try { await Task.Run(() => action(current, row.Operation.Id)); }
                finally { retentionReview = null; AcceptRetention.IsChecked = false; AcceptPurge.IsChecked = false; await RefreshAsync(); }
                SetStatus(message);
            });
    }

    private void RetentionChoiceChanged(object sender, RoutedEventArgs e) => RefreshActions();

    private async Task ShowRetentionAsync()
    {
        if (store is not { } current)
            return;
        string? selected = (RetentionChoice.SelectedItem as CleanupRow)?.Operation.Id;
        try
        {
            var rows = await Task.Run(() => current.ListRetention().OrderByDescending(item => item.CreatedUtc)
                .Select(item => new CleanupRow(item)).ToArray());
            RetentionChoice.ItemsSource = rows;
            RetentionChoice.SelectedItem = rows.FirstOrDefault(row => row.Operation.Id == selected && row.Operation.State is not ("undone" or "purged")) ??
                rows.FirstOrDefault(row => row.Operation.State is not ("undone" or "purged")) ??
                rows.FirstOrDefault(row => row.Operation.Id == selected) ?? rows.FirstOrDefault();
        }
        catch (Exception error) when (IsExpected(error))
        {
            RetentionChoice.ItemsSource = null;
            retentionReview = null;
            RetentionText.Text = "Limpieza suspendida: " + error.Message;
        }
    }

    private void RefreshRetentionActions()
    {
        if (PreviewRetentionButton is null)
            return;
        bool ready = !busy && store is not null;
        PreviewRetentionButton.IsEnabled = ready;
        ApplyRetentionButton.IsEnabled = ready && retentionReview is { Expire.Length: > 0 } && AcceptRetention.IsChecked == true;
        RetentionChoice.IsEnabled = ready;
        string? state = (RetentionChoice.SelectedItem as CleanupRow)?.Operation.State;
        ResumeRetentionButton.IsEnabled = ready && state == "moving";
        UndoRetentionButton.IsEnabled = ready && state is "moving" or "quarantined" or "undoing";
        PurgeRetentionButton.IsEnabled = ready && state is "quarantined" or "purging" && AcceptPurge.IsChecked == true;
    }
}
