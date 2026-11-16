using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CopyLab;

public partial class ProductCloudWindow
{
    private const int RecoveryPageSize = 30;
    private sealed record RecoveryChoice(Guid Id, string Label);
    private DispatcherTimer? recoveryTimer;
    private CloudRecoveryView[] recoveryRuns = [];
    private RecoveryChoice[] displayedRuns = [];
    private readonly SemaphoreSlim recoveryRefresh = new(1, 1);
    private Task<CloudRecoveryResult>? testRecoveryTask;
    private int recoveryOffset;
    private VersionBrowser? remoteBrowser;
    private string remoteFolder = "", remoteQuery = "";
    private int remoteOffset;

    private void InitializeRecoveryTracking()
    {
        recoveryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        recoveryTimer.Tick += async (_, _) => { if (Actions.IsEnabled) await RefreshRecoveriesAsync(); };
        Loaded += async (_, _) =>
        {
            await RefreshRecoveriesAsync();
            if (!closed.IsCancellationRequested) recoveryTimer.Start();
        };
        Closed += (_, _) => recoveryTimer.Stop();
    }

    private void CloudDateChanged(object sender, SelectionChangedEventArgs e)
    {
        remoteBrowser = null;
        CloudCatalogPanel.Visibility = Visibility.Collapsed;
        CloudFiles.ItemsSource = null;
        CloudFileSearch.Clear();
        RestoreCloud.IsEnabled = CloudDates.SelectedItem is DateChoice;
        BrowseCloud.IsEnabled = CloudDates.SelectedItem is DateChoice { Recovery.Point.Layout: 2 };
        CloudDateLimit.Text = CloudDates.SelectedItem is DateChoice { Recovery.Point.Layout: 0 } ?
            "Esta fecha se creó con el formato anterior y admite recuperar la fecha completa." : "";
    }

    private (CloudConnection Connection, CloudRecoveryPoint Point) ReviewedCloudDate()
    {
        if (readConnection is not { } connection || CloudDates.SelectedItem is not DateChoice selected || Destination() != connection.Destination)
            throw new InvalidDataException("Consulta las fechas del repositorio y elige la que quieres recuperar.");
        return (connection, selected.Recovery);
    }

