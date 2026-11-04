using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CopyLab;

public partial class VersionWindow : Window
{
    private sealed record PointRow(string Id, VersionPoint? Confirmed, PendingVersion? Pending)
    {
        internal string Date => (Confirmed?.CreatedUtc ?? VersionStore.GenerationTime(Id)).ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss.fff");
        public string Label => Confirmed switch
        {
            { Problem: not null } => $"{Date} · requiere revisión",
            { } point => $"{Date} · {point.FileCount:N0} archivos" + (point.Degraded ? " · una réplica" : ""),
            _ => $"{Date} · pendiente · {Pending!.FileCount:N0} archivos"
        };
    }

    private readonly LabWorkspace workspace;
    private readonly Func<string, string?>? testPicker;
    private VersionStore? store;
    private string? source;
    private VersionBrowser? browser;
    private string directory = "";
    private PointRow[] points = [];
    private PointRow? previous;
    private bool busy;
    private Task activeTask = Task.CompletedTask;

    internal VersionWindow(LabWorkspace workspace, Func<string, string?>? testPicker = null)
    {
        this.workspace = workspace;
        this.testPicker = testPicker;
        InitializeComponent();
        RefreshActions();
    }

    private void CreateStoreClick(object sender, RoutedEventArgs e) => PickStore(create: true);
    private void OpenStoreClick(object sender, RoutedEventArgs e) => PickStore(create: false);

    private void PickStore(bool create)
    {
        if (busy)
            return;
        string? selected = PickFolder(create ? "Elige una carpeta vacía para las versiones" : "Elige un almacén existente");
        if (selected is null)
            return;
        Start(async () =>
        {
            store = await Task.Run(() => create ? VersionStore.Initialize(workspace, selected) : VersionStore.Open(workspace, selected));
            source = null;
            SourceText.Text = "Elige un origen para crear otro punto.";
            StoreText.Text = store.Destination;
            OnlyChanges.IsChecked = false;
            await RefreshAsync();
            SetStatus(create ? "Almacén creado." : "Almacén abierto.");
        });
    }

    private void ChooseSourceClick(object sender, RoutedEventArgs e)
    {
        if (busy || store is not { } current)
            return;
        string? selected = PickFolder("Elige una subcarpeta con archivos sintéticos");
        if (selected is null)
            return;
        try
        {
            workspace.CheckRoots(selected, current.Destination);
            source = selected;
            SourceText.Text = selected;
            SetStatus("El origen está listo para analizar y crear un punto.");
            RefreshActions();
        }
        catch (Exception error) when (IsExpected(error)) { SetStatus(error.Message); }
    }

    private void CreatePointClick(object sender, RoutedEventArgs e)
    {
        if (!busy && store is { } current && source is { } origin)
        {
            string id = VersionStore.NewGenerationId();
            Start(() => RunPointAsync(id, () => current.CreatePoint(origin, id)));
        }
    }

    private void ResumeClick(object sender, RoutedEventArgs e)
    {
        if (!busy && store is { } current && Timeline.SelectedItem is PointRow { Pending: not null } row)
            Start(() => RunPointAsync(row.Id, () => current.ResumePoint(row.Id)));
    }

    private async Task RunPointAsync(string id, Action write)
    {
        string message = "Punto confirmado.";
        try { await Task.Run(write); }
        catch (Exception error) when (IsExpected(error)) { message = "El punto requiere revisión. " + error.Message; }
        await RefreshAsync(id);
        SetStatus(message);
    }

