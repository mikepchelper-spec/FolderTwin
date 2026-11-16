using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CopyLab;

public partial class ExistingBackupWindow : Window
{
    private sealed record DateChoice(VersionPoint Point)
    {
        public string Label => $"{Point.CreatedUtc.ToLocalTime():g} · {Point.FileCount:N0} archivos" +
            (Point.Problem is not null ? " · Requiere revisión" : Point.Expired ? " · Expirado" :
                Point.Degraded ? " · Una réplica íntegra" : "");
    }

    private readonly Func<ProductJobRegistry> openRegistry;
    private readonly Func<string?> chooseBackup;
    private readonly Func<string?> chooseOutput;
    private ProductBackupRecovery? reader;
    private VersionBrowser? browser;
    private CancellationTokenSource? operation;
    private WorkProgress? latestProgress;
    private readonly DispatcherTimer progressTimer;
    private string directory = "";
    private string query = "";
    private int offset;
    private const int PageSize = 250;

    internal ExistingBackupWindow(Func<ProductJobRegistry> openRegistry, Func<string?>? chooseBackup = null,
        Func<string?>? chooseOutput = null)
    {
        this.openRegistry = openRegistry;
        this.chooseBackup = chooseBackup ?? (() => SelectFolder("Elige la carpeta que contiene el respaldo de FolderTwin"));
        this.chooseOutput = chooseOutput ?? (() => SelectFolder("Elige dónde crear la carpeta recuperada"));
        InitializeComponent();
        progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            if (Volatile.Read(ref latestProgress) is not { } work) return;
            OperationProgress.IsIndeterminate = work.Total <= 0;
            OperationProgress.Maximum = Math.Max(1, work.Total);
            OperationProgress.Value = work.Processed;
            OperationStatus.Text = $"Procesados {work.Processed:N0} de {work.Total:N0}" +
                (work.RelativePath is { } path ? " · " + path : "");
        }, Dispatcher);
        progressTimer.Stop();
        Closing += (_, e) =>
        {
            if (operation is null) return;
            operation.Cancel();
            e.Cancel = true;
            OperationStatus.Text = "Cancelando la operación. Espera a que termine el paso actual.";
        };
    }

    private string? SelectFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, AddToRecent = false, DereferenceLinks = false };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void BackClick(object sender, RoutedEventArgs e) => Close();
    private async void OpenClick(object sender, RoutedEventArgs e)
    {
        if (operation is not null || chooseBackup() is not { } path) return;
        await RunAsync(async token =>
        {
            var opened = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var result = ProductBackupRecovery.Open(path);
                return (Reader: result, Points: result.ListPoints(), Warning: result.RegistryProblem());
            }, token);
            reader = opened.Reader;
            BackupPath.Text = path;
            Dates.ItemsSource = opened.Points.Select(point => new DateChoice(point)).ToArray();
            OperationStatus.Text = opened.Warning ?? (opened.Points.Length == 0
                ? "No hay fechas confirmadas disponibles." : "Elige una fecha para verificar o recuperar.");
        });
    }

    private async void DateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (operation is not null || reader is null) return;
        browser = null;
        Files.ItemsSource = null;
        if (Dates.SelectedItem is not DateChoice { Point.Problem: null } selected) return;
        await RunAsync(async token =>
        {
            var catalog = await Task.Run(() => reader.ReadCatalog(selected.Point.Id), token);
            browser = new(catalog, null);
            directory = query = SearchBox.Text = "";
            offset = 0;
            ShowPage();
        });
    }

    private void ShowPage()
    {
        var page = browser?.Page(directory, false, query, offset, PageSize);
        Files.ItemsSource = page?.Entries;
        FolderPath.Text = query.Length == 0 ? directory.Length == 0 ? "Carpeta del respaldo" : directory : "Resultados de búsqueda";
        PageStatus.Text = page is null || page.Entries.Length == 0 ? "Sin resultados" : $"{offset + 1:N0}–{offset + page.Entries.Length:N0}";
        PreviousButton.IsEnabled = offset > 0;
        NextButton.IsEnabled = page?.HasMore == true;
        ParentButton.IsEnabled = directory.Length > 0;
        RefreshActions();
    }

    private void SearchClick(object sender, RoutedEventArgs e)
    {
        if (operation is not null) return;
        query = SearchBox.Text.Trim();
        offset = 0;
        ShowPage();
    }
    private void SearchKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; SearchClick(sender, e); } }
    private void ParentClick(object sender, RoutedEventArgs e)
    {
        directory = VersionBrowser.Parent(directory);
        query = SearchBox.Text = "";
        offset = 0;
        ShowPage();
    }
    private void OpenFolder(object sender, MouseButtonEventArgs e)
        => OpenSelectedFolder();

    private void FilesKeyDown(object sender, KeyEventArgs e)
    {
        if (operation is not null) return;
        if (e.Key == Key.Enter) { e.Handled = true; OpenSelectedFolder(); }
        else if (e.Key == Key.Back && directory.Length > 0) { e.Handled = true; ParentClick(sender, e); }
    }

    private void OpenSelectedFolder()
    {
        if (operation is not null || Files.SelectedItem is not VersionEntry { IsDirectory: true } entry) return;
        directory = entry.Path;
        query = SearchBox.Text = "";
        offset = 0;
        ShowPage();
    }
    private void PreviousClick(object sender, RoutedEventArgs e) { offset = Math.Max(0, offset - PageSize); ShowPage(); }
    private void NextClick(object sender, RoutedEventArgs e) { offset += PageSize; ShowPage(); }
    private void FileChanged(object sender, SelectionChangedEventArgs e) => RefreshActions();
    private void CancelClick(object sender, RoutedEventArgs e) => operation?.Cancel();

    private async void VerifyClick(object sender, RoutedEventArgs e)
    {
        if (reader is null || Dates.SelectedItem is not DateChoice selected) return;
        await RunAsync(async token =>
        {
            var observe = ProgressObserver(token);
            await Task.Run(() => ProductBackupRecovery.Open(reader.Path, observe).Verify(selected.Point.Id), token);
            OperationStatus.Text = "Contenido verificado. Puedes recuperar esta fecha en una carpeta nueva.";
        });
    }

    private async void RestoreAllClick(object sender, RoutedEventArgs e) => await RestoreAsync(null);
    private async void RestoreFileClick(object sender, RoutedEventArgs e)
    {
        if (Files.SelectedItem is VersionEntry { IsDirectory: false } entry) await RestoreAsync(entry.Path);
    }

    private async Task RestoreAsync(string? relativeFile)
    {
        if (operation is not null || reader is null || Dates.SelectedItem is not DateChoice selected ||
            chooseOutput() is not { } parent) return;
        await RunAsync(async token =>
        {
            var target = LocalJobRoots.ReviewDedicatedDestination(reader.Path, parent);
            string displayedOutput = Path.Combine(parent, target.FolderName);
            OutputPath.Text = "Carpeta de recuperación · " + displayedOutput;
            OperationStatus.Text = "Recuperando. La salida se conserva si cancelas.";
            var observe = ProgressObserver(token);
            await Task.Run(() =>
            {
                using var registry = openRegistry();
                using var exclusion = registry.BeginIndependentRecovery(target);
                using var roots = LocalJobRoots.CreateDedicatedDestination(target);
                ProductBackupRecovery.Open(reader.Path, observe)
                    .Restore(selected.Point.Id, roots.Review.Destination.VolumePath, relativeFile);
            }, token);
            OperationStatus.Text = "Recuperación completada y verificada.";
        });
    }

    private Action<WorkProgress> ProgressObserver(CancellationToken token)
        => work => { token.ThrowIfCancellationRequested(); Volatile.Write(ref latestProgress, work); };

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null) return;
        operation = new();
        latestProgress = null;
        OperationProgress.IsIndeterminate = true;
        progressTimer.Start();
        RefreshActions();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { OperationStatus.Text = "Operación cancelada. La carpeta de salida indicada conserva los archivos completados."; }
        catch (Exception error) when (ProductAutomation.Expected(error)) { OperationStatus.Text = error.Message; }
        finally
        {
            progressTimer.Stop();
            operation.Dispose();
            operation = null;
            latestProgress = null;
            RefreshActions();
        }
    }

    private void RefreshActions()
    {
        if (OpenButton is null || Files is null) return;
        bool idle = operation is null;
        bool ready = browser is not null && Dates.SelectedItem is DateChoice { Point.Problem: null };
        OpenButton.IsEnabled = Dates.IsEnabled = Files.IsEnabled = SearchBox.IsEnabled = SearchButton.IsEnabled = idle;
        VerifyButton.IsEnabled = idle && ready;
        RestoreAllButton.IsEnabled = idle && ready;
        RestoreFileButton.IsEnabled = idle && ready && Files.SelectedItem is VersionEntry { IsDirectory: false };
        CancelButton.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        BackButton.IsEnabled = idle;
        OperationProgress.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        ParentButton.IsEnabled = idle && directory.Length > 0;
        PreviousButton.IsEnabled = idle && offset > 0;
        NextButton.IsEnabled = idle && (browser?.Page(directory, false, query, offset, PageSize).HasMore == true);
    }
}