    private async void BrowseCloudClick(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var (connection, point) = ReviewedCloudDate();
        var credentials = Credentials();
        remoteBrowser = await Task.Run(async () =>
        {
            using var session = Session(connection.Destination, credentials);
            if (await session.OpenAsync(create: false, readOnly: true, token) != connection)
                throw new InvalidDataException("El repositorio cambió. Vuelve a consultar sus fechas.");
            var catalog = await session.ReadRecoveryCatalogAsync(point.Point, token);
            return new VersionBrowser(catalog, null);
        }, token);
        remoteFolder = remoteQuery = CloudFileSearch.Text = "";
        remoteOffset = 0;
        CloudCatalogPanel.Visibility = Visibility.Visible;
        ShowCloudFiles();
        _ = Dispatcher.InvokeAsync(() => CloudFileSearch.Focus(), DispatcherPriority.Input);
    });

    private void ShowCloudFiles()
    {
        var page = remoteBrowser?.Page(remoteFolder, false, remoteQuery, remoteOffset, RecoveryPageSize);
        CloudFiles.ItemsSource = page?.Entries;
        CloudFiles.SelectedIndex = page?.Entries.Length > 0 ? 0 : -1;
        CloudFolderPath.Text = remoteQuery.Length != 0 ? "Resultados de búsqueda" :
            remoteFolder.Length == 0 ? "Carpeta del respaldo" : remoteFolder;
        CloudFilePage.Text = page?.Entries.Length > 0 ? $"{remoteOffset + 1:N0}–{remoteOffset + page.Entries.Length:N0}" : "Sin resultados";
        CloudPreviousFiles.IsEnabled = remoteOffset > 0;
        CloudNextFiles.IsEnabled = page?.HasMore == true;
        CloudParentFolder.IsEnabled = remoteFolder.Length > 0;
        RefreshCloudSelection();
    }

    private void SearchCloudClick(object sender, RoutedEventArgs e)
    {
        remoteQuery = CloudFileSearch.Text.Trim();
        remoteOffset = 0;
        ShowCloudFiles();
    }

    private void CloudSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        SearchCloudClick(sender, e);
    }

    private void CloudParentClick(object sender, RoutedEventArgs e) => OpenCloudFolder(VersionBrowser.Parent(remoteFolder));
    private void CloudOpenClick(object sender, RoutedEventArgs e)
    {
        if (CloudFiles.SelectedItem is VersionEntry { IsDirectory: true } entry) OpenCloudFolder(entry.Path);
    }

    private void CloudFilesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CloudOpenFolder.IsEnabled) { e.Handled = true; CloudOpenClick(sender, e); }
        else if (e.Key == Key.Back && CloudParentFolder.IsEnabled) { e.Handled = true; CloudParentClick(sender, e); }
    }

    private void OpenCloudFolder(string folder)
    {
        remoteFolder = folder;
        remoteQuery = CloudFileSearch.Text = "";
        remoteOffset = 0;
        ShowCloudFiles();
        CloudFiles.Focus();
    }

    private void CloudPreviousClick(object sender, RoutedEventArgs e) { remoteOffset -= RecoveryPageSize; ShowCloudFiles(); }
    private void CloudNextClick(object sender, RoutedEventArgs e) { remoteOffset += RecoveryPageSize; ShowCloudFiles(); }
    private void CloudFileChanged(object sender, SelectionChangedEventArgs e) => RefreshCloudSelection();
    private void RefreshCloudSelection()
    {
        if (CloudOpenFolder is null || RestoreCloudSelection is null) return;
        CloudOpenFolder.IsEnabled = CloudFiles.SelectedItem is VersionEntry { IsDirectory: true };
        RestoreCloudSelection.IsEnabled = CloudFiles.SelectedItem is VersionEntry;
    }

    private async void RestoreClick(object sender, RoutedEventArgs e) => await LaunchCloudRecoveryAsync(null);
    private async void RestoreCloudSelectionClick(object sender, RoutedEventArgs e)
    {
        if (CloudFiles.SelectedItem is VersionEntry entry)
            await LaunchCloudRecoveryAsync(new(entry.Path, entry.IsDirectory));
    }

    private Task LaunchCloudRecoveryAsync(CloudRecoverySelection? selection) => RunAsync(async _ =>
    {
        var (connection, point) = ReviewedCloudDate();
        string? selectedParent = testOutput?.Invoke();
        if (selectedParent is null)
        {
            var dialog = new OpenFolderDialog { Title = "Elige dónde crear la carpeta recuperada", AddToRecent = false, DereferenceLinks = false };
            if (dialog.ShowDialog(this) != true) return;
            selectedParent = dialog.FolderName;
        }
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedParent));
        var credentials = Credentials();
        var request = await Task.Run(() =>
        {
            using var registry = openRegistry();
            return registry.ApproveCloudRecovery(connection, point, credentials, parent, selection, lab: testSession is not null);
        });
        if (testSession is null)
        {
            using var registry = openRegistry();
            using var worker = ProductWorker.StartCloudRecovery(registry.Location, request.Id);
        }
        else
        {
            testRecoveryTask = Task.Run(async () =>
            {
                using var registry = openRegistry();
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                return await registry.RunCloudRecoveryAsync(request.Id, (reviewed, secret) => Session(reviewed.Destination, secret), deadline.Token, lab: true);
            });
        }
        CloudStatus.Text = "Recuperación iniciada. Puedes cerrar esta ventana y consultar el intento al volver.";
        recoveryOffset = 0;
        await RefreshRecoveriesAsync(request.Id);
        CloudRecoveryStatus.BringIntoView();
    });

    private async Task RefreshRecoveriesAsync(Guid? select = null)
    {
        if (closed.IsCancellationRequested) return;
        if (select is null)
        {
            if (!recoveryRefresh.Wait(0)) return;
        }
        else await recoveryRefresh.WaitAsync();
        try
        {
            var history = await Task.Run(() =>
            {
                using var registry = openRegistry();
                return registry.ReadCloudRecoveries(lab: testSession is not null);
            });
            if (closed.IsCancellationRequested) return;
            recoveryRuns = history.Runs;
            ShowRecoveryPage(select);
            if (history.Unreadable != 0)
                CloudRecoveryStatus.Text += $" {history.Unreadable:N0} intentos requieren revisar su registro.";
        }
        catch (Exception error) when (VersionJob.SharingViolation(error)) { }
        catch (Exception error) when (ProductAutomation.Expected(error))
        {
            CloudRecoveryStatus.Text = "El historial de recuperaciones requiere revisión.";
        }
        finally { recoveryRefresh.Release(); }
    }

    private void ShowRecoveryPage(Guid? select = null)
    {
        int lastPage = Math.Max(0, recoveryRuns.Length - 1) / RecoveryPageSize * RecoveryPageSize;
        recoveryOffset = Math.Clamp(recoveryOffset, 0, lastPage);
        var rows = recoveryRuns.Skip(recoveryOffset).Take(RecoveryPageSize).Select(run =>
            new RecoveryChoice(run.Request.Id, $"{run.Request.RequestedUtc.ToLocalTime():g} · {run.Request.Id.ToString("N")[..8]}")).ToArray();
        Guid? selected = select ?? (CloudRecoveryRuns.SelectedItem as RecoveryChoice)?.Id;
        if (!rows.SequenceEqual(displayedRuns))
        {
            displayedRuns = rows;
            CloudRecoveryRuns.ItemsSource = displayedRuns;
        }
        var selectedRow = displayedRuns.FirstOrDefault(row => row.Id == selected) ?? displayedRuns.FirstOrDefault();
        if (!ReferenceEquals(CloudRecoveryRuns.SelectedItem, selectedRow)) CloudRecoveryRuns.SelectedItem = selectedRow;
        OlderCloudRuns.IsEnabled = recoveryOffset < lastPage;
        NewerCloudRuns.IsEnabled = recoveryOffset > 0;
        ShowCloudRun();
    }

    private void OlderCloudRunsClick(object sender, RoutedEventArgs e) { recoveryOffset += RecoveryPageSize; ShowRecoveryPage(); }
    private void NewerCloudRunsClick(object sender, RoutedEventArgs e) { recoveryOffset -= RecoveryPageSize; ShowRecoveryPage(); }
    private void CloudRunChanged(object sender, SelectionChangedEventArgs e) => ShowCloudRun();

    private void ShowCloudRun()
    {
        if (CloudRecoveryStatus is null) return;
        var selected = CloudRecoveryRuns.SelectedItem as RecoveryChoice;
        var run = recoveryRuns.FirstOrDefault(item => item.Request.Id == selected?.Id);
        bool active = run?.State is CloudRecoveryState.Queued or CloudRecoveryState.Running;
        CancelCloudRecovery.IsEnabled = (active || run?.State == CloudRecoveryState.Interrupted) && run?.CancellationRequested != true;
        CancelCloudRecovery.Content = run?.State == CloudRecoveryState.Interrupted ? "Cerrar intento interrumpido" : "Cancelar recuperación";
        CloudRecoveryProgress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        CloudRecoveryOutput.Visibility = run is null ? Visibility.Collapsed : Visibility.Visible;
        if (run is null) { CloudRecoveryStatus.Text = "Todavía no hay intentos de recuperación."; return; }
        string output = ProductWindow.DisplayRoutePath(run.Request.Output);
        if (CloudRecoveryOutput.Text != output) CloudRecoveryOutput.Text = output;
        var progress = run.Progress;
        bool determinate = progress is { Phase: WorkPhase.Verifying or WorkPhase.Publishing, Total: > 0 };
        CloudRecoveryProgress.IsIndeterminate = !determinate;
        if (determinate)
        {
            CloudRecoveryProgress.Maximum = progress!.Total;
            CloudRecoveryProgress.Value = progress.Processed;
        }
        string status = run.State switch
        {
            CloudRecoveryState.Completed => "Recuperación completada y verificada.",
            CloudRecoveryState.Cancelled => "Recuperación cancelada. Se conserva la salida que se haya creado.",
            CloudRecoveryState.Interrupted => "El ejecutor se interrumpió. Revisa la salida parcial e inicia otro intento en una carpeta nueva.",
            CloudRecoveryState.Review => run.Result?.Detail ?? "Revisa el intento y su salida.",
            _ when run.CancellationRequested => "Cancelación solicitada.",
            CloudRecoveryState.Queued => "Esperando al ejecutor. Si no arranca, cancela este intento e inicia otro.",
            _ when progress is { Phase: WorkPhase.Verifying } => $"Comprobando archivos · {progress.Processed:N0} de {progress.Total:N0}",
            _ when progress is { Phase: WorkPhase.Publishing } => $"Entregando archivos · {progress.Processed:N0} de {progress.Total:N0}",
            _ when progress is { Phase: WorkPhase.Restoring } => "Descargando archivos.",
            _ => "Abriendo el repositorio y comprobando la fecha."
        };
        if (CloudRecoveryStatus.Text != status) CloudRecoveryStatus.Text = status;
    }

    private async void CancelCloudRecoveryClick(object sender, RoutedEventArgs e)
    {
        if (CloudRecoveryRuns.SelectedItem is not RecoveryChoice selected) return;
        await RunAsync(async _ =>
        {
            await Task.Run(() =>
            {
                using var registry = openRegistry();
                registry.CancelCloudRecovery(selected.Id, lab: testSession is not null);
            });
            await RefreshRecoveriesAsync(selected.Id);
        });
    }
}
