using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private ProductMaintenanceReview? applicationReview;

    private void ManageApplicationClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        Start(async () =>
        {
            applicationReview = await Task.Run(() => { using var registry = openRegistry(); return registry.ReviewMaintenance(); });
            AcceptApplicationUpdate.IsChecked = AcceptRemoveSchedules.IsChecked = false;
            ApplicationReviewText.Text = applicationReview.Jobs.Length == 0 ? "Todavía no hay trabajos guardados." :
                string.Join("\n", applicationReview.Jobs.Select(job => job.Options.Name + " · " + ModeName(job.Options.Mode) +
                    (job.Retired ? " · Retirado" : !job.Enabled ? " · Deshabilitado" : job.Options.DailyAt is { } hour ? $" · Diario {hour:HH:mm}" : " · A demanda")));
            JobListPanel.Visibility = Visibility.Collapsed;
            ApplicationPanel.Visibility = Visibility.Visible;
            InstalledApplicationText.Text = "";
        });
    }

    private void ApplicationChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (InstallApplicationButton is null) return;
        InstallApplicationButton.IsEnabled = !busy && applicationReview is not null && AcceptApplicationUpdate.IsChecked == true;
        RemoveApplicationSchedulesButton.IsEnabled = !busy && applicationReview is not null && AcceptRemoveSchedules.IsChecked == true;
    }

    private Action<ProductJob, string?>? TestDeploymentSchedule(string location) => testSchedule is null ? null :
        (job, executable) => testSchedule(location, job.Id, executable is null);

    private void InstallApplicationClick(object sender, RoutedEventArgs e)
    {
        if (busy || applicationReview is not { } review || AcceptApplicationUpdate.IsChecked != true) return;
        Start(async () =>
        {
            string executable = await Task.Run(() =>
            {
                using var registry = openRegistry();
                return ProductDeployment.Install(registry, review, testPackageInstall, TestDeploymentSchedule(registry.Location));
            });
            InstalledApplicationText.Text = "Ejecutor verificado y horarios comprobados.\n" + executable;
            AcceptApplicationUpdate.IsChecked = false;
            SetStatus("Preparación terminada. Conserva el paquete anterior hasta verificar un respaldo y recuperar una muestra.");
        });
    }

    private void RemoveApplicationSchedulesClick(object sender, RoutedEventArgs e)
    {
        if (busy || applicationReview is not { } review || AcceptRemoveSchedules.IsChecked != true) return;
        Start(async () =>
        {
            await Task.Run(() =>
            {
                using var registry = openRegistry();
                ProductDeployment.RemoveSchedules(registry, review, TestDeploymentSchedule(registry.Location));
            });
            applicationReview = null;
            AcceptRemoveSchedules.IsChecked = AcceptApplicationUpdate.IsChecked = false;
            SetStatus("Horarios retirados y trabajos deshabilitados. La configuración y los respaldos permanecen disponibles para recuperar.");
            await LoadListAsync();
        });
    }

    private void ReturnFromApplicationClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        ShowJobs();
        Start(async () => { await LoadListAsync(); await RefreshRuntimeAsync(); });
    }
}
