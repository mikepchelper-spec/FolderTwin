using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace FolderTwin;

public partial class MainWindow
{
    private LabWorkspace? workspace;
    private WorkspacePreview? workspacePreview;
    private WorkspaceJob? workspaceJob;
    private string? workspaceDestination;
    private LabTray? tray;

    internal async Task StartTrayAsync(string root)
    {
        await SwitchWorkspaceAsync(() => LabWorkspace.Open(root));
        var current = workspace ?? throw new IOException("No se pudo abrir el espacio de la bandeja.");
        tray ??= new LabTray(current, ShowMain, () => { ShowMain(); Close(); }, notifications: testSelections is null);
        await tray.RefreshAsync();
        Hide();
    }

    private async void TrayClick(object sender, RoutedEventArgs e)
    {
        if (busy || workspace is not { } current)
            return;
        busy = true;
        RefreshActions();
        try
        {
            tray ??= new LabTray(current, ShowMain, () => { ShowMain(); Close(); }, notifications: testSelections is null);
            await tray.RefreshAsync();
            Hide();
        }
        catch (Exception error) when (IsLabError(error))
        {
            tray?.Dispose();
            tray = null;
            SetStatus("No se pudo abrir la bandeja. " + error.Message);
        }
        finally { busy = false; RefreshActions(); }
    }

    private void ShowMain()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void VersionsClick(object sender, RoutedEventArgs e)
    {
        if (VersionsButton.IsEnabled && workspace is { } current)
            new VersionWindow(current) { Owner = this }.ShowDialog();
    }

    private void CreateWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (CreateWorkspaceButton.IsEnabled)
            activeTask = SwitchWorkspaceAsync(LabWorkspace.CreatePersistent);
    }

    private void OpenWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (!OpenWorkspaceButton.IsEnabled)
            return;
        string? root = PickFolder("Elige un espacio FolderTwin-Workspace",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (root is not null)
            activeTask = SwitchWorkspaceAsync(() => LabWorkspace.Open(Path.TrimEndingDirectorySeparator(root)));
    }

    private async Task SwitchWorkspaceAsync(Func<LabWorkspace> open)
    {
        busy = true;
        RefreshActions();
        try
        {
            workspace = await Task.Run(open);
            tray?.Dispose();
            tray = null;
            workspacePreview = null;
            workspaceJob = null;
            workspaceDestination = null;
            selectedSource = null;
            sourceSelected = destinationSelected = analyzed = false;
            copyStarted = copied = restored = false;
            ResultsList.ItemsSource = HistoryList.ItemsSource = null;
            HistoryStatus.Text = "Aún no hay intentos registrados.";
            SessionText.Text = $"Espacio de pruebas · {workspace.Root}\nSe conserva al cerrar la ventana.";
            SourceText.Text = "Elige una subcarpeta con datos sintéticos.";
            DestinationText.Text = "Elige una subcarpeta vacía del espacio.";
            PreviewText.Text = "Analiza las carpetas para revisar qué se copiará.";
            SetStatus("Puedes preparar un trabajo nuevo o abrir uno existente. Usa datos sintéticos.");
        }
        catch (Exception error) when (IsLabError(error))
        {
            SetStatus("No se pudo abrir el espacio de pruebas. " + error.Message);
        }
        finally
        {
            busy = false;
            RefreshActions();
        }
    }

    private void BrowseWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (workspace is not { } current)
            return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(LabWorkspace.Open(current.Root).Root) { UseShellExecute = true });
        }
        catch (Exception error) when (IsLabError(error))
        {
            SetStatus("No se pudo abrir la carpeta de pruebas. " + error.Message);
        }
    }

    private void SelectWorkspaceFolder(LabWorkspace current, bool source)
    {
        string? folder = PickFolder(source ? "Elegir origen sintético" : "Elegir destino vacío", current.Root);
        if (folder is null)
            return;
        folder = Path.TrimEndingDirectorySeparator(folder);
        try
        {
            current.CheckChild(folder);
        }
        catch (Exception error) when (IsLabError(error))
        {
            SetStatus(error.Message);
            return;
        }
        if (source)
        {
            selectedSource = folder;
            sourceSelected = true;
            SourceText.Text = folder;
        }
        else
        {
            workspaceDestination = folder;
            destinationSelected = true;
            DestinationText.Text = folder;
        }
        analyzed = false;
        workspacePreview = null;
        PreviewText.Text = "Analiza las carpetas para revisar qué se copiará.";
        SetStatus(sourceSelected && destinationSelected ? "Todo listo para analizar." : "Selecciona la otra carpeta.");
        RefreshActions();
    }

    private void OpenWorkspaceJobClick(object sender, RoutedEventArgs e)
    {
        if (!OpenWorkspaceJobButton.IsEnabled || workspace is not { } current)
            return;
        string? folder = PickFolder("Elige el destino del trabajo anterior", current.Root);
        if (folder is not null)
            activeTask = OpenWorkspaceJobAsync(current, Path.TrimEndingDirectorySeparator(folder));
    }

    private async Task OpenWorkspaceJobAsync(LabWorkspace current, string destination)
    {
        busy = true;
        RefreshActions();
        SetStatus("Comprobando el destino del trabajo…");
        try
        {
            var job = await Task.Run(() => current.OpenJob(destination));
            workspaceJob = job;
            workspacePreview = null;
            workspaceDestination = job.Destination;
            selectedSource = job.Source;
            analyzed = true;
            copyStarted = restored = false;
            copied = job.Confirmed;
            SourceText.Text = job.Source ?? "Respaldo confirmado. La restauración usa el catálogo.";
            DestinationText.Text = job.Destination;
            PreviewText.Text = $"{job.FileCount:N0} archivos · " + (job.Confirmed ? "respaldo confirmado" : "trabajo pendiente");
            ResultsList.ItemsSource = null;
            SetStatus(job.Confirmed ? "Puedes verificar o restaurar el trabajo." : "Puedes reintentar la copia.");
            await RefreshHistoryAsync();
        }
        catch (Exception error) when (IsLabError(error))
        {
            SetStatus("No se pudo abrir el trabajo. " + error.Message);
        }
        finally
        {
            busy = false;
            RefreshActions();
        }
    }

    private BatchResult RunWorkspace(LabWorkspace current, AttemptOperation operation, string? output,
        CancellationToken token, IProgress<BatchProgress> progress, Action<BatchProgress> waitBetweenFiles) =>
        operation switch
        {
            AttemptOperation.Verify => current.Verify(workspaceJob!, progress),
            AttemptOperation.Restore => current.Restore(workspaceJob!, output!, progress),
            _ when workspaceJob is { } job => current.Resume(job, token, progress, waitBetweenFiles),
            _ => current.Start(workspacePreview!, token, progress, waitBetweenFiles: waitBetweenFiles)
        };
}
