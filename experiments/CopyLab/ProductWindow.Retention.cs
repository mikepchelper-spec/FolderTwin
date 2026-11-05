using System.Globalization;
using System.IO;
using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private sealed record ProductCleanupRow(RetentionOperation Operation)
    {
        public string Label => $"{Operation.CreatedUtc.ToLocalTime():g} · " + (Operation.State switch
        {
            "moving" => "movimientos pendientes", "quarantined" => "en cuarentena", "undoing" => "deshacer pendiente",
            "undone" => "deshecha", "purging" => "purga pendiente", "purged" => "purgada", _ => "requiere revisión"
        }) + $" · {Operation.Objects:N0} objetos";
    }
    private RetentionPreview? productRetentionReview;
    private Guid? productRetentionJob;

    private void ResetProductRetention()
    {
        if (ProductRetentionPanel is null) return;
        productRetentionReview = null;
        productRetentionJob = null;
        AcceptProductRetention.IsChecked = AcceptProductPurge.IsChecked = false;
        ProductRetentionChoice.ItemsSource = null;
        ProductRetentionText.Text = "";
        ProductRetentionPanel.IsExpanded = false;
        RefreshProductRetentionActions();
    }

    private void ProductRetentionExpanded(object sender, RoutedEventArgs e)
    {
        if (!busy && (JobList.SelectedItem as JobRow)?.Job is { } job)
            Start(() => LoadProductRetentionAsync(job));
    }

    private void ProductRetentionPolicyChanged(object sender, RoutedEventArgs e)
    {
        if (AcceptProductRetention is null) return;
        productRetentionReview = null;
        AcceptProductRetention.IsChecked = false;
        RefreshProductRetentionActions();
    }

    private void ProductRetentionChoiceChanged(object sender, RoutedEventArgs e) => RefreshProductRetentionActions();

    private void RefreshProductRetentionActions()
    {
        if (PreviewProductRetentionButton is null) return;
        var job = (JobList.SelectedItem as JobRow)?.Job;
        bool ready = !busy && job is not null && !IsJobRunning(job.Id);
        ProductRetentionPanel.IsEnabled = ready;
        PreviewProductRetentionButton.IsEnabled = ready;
        ApplyProductRetentionButton.IsEnabled = ready && productRetentionJob == job?.Id &&
            productRetentionReview is { } review && (review.Expire.Length > 0 || review.Objects.Length > 0) && AcceptProductRetention.IsChecked == true;
        string? state = (ProductRetentionChoice.SelectedItem as ProductCleanupRow)?.Operation.State;
        ResumeProductRetentionButton.IsEnabled = ready && state == "moving";
        UndoProductRetentionButton.IsEnabled = ready && state is "moving" or "quarantined" or "undoing";
        PurgeProductRetentionButton.IsEnabled = ready && state is "quarantined" or "purging" && AcceptProductPurge.IsChecked == true;
    }

    private void PreviewProductRetentionClick(object sender, RoutedEventArgs e)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { } job) return;
        Start(async () =>
        {
            productRetentionReview = null;
            AcceptProductRetention.IsChecked = false;
            int Number(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value :
                throw new InvalidDataException("Escribe números enteros para la política de retención.");
            var policy = new RetentionPolicy(Number(ProductRetentionDays.Text), Number(ProductRetentionWeeks.Text),
                Number(ProductRetentionMonths.Text), Number(ProductRetentionGrace.Text));
            var preview = await Task.Run(() => VersionStore.OpenProduct(job).PreviewRetention(policy));
            productRetentionReview = preview;
            productRetentionJob = job.Id;
            ProductRetentionText.Text = $"Se conservarán {preview.Keep.Length:N0} fechas y expirarán {preview.Expire.Length:N0}. " +
                $"{preview.Objects.Length:N0} objetos ({preview.QuarantineBytes:N0} bytes) irán a cuarentena. " +
                $"Gracia: {policy.GraceDays} días desde que terminen los movimientos.\n" +
                string.Join("\n", preview.Expire.Take(10).Select(id => VersionStore.GenerationTime(id).ToLocalTime().ToString("g"))) +
                (preview.Expire.Length > 10 ? $"\nY {preview.Expire.Length - 10:N0} fechas adicionales." : "");
            SetStatus("Revisa las fechas. Los objetos compartidos por puntos conservados permanecen en el almacén.");
        });
    }

    private void ApplyProductRetentionClick(object sender, RoutedEventArgs e)
    {
        if (productRetentionReview is { } review && productRetentionJob == (JobList.SelectedItem as JobRow)?.Id &&
            AcceptProductRetention.IsChecked == true)
            RunProductCleanup(store => { store.BeginRetention(review); }, "Cuarentena terminada. Puedes deshacerla; no se ha liberado espacio.");
    }

    private void ResumeProductRetentionClick(object sender, RoutedEventArgs e) => RunSelectedCleanup((store, id) => store.ResumeRetention(id),
        "Movimientos reanudados. La cuarentena conserva los objetos retirados.");
    private void UndoProductRetentionClick(object sender, RoutedEventArgs e) => RunSelectedCleanup((store, id) => store.UndoRetention(id),
        "Cuarentena deshecha. Las fechas vuelven a estar retenidas.");
    private void PurgeProductRetentionClick(object sender, RoutedEventArgs e)
    {
        if (AcceptProductPurge.IsChecked == true) RunSelectedCleanup((store, id) => store.PurgeRetention(id),
            "Purga terminada. Los objetos borrados ya no se pueden recuperar desde esta cuarentena.");
    }
    private void RunSelectedCleanup(Action<VersionStore, string> action, string message)
    {
        if (ProductRetentionChoice.SelectedItem is ProductCleanupRow row) RunProductCleanup(store => action(store, row.Operation.Id), message);
    }
    private void RunProductCleanup(Action<VersionStore> action, string message)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { } job) return;
        Start(async () =>
        {
            try { await Task.Run(() => action(VersionStore.OpenProduct(job))); }
            finally
            {
                productRetentionReview = null;
                AcceptProductRetention.IsChecked = AcceptProductPurge.IsChecked = false;
                await LoadProductRetentionAsync(job);
            }
            SetStatus(message);
        });
    }

    private async Task LoadProductRetentionAsync(ProductJob job)
    {
        try
        {
            var rows = await Task.Run(() => VersionStore.OpenProduct(job).ListRetention().OrderByDescending(operation => operation.CreatedUtc)
                .Select(operation => new ProductCleanupRow(operation)).ToArray());
            ProductRetentionChoice.ItemsSource = rows;
            ProductRetentionChoice.SelectedItem = rows.FirstOrDefault(row => row.Operation.State is not ("undone" or "purged")) ?? rows.FirstOrDefault();
        }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            ProductRetentionChoice.ItemsSource = null;
            productRetentionReview = null;
            ProductRetentionText.Text = "Limpieza suspendida · " + error.Message;
        }
    }
}
