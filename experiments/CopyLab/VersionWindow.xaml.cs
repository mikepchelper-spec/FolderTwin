using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
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
            { Expired: true } => $"{Date} · expirado · revisar cuarentena",
            { } point => $"{Date} · {point.FileCount:N0} archivos" + (point.Degraded ? " · una réplica" : ""),
            _ => $"{Date} · pendiente · {Pending!.FileCount:N0} archivos"
        };
    }

    private readonly LabWorkspace workspace;
    private readonly VersionIndex index;
    private readonly Func<string, string?>? testPicker;
    private VersionStore? store;
    private string? source;
    private Guid? backgroundJob;
    private VersionBrowser? browser;
    private string? indexKey;
    private string? comparisonId;
    private const string IndexUnavailable = " Índice local no disponible. Vista leída desde los catálogos.";
    private string directory = "";
    private PointRow[] points = [];
    private PointRow? previous;
    private bool busy;
    private Task activeTask = Task.CompletedTask;

    internal VersionWindow(LabWorkspace workspace, Func<string, string?>? testPicker = null)
    {
        this.workspace = workspace;
        index = new(workspace);
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
            ResetMirrorReview();
            retentionReview = null;
            RetentionText.Text = "";
            AcceptRetention.IsChecked = AcceptPurge.IsChecked = false;
            backgroundJob = null;
            SourceText.Text = "Elige un origen para crear otro punto.";
            StoreText.Text = store.Destination;
            OnlyChanges.IsChecked = false;
            await RefreshAsync();
            if (points.Length == 0)
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
            ResetMirrorReview();
            backgroundJob = null;
            JobChoice.SelectedItem = null;
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

    private void BackgroundClick(object sender, RoutedEventArgs e)
    {
        if (!busy && store is { } current && source is { } origin)
            Start(async () =>
            {
                backgroundJob ??= await Task.Run(() => VersionJob.Create(workspace, origin, current.Destination));
                LabWorker.Start(workspace, backgroundJob.Value);
                SetStatus("Trabajo iniciado en segundo plano. Puedes cerrar la ventana. Actualiza el almacén para revisar los puntos confirmados.");
            });
    }

    private void ScheduleClick(object sender, RoutedEventArgs e)
    {
        if (busy || store is not { } current || source is not { } origin)
            return;
        if (!TimeOnly.TryParseExact(ScheduleTime.Text, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var time))
        {
            SetStatus("Escribe la hora como HH:mm, por ejemplo 20:00.");
            return;
        }
        Start(async () =>
        {
            DateTime first = DateTime.Today.Add(time.ToTimeSpan());
            if (first <= DateTime.Now)
                first = first.AddDays(1);
            backgroundJob ??= await Task.Run(() => VersionJob.Create(workspace, origin, current.Destination));
            Guid id = backgroundJob.Value;
            await Task.Run(() =>
            {
                string executable = LabInstallation.EnsureWorker();
                using var scheduler = new WindowsSchedule();
                scheduler.Register(workspace, id, executable, first);
            });
            ScheduleText.Text = $"Cada día a las {time:HH:mm}, con tu sesión iniciada. El ejecutor queda instalado en LocalAppData.";
            SetStatus("Horario registrado y comprobado. Puedes cerrar la interfaz.");
        });
    }

    private void RemoveScheduleClick(object sender, RoutedEventArgs e)
    {
        if (busy || store is null)
            return;
        Start(async () =>
        {
            if (backgroundJob is not { } id)
            {
                SetStatus("Elige un trabajo guardado para retirar su horario.");
                return;
            }
            await Task.Run(() =>
            {
                using var scheduler = new WindowsSchedule();
                scheduler.Remove(workspace, id);
            });
            ScheduleText.Text = "Horario retirado.";
            SetStatus("Se retiró la ejecución automática de este trabajo.");
        });
    }

    private void JobChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!busy && JobChoice.SelectedItem is VersionJobInfo job)
            Start(async () =>
            {
                SelectJob(job);
                await ShowScheduleAsync();
                SetStatus("Trabajo seleccionado.");
            });
    }

    private void SelectJob(VersionJobInfo job)
    {
        ResetMirrorReview();
        backgroundJob = job.Id;
        source = Path.Combine(workspace.Root, job.Source);
        SourceText.Text = "Origen registrado · " + source;
    }

    private void StartupClick(object sender, RoutedEventArgs e)
    {
        if (busy)
            return;
        bool enabled = TrayAtLogin.IsChecked == true;
        Start(async () =>
        {
            await Task.Run(() =>
            {
                string? executable = enabled ? LabInstallation.EnsureWorker() : null;
                using var scheduler = new WindowsSchedule();
                scheduler.SetTrayStartup(workspace, executable, enabled);
            });
            SetStatus(enabled ? "El estado se abrirá en la bandeja al iniciar sesión." : "Se retiró el inicio automático de la bandeja.");
        });
    }

    private async Task ShowScheduleAsync()
    {
        if (Path.GetDirectoryName(workspace.Root) != LabWorkspace.PersistentParent)
        {
            ScheduleText.Text = "Para programar, crea un nuevo espacio de pruebas persistente.";
            return;
        }
        try
        {
            var startup = await Task.Run(() =>
            {
                using var scheduler = new WindowsSchedule();
                return scheduler.ReadTray(workspace);
            });
            TrayAtLogin.IsChecked = startup is { Enabled: true, MeetsPolicy: true };
            if (backgroundJob is not { } id)
            {
                ScheduleText.Text = "El horario se ejecuta mientras tengas la sesión iniciada.";
                return;
            }
            var information = await Task.Run(() =>
            {
                using var scheduler = new WindowsSchedule();
                return scheduler.Read(workspace, id);
            });
            ScheduleText.Text = information switch
            {
                null => "Este trabajo no tiene horario.",
                { MeetsPolicy: false } => "El horario requiere revisión de sus opciones en Windows.",
                { Enabled: false } => "Horario deshabilitado en Windows.",
                { Running: true } => "Ejecución programada en curso.",
                { LastResult: not (0 or 0x41303) } => "El último intento programado requiere revisión. Puedes consultar el Programador de tareas.",
                { } task => $"Próxima ejecución · {task.NextRun:g}, con tu sesión iniciada."
            };
        }
        catch (Exception error) when (IsExpected(error))
        {
            ScheduleText.Text = "No se pudo consultar el horario. " + error.Message;
        }
    }

    private async Task RunPointAsync(string id, Action write)
    {
        string message = "Punto confirmado.";
        try { await Task.Run(write); }
        catch (Exception error) when (IsExpected(error)) { message = "El punto requiere revisión. " + error.Message; }
        await RefreshAsync(id);
        SetStatus(message + (browser is null ? "" : IndexUnavailable));
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
        var jobs = await Task.Run(() =>
        {
            var catalog = VersionJob.List(workspace);
            var own = catalog.Jobs.Where(job => job.TargetMarker == current.Binding.Marker &&
                job.Destination == Path.GetFileName(current.Destination)).ToArray();
            var histories = own.Select(job => (Job: job, History: VersionJob.History(workspace, job.Id))).ToArray();
            return (Entries: histories, Unreadable: catalog.Unreadable + histories.Sum(entry => entry.History.Unreadable));
        });
        int failed = jobs.Entries.Count(entry => entry.History.Results.FirstOrDefault()?.State is { } state &&
            state != VersionJobState.Confirmed);
        JobsText.Text = string.Join("\n", jobs.Entries.Take(4).Select(entry =>
            entry.Job.Source + " · " + (entry.History.Results.FirstOrDefault() is { } result ?
                JobStateText(result.State) : "sin resultado registrado"))) +
            (jobs.Entries.Length > 4 ? $"\nHay {jobs.Entries.Length - 4} trabajos adicionales." : "") +
            (failed > 0 ? $"\n{failed} trabajos tienen un último intento pendiente de resolver." : "") +
            (jobs.Unreadable > 0 ? $"\n{jobs.Unreadable} registros de trabajos requieren revisión." : "");
        JobChoice.ItemsSource = jobs.Entries.Select(entry => entry.Job).ToArray();
        var chosen = jobs.Entries.Select(entry => entry.Job).FirstOrDefault(job => job.Id == backgroundJob) ??
            (source is null && jobs.Entries.Length == 1 ? jobs.Entries[0].Job : null);
        JobChoice.SelectedItem = chosen;
        if (chosen is not null)
            SelectJob(chosen);
        await ShowScheduleAsync();
        await ShowRetentionAsync();
        await ShowMirrorAsync();
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
        indexKey = null;
        comparisonId = null;
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
            ComparisonText.Text = $"Punto {row.Date} · anterior {previous.Date}" +
                (previous.Confirmed.Expired ? " · El anterior expiró. Deshaz su cuarentena antes de recuperar archivos eliminados." : "");
        comparisonId = OnlyChanges.IsChecked == true ? previous?.Id : null;
        var current = store!;
        var selection = await Task.Run(() =>
        {
            var catalogs = current.ReadCatalogs(point.Id, comparisonId);
            var key = VersionIndex.Key(catalogs.Current, catalogs.Previous);
            VersionBrowser Build() => new(catalogs.Current, catalogs.Previous);
            try
            {
                index.Ensure(key, Build);
                return (Key: (string?)key, Fallback: (VersionBrowser?)null, catalogs.Current.Degraded);
            }
            catch (Exception error) when (VersionIndex.IsUnavailable(error))
            {
                return (Key: (string?)null, Fallback: (VersionBrowser?)Build(), catalogs.Current.Degraded);
            }
        });
        browser = selection.Fallback;
        indexKey = selection.Key;
        await LoadDirectoryAsync("");
        string message = point.Expired ? "Punto expirado. Su catálogo sigue disponible; deshaz su cuarentena para restaurarlo." :
            selection.Degraded ? "Se usó una réplica íntegra del catálogo." : "Punto confirmado. Puedes verificarlo o restaurarlo.";
        SetStatus(message + (browser is null ? "" : IndexUnavailable));
    }

    private void ParentClick(object sender, RoutedEventArgs e)
    {
        if (!busy && directory.Length != 0)
            Start(() => LoadDirectoryAsync(VersionBrowser.Parent(directory)));
    }

    private void OpenDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (!busy && Files.SelectedItem is VersionEntry { IsDirectory: true } entry)
            Start(() => LoadDirectoryAsync(entry.Path));
    }

    private async Task LoadDirectoryAsync(string path)
    {
        bool onlyChanges = OnlyChanges.IsChecked == true;
        VersionEntry[] entries;
        if (indexKey is { } key)
        {
            try
            {
                entries = await Task.Run(() => index.Children(key, path, onlyChanges));
                SetDirectory(path, entries);
                SetStatus($"{entries.Length:N0} elementos en esta carpeta.");
                return;
            }
            catch (Exception error) when (VersionIndex.IsUnavailable(error))
            {
                var current = store!;
                var row = (PointRow)Timeline.SelectedItem;
                browser = await Task.Run(() =>
                {
                    var catalogs = current.ReadCatalogs(row.Id, comparisonId);
                    return new VersionBrowser(catalogs.Current, catalogs.Previous);
                });
                indexKey = null;
            }
        }
        entries = browser?.Children(path, onlyChanges) ?? [];
        SetDirectory(path, entries);
        SetStatus($"{entries.Length:N0} elementos en esta carpeta." + (browser is null ? "" : IndexUnavailable));
    }

    private void SetDirectory(string path, VersionEntry[]? entries = null)
    {
        directory = path;
        DirectoryText.Text = path.Length == 0 ? "Carpeta raíz" : path;
        Files.ItemsSource = entries;
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
        if (busy || store is not { } current || row?.Confirmed is not { Problem: null, Expired: false })
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
        bool retained = confirmed && row?.Confirmed?.Expired == false;
        var entry = Files.SelectedItem as VersionEntry;
        NewStoreButton.IsEnabled = OpenStoreButton.IsEnabled = Timeline.IsEnabled = !busy;
        ChooseSourceButton.IsEnabled = RefreshButton.IsEnabled = !busy && store is not null;
        CreatePointButton.IsEnabled = ChooseSourceButton.IsEnabled && source is not null;
        BackgroundButton.IsEnabled = CreatePointButton.IsEnabled;
        bool persistent = Path.GetDirectoryName(workspace.Root) == LabWorkspace.PersistentParent;
        ScheduleButton.IsEnabled = ScheduleTime.IsEnabled = CreatePointButton.IsEnabled && persistent;
        RemoveScheduleButton.IsEnabled = !busy && backgroundJob is not null && persistent;
        TrayAtLogin.IsEnabled = ApplyStartupButton.IsEnabled = !busy && persistent;
        JobChoice.IsEnabled = !busy && store is not null;
        ResumeButton.IsEnabled = !busy && row?.Pending is not null;
        VerifyButton.IsEnabled = RestorePointButton.IsEnabled = retained;
        RestoreFileButton.IsEnabled = retained && entry is { IsDirectory: false } &&
            (entry.Change != VersionChange.Removed || previous?.Confirmed?.Expired == false);
        OnlyChanges.IsEnabled = confirmed && previous?.Confirmed is { Problem: null };
        OpenDirectoryButton.IsEnabled = confirmed && entry is { IsDirectory: true };
        ParentButton.IsEnabled = confirmed && directory.Length != 0;
        Files.IsEnabled = confirmed;
        RefreshRetentionActions();
        RefreshMirrorActions();
    }

    private static bool IsExpected(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or Win32Exception or ArgumentException or JsonException or COMException;

    private static string JobStateText(VersionJobState state) => state switch
    {
        VersionJobState.Confirmed => "punto confirmado",
        VersionJobState.Busy => "otro trabajo estaba en curso",
        VersionJobState.WaitingForSource => "origen ausente",
        VersionJobState.WaitingForDestination => "destino ausente",
        _ => "requiere revisión"
    };

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
