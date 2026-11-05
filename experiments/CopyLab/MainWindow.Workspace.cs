using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace CopyLab;

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

    private static async Task RunWorkspaceSmokeAsync()
    {
        var initial = new MainWindow(smoke: true);
        LabWorkspace? current = null;
        try
        {
            try
            {
                Click(initial.CreateWorkspaceButton);
                await initial.activeTask;
                current = initial.workspace ?? throw new InvalidOperationException(initial.StatusText.Text);
                string source = Directory.CreateDirectory(Path.Combine(current.Root, "Entrada café")).FullName;
                string target = Directory.CreateDirectory(Path.Combine(current.Root, "Respaldo 漢字")).FullName;
                string file = Path.Combine(source, "informe ñ.txt");
                File.WriteAllText(file, "contenido de la prueba de interfaz");
                File.WriteAllBytes(Path.Combine(source, "segunda prueba.bin"), [12, 34]);
                initial.testSelections!.Clear();
                initial.testSelections.Enqueue(Path.GetTempPath());
                initial.testSelections.Enqueue(source);
                initial.testSelections.Enqueue(target);
                Click(initial.SourceButton);
                Check(!initial.sourceSelected, "A path outside the workspace was selected.");
                Click(initial.SourceButton);
                Click(initial.DestinationButton);
                Click(initial.AnalyzeButton);
                await initial.activeTask;
                Check(initial.CopyButton.IsEnabled && initial.PreviewText.Text.Contains("2 archivos", StringComparison.Ordinal) &&
                    initial.PreviewText.Text.Contains("Espacio disponible al analizar", StringComparison.Ordinal) &&
                    !Directory.EnumerateFileSystemEntries(target).Any(), "The configurable preview failed or modified the destination.");
                using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    Click(initial.CopyButton);
                    await initial.activeTask;
                    Check(initial.StatusText.Text.StartsWith("Resultado parcial: 1 de 2", StringComparison.Ordinal) &&
                        initial.HistoryList.Items.Cast<string>().Any(row => row.Contains("informe ñ.txt · Archivo en uso", StringComparison.Ordinal)),
                        "A partial configurable job did not report its actual progress and history.");
                }
            }
            finally { initial.Close(); }

            var reopened = new MainWindow(smoke: true);
            try
            {
                string source = Path.Combine(current.Root, "Entrada café");
                string target = Path.Combine(current.Root, "Respaldo 漢字");
                string second = Directory.CreateDirectory(Path.Combine(current.Root, "Otro destino")).FullName;
                string output = Directory.CreateDirectory(Path.Combine(current.Root, "Restauración elegida")).FullName;
                byte[] hash = SHA256.HashData(File.ReadAllBytes(Path.Combine(source, "informe ñ.txt")));
                reopened.testSelections!.Clear();
                reopened.testSelections.Enqueue(current.Root);
                reopened.testSelections.Enqueue(target);
                reopened.testSelections.Enqueue(output);
                reopened.testSelections.Enqueue(second);
                Click(reopened.OpenWorkspaceButton);
                await reopened.activeTask;
                Click(reopened.OpenWorkspaceJobButton);
                await reopened.activeTask;
                Check(reopened.workspaceJob is { Confirmed: false } && reopened.CopyButton.IsEnabled &&
                    reopened.HistoryList.Items.Count == 1, "The configurable job did not reopen with its history.");
                Click(reopened.CopyButton);
                await reopened.activeTask;
                Check(reopened.workspaceJob is { Confirmed: true } && reopened.VerifyButton.IsEnabled, reopened.StatusText.Text);
                Click(reopened.VerifyButton);
                await reopened.activeTask;
                Check(reopened.StatusText.Text.Contains("Coinciden los 2 archivos", StringComparison.Ordinal), reopened.StatusText.Text);
                Check(current.Start(current.Analyze(source, second)).Complete, "Creating the independent history fixture failed.");
                Directory.Move(source, Path.Combine(current.Root, "Origen apartado"));
                Click(reopened.RestoreButton);
                await reopened.activeTask;
                Check(reopened.restored && File.ReadAllBytes(Path.Combine(output, "segunda prueba.bin")).SequenceEqual(new byte[] { 12, 34 }) &&
                    SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "informe ñ.txt"))).AsSpan().SequenceEqual(hash) &&
                    reopened.HistoryList.Items.Count == 4, "Restoration without the source failed or was not logged.");
                Click(reopened.OpenWorkspaceJobButton);
                await reopened.activeTask;
                Check(reopened.workspaceJob?.Destination == second && reopened.HistoryList.Items.Count == 0,
                    "The second destination inherited history from another job.");
            }
            finally { reopened.Close(); }
        }
        finally
        {
            if (current is not null)
                Directory.Delete(current.Root, recursive: true); // The test created this workspace and every entry in it.
        }

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