    private void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (!busy && store is not null)
            Start(() => RefreshAsync((Timeline.SelectedItem as PointRow)?.Id));
    }

    private async Task RefreshAsync(string? select = null)
    {
        var current = store!;
        points = await Task.Run(() => current.ListPoints().Select(point => new PointRow(point.Id, point, null))
            .Concat(current.ListPending().Select(point => new PointRow(point.Id, null, point)))
            .OrderByDescending(point => point.Id, StringComparer.Ordinal).ToArray());
        Timeline.ItemsSource = points;
        Timeline.SelectedItem = points.FirstOrDefault(row => row.Id == select) ?? points.FirstOrDefault();
        await ShowSelectionAsync();
    }

    private void TimelineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!busy && store is not null)
            Start(ShowSelectionAsync);
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (!busy && store is not null)
            Start(ShowSelectionAsync);
    }

    private async Task ShowSelectionAsync()
    {
        browser = null;
        previous = null;
        ComparisonText.Text = "";
        SetDirectory("");
        if (Timeline.SelectedItem is not PointRow row)
            return;
        if (row.Pending is { } pending)
        {
            SetStatus($"Punto pendiente. Origen registrado · {pending.Source}");
            return;
        }
        if (row.Confirmed is not { Problem: null } point)
        {
            SetStatus("Este punto requiere revisión. " + row.Confirmed?.Problem);
            return;
        }
        previous = points.SkipWhile(candidate => candidate.Id != row.Id).Skip(1).FirstOrDefault(candidate => candidate.Confirmed is not null);
        if (previous?.Confirmed is not { Problem: null })
        {
            OnlyChanges.IsChecked = false;
            ComparisonText.Text = "Sin un punto anterior disponible para comparar.";
        }
        else
            ComparisonText.Text = $"Punto {row.Date} · anterior {previous.Date}";
        string? comparison = OnlyChanges.IsChecked == true ? previous?.Id : null;
        var current = store!;
        var selection = await Task.Run(() =>
        {
            var catalogs = current.ReadCatalogs(point.Id, comparison);
            return (Browser: new VersionBrowser(catalogs.Current, catalogs.Previous), catalogs.Current.Degraded);
        });
        browser = selection.Browser;
        SetDirectory("");
        SetStatus(selection.Degraded ? "Se usó una réplica íntegra del catálogo." : "Punto confirmado. Puedes verificarlo o restaurarlo.");
    }

    private void ParentClick(object sender, RoutedEventArgs e) => SetDirectory(VersionBrowser.Parent(directory));

    private void OpenDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (Files.SelectedItem is VersionEntry { IsDirectory: true } entry)
            SetDirectory(entry.Path);
    }

    private void SetDirectory(string path)
    {
        directory = path;
        DirectoryText.Text = path.Length == 0 ? "Carpeta raíz" : path;
        Files.ItemsSource = browser?.Children(path, OnlyChanges.IsChecked == true);
        RefreshActions();
    }

    private void FilesChanged(object sender, SelectionChangedEventArgs e) => RefreshActions();

    private void VerifyClick(object sender, RoutedEventArgs e)
    {
        if (!busy && store is { } current && Timeline.SelectedItem is PointRow { Confirmed.Problem: null } row)
            Start(async () =>
            {
                bool degraded = await Task.Run(() => current.Verify(row.Id));
                SetStatus("Los objetos del punto coinciden con sus SHA-256." +
                    (degraded ? " Se usó una réplica íntegra del catálogo." : ""));
            });
    }

    private void RestorePointClick(object sender, RoutedEventArgs e) => Restore(relative: null, fromPrevious: false);
    private void RestoreFileClick(object sender, RoutedEventArgs e)
    {
        if (Files.SelectedItem is VersionEntry { IsDirectory: false } entry)
            Restore(entry.Path, entry.Change == VersionChange.Removed);
    }

    private void Restore(string? relative, bool fromPrevious)
    {
        var row = fromPrevious ? previous : Timeline.SelectedItem as PointRow;
        if (busy || store is not { } current || row?.Confirmed is not { Problem: null })
            return;
        string? output = PickFolder("Elige una subcarpeta vacía para restaurar");
        if (output is null)
            return;
        Start(async () =>
        {
            var catalog = await Task.Run(() => current.Restore(row.Id, output, relative));
            SetStatus($"Restauración verificada del punto {row.Date} en {output}." +
                (catalog.Degraded ? " Se usó una réplica íntegra del catálogo." : ""));
        });
    }

    private string? PickFolder(string title)
    {
        if (testPicker is not null)
            return testPicker(title);
        var dialog = new OpenFolderDialog
        {
            Title = title, RootDirectory = workspace.Root, InitialDirectory = workspace.Root,
            AddToRecent = false, DereferenceLinks = false
        };
        return dialog.ShowDialog(this) == true ? Path.TrimEndingDirectorySeparator(dialog.FolderName) : null;
    }

    private void Start(Func<Task> operation) => activeTask = RunAsync(operation);

    private async Task RunAsync(Func<Task> operation)
    {
        busy = true;
        RefreshActions();
        SetStatus("Procesando el almacén…");
        try { await operation(); }
        catch (Exception error) when (IsExpected(error)) { SetStatus("La operación necesita revisión. " + error.Message); }
        finally
        {
            busy = false;
            RefreshActions();
        }
    }

    private void RefreshActions()
    {
        if (NewStoreButton is null || Files is null)
            return;
        var row = Timeline.SelectedItem as PointRow;
        bool confirmed = !busy && row?.Confirmed is { Problem: null };
        var entry = Files.SelectedItem as VersionEntry;
        NewStoreButton.IsEnabled = OpenStoreButton.IsEnabled = Timeline.IsEnabled = !busy;
        ChooseSourceButton.IsEnabled = RefreshButton.IsEnabled = !busy && store is not null;
        CreatePointButton.IsEnabled = ChooseSourceButton.IsEnabled && source is not null;
        ResumeButton.IsEnabled = !busy && row?.Pending is not null;
        VerifyButton.IsEnabled = RestorePointButton.IsEnabled = confirmed;
        RestoreFileButton.IsEnabled = confirmed && entry is { IsDirectory: false };
        OnlyChanges.IsEnabled = confirmed && previous?.Confirmed is { Problem: null };
        OpenDirectoryButton.IsEnabled = confirmed && entry is { IsDirectory: true };
        ParentButton.IsEnabled = confirmed && directory.Length != 0;
        Files.IsEnabled = confirmed;
    }

    private static bool IsExpected(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or Win32Exception or ArgumentException or JsonException;

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        UIElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            SetStatus("Espera a que termine la operación para cerrar.");
        }
        base.OnClosing(e);
    }
}
