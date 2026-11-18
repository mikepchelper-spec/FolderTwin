using System.Windows;

namespace FolderTwin;

public partial class ProductWindow
{
    private ProductMaintenanceReview? applicationReview;
    private bool hasMonitorTask;
    private bool hasCloudMonitorableJobs;

    private void ManageApplicationClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        Start(async () =>
        {
            (applicationReview, hasCloudMonitorableJobs) = await Task.Run(() =>
            {
                using var registry = openRegistry();
                var review = registry.ReviewMaintenance();
                bool cloud = review.Jobs.Any(job => ProductCloudDispatch.CanSchedule(registry, job));
                return (review, cloud);
            });
            AcceptApplicationUpdate.IsChecked = AcceptRemoveSchedules.IsChecked = false;
            ApplicationReviewText.Text = applicationReview.Jobs.Length == 0 ? "Todavía no hay trabajos guardados." :
                string.Join("\n", applicationReview.Jobs.Select(job => job.Options.Name + " · " + ModeName(job.Options.Mode) +
                    (job.Retired ? " · Retirado" : !job.Enabled ? " · Deshabilitado" : " · " + TriggerLabel(job.Options, job.WatchMode))));
            JobListPanel.Visibility = Visibility.Collapsed;
            ApplicationPanel.Visibility = Visibility.Visible;
            InstalledApplicationText.Text = "";
            InstalledVersionChoice.ItemsSource = null;
            InstalledVersionDetail.Text = "";
            AcceptInstalledVersionRemoval.IsChecked = false;
            await RefreshApplicationMonitorAsync();
            SetStatus("Revisa los trabajos antes de instalar, actualizar o retirar la aplicación.");
        });
    }

    private void ApplicationChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (InstallApplicationButton is null || RemoveInstalledVersionButton is null) return;
        InstallApplicationButton.IsEnabled = !busy && applicationReview is not null && AcceptApplicationUpdate.IsChecked == true;
        RemoveApplicationSchedulesButton.IsEnabled = !busy && applicationReview is not null && AcceptRemoveSchedules.IsChecked == true;
        RemoveInstalledVersionButton.IsEnabled = !busy && applicationReview is { } review && review.Jobs.All(job => !job.Enabled) &&
            InstalledVersionChoice.SelectedItem is InstallRemovalChoice { Current: false } && AcceptInstalledVersionRemoval.IsChecked == true;
        ApplyMonitorStartupButton.IsEnabled = !busy &&
            (applicationReview?.Jobs.Any(job => job.Enabled && job.WatchMode == ProductWatchMode.Watching) == true || hasCloudMonitorableJobs);
        StopMonitorButton.IsEnabled = !busy && hasMonitorTask;
    }

    private async Task RefreshApplicationMonitorAsync()
    {
        hasMonitorTask = false;
        MonitorAtLogon.IsChecked = true;
        MonitorApplicationText.Text = "Monitor sin configurar. Elige la vigilancia en un trabajo para iniciarlo.";
        if (testSchedule is not null || testFolderWorkspace is not null || testPicker is not null || testMonitor is not null)
            return;
        try
        {
            var (task, alive) = await Task.Run(() =>
            {
                using var registry = openRegistry();
                using var scheduler = new WindowsSchedule();
                return (scheduler.ReadMonitor(registry), registry.ReadMonitorProcess() is not null);
            });
            hasMonitorTask = task is not null;
            MonitorAtLogon.IsChecked = task?.AtLogon ?? true;
            if (task is { Enabled: true, MeetsPolicy: true })
                MonitorApplicationText.Text = alive ? "Monitor activo." :
                    $"Monitor detenido o iniciándose · Resultado de Windows 0x{task.LastResult:X8}.";
            else if (task is not null) MonitorApplicationText.Text = "La tarea del monitor requiere revisión.";
        }
        catch (Exception error) when (ProductAutomation.Expected(error))
        {
            MonitorApplicationText.Text = "No se pudo comprobar la tarea del monitor. " + error.Message;
        }
    }

    private void ApplyMonitorStartupClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        bool atLogon = MonitorAtLogon.IsChecked == true;
        Start(async () =>
        {
            await StartWatchAsync(atLogon);
            await RefreshApplicationMonitorAsync();
        });
    }

    private void StopMonitorClick(object sender, RoutedEventArgs e)
    {
        if (busy || !hasMonitorTask) return;
        Start(async () =>
        {
            await Task.Run(() =>
            {
                using var registry = openRegistry();
                using var scheduler = new WindowsSchedule();
                scheduler.RemoveMonitor(registry);
            });
            await RefreshApplicationMonitorAsync();
            explainWatchOnClose = false;
            SetStatus("Vigilancia detenida. Una copia ya iniciada puede terminar.");
        });
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
            hasCloudMonitorableJobs = false;
            explainWatchOnClose = false;
            AcceptRemoveSchedules.IsChecked = AcceptApplicationUpdate.IsChecked = false;
            await RefreshApplicationMonitorAsync();
            SetStatus("Automatización retirada y trabajos deshabilitados. La configuración y los respaldos permanecen disponibles para recuperar.");
            await LoadListAsync();
        });
    }

    private void ReturnFromApplicationClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        ShowJobs();
        Start(async () => { await LoadListAsync(); await RefreshRuntimeAsync(); });
    }

    private void ReviewInstalledVersionsClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        Start(async () =>
        {
            var snapshot = await Task.Run(() =>
            {
                using var registry = openRegistry();
                var review = registry.ReviewMaintenance();
                bool cloud = review.Jobs.Any(job => ProductCloudDispatch.CanSchedule(registry, job));
                return (Review: review, Cloud: cloud, Choices: LabInstallation.ListRemovalsAt(testInstallations ?? LabInstallation.ProductInstallations));
            });
            applicationReview = snapshot.Review;
            hasCloudMonitorableJobs = snapshot.Cloud;
            AcceptApplicationUpdate.IsChecked = AcceptRemoveSchedules.IsChecked = AcceptInstalledVersionRemoval.IsChecked = false;
            InstalledVersionChoice.ItemsSource = snapshot.Choices;
            InstalledVersionChoice.SelectedIndex = snapshot.Choices.Length == 0 ? -1 : 0;
            SetStatus(snapshot.Choices.Length == 0 ? "No hay versiones instaladas pendientes de retirar." :
                snapshot.Review.Jobs.Any(job => job.Enabled) ? "Retira primero los horarios y deshabilita los trabajos. Sus respaldos se conservarán." :
                "Revisa la ubicación y acepta la retirada de la versión seleccionada.");
        });
    }

    private void InstalledVersionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        AcceptInstalledVersionRemoval.IsChecked = false;
        InstalledVersionDetail.Text = InstalledVersionChoice.SelectedItem is InstallRemovalChoice choice ?
            System.IO.Path.Combine(testInstallations ?? LabInstallation.ProductInstallations, choice.Review.Fingerprint) +
            (choice.Current ? "\nEsta versión está abierta. Usa otra copia portable para retirarla." :
                choice.InProgress ? "\nSe reanudará la retirada desde su diario. Solo se revisan los archivos del paquete que sigan presentes." :
                "\nSe comprobarán sus archivos antes de eliminarlos. La configuración y los respaldos no pertenecen a esta carpeta.") : "";
        ApplicationChoiceChanged(sender, e);
    }

    private void RemoveInstalledVersionClick(object sender, RoutedEventArgs e)
    {
        if (busy || !RemoveInstalledVersionButton.IsEnabled || applicationReview is not { } review ||
            InstalledVersionChoice.SelectedItem is not InstallRemovalChoice choice) return;
        Start(async () =>
        {
            string installations = testInstallations ?? LabInstallation.ProductInstallations;
            await Task.Run(() =>
            {
                using var registry = openRegistry();
                ProductDeployment.RemoveVersion(registry, review, installations, choice.Review,
                    testSchedule is null ? null : static () => { });
            });
            AcceptInstalledVersionRemoval.IsChecked = false;
            InstalledVersionChoice.ItemsSource = await Task.Run(() => LabInstallation.ListRemovalsAt(installations));
            InstalledVersionDetail.Text = "";
            SetStatus("Versión instalada retirada. Los trabajos, el historial y los respaldos se conservan. Puedes reinstalarla desde su paquete portable.");
        });
    }
}
