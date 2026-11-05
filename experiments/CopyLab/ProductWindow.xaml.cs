using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CopyLab;

public partial class ProductWindow : Window
{
    private sealed record Choice(ProductJobOptions Options, DedicatedDestinationReview Destination);
    private sealed record JobRow(Guid Id, string Label, string Detail, bool Pending, ProductJob? Job = null);
    private readonly Func<ProductJobRegistry> openRegistry;
    private readonly Func<string, string?>? testPicker;
    private readonly VersionIndex productIndex;
    private string? origin;
    private string? parent;
    private Choice? selection;
    private int step;
    private bool busy;
    private Task activeTask = Task.CompletedTask;

    internal ProductWindow(Func<ProductJobRegistry>? openRegistry = null, Func<string, string?>? testPicker = null)
    {
        this.openRegistry = openRegistry ?? ProductJobRegistry.OpenLocal;
        this.testPicker = testPicker;
        productIndex = new(() =>
        {
            using var registry = this.openRegistry();
            return registry.OpenIndexScope();
        });
        InitializeComponent();
        ModeChanged(this, new RoutedEventArgs());
    }

    internal Task InitializeAsync() => RunAsync(LoadListAsync);

    private void NewJobClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        origin = parent = null;
        selection = null;
        ResetProductActions();
        OriginText.Text = ParentText.Text = "";
        JobName.Text = DailyTime.Text = "";
        BackupMode.SelectedIndex = 1;
        JobListPanel.Visibility = Visibility.Collapsed;
        Wizard.Visibility = Visibility.Visible;
        SetStatus("");
        ShowStep(0);
    }

    private void ChooseOriginClick(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder("Elige la carpeta de origen") is not { } chosen) return;
        origin = chosen;
        OriginText.Text = chosen;
        NextStepButton.IsEnabled = true;
        NextStepButton.Focus();
    }

    private void ChooseParentClick(object sender, RoutedEventArgs e)
    {
        if (ChooseFolder("Elige dónde guardar el respaldo") is not { } chosen) return;
        parent = chosen;
        ParentText.Text = chosen;
        NextStepButton.IsEnabled = true;
        NextStepButton.Focus();
    }

    private string? ChooseFolder(string title)
    {
        if (busy) return null;
        if (testPicker is not null) return testPicker(title);
        var dialog = new OpenFolderDialog { Title = title, AddToRecent = false, DereferenceLinks = false };
        return dialog.ShowDialog(this) == true ? Path.TrimEndingDirectorySeparator(dialog.FolderName) : null;
    }

    private void NextStepClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (step == 0 && origin is not null) ShowStep(1);
        else if (step == 1 && parent is not null) ShowStep(2);
        else if (step == 2 && origin is { } source && parent is { } destinationParent)
            Start(async () =>
            {
                var options = ReadOptions();
                var reviewed = await Task.Run(() => LocalJobRoots.ReviewDedicatedDestination(source, destinationParent));
                selection = new(options, reviewed);
                ReviewText.Text = $"{options.Name}\n\nOrigen\n{source}\n\nDestino nuevo\n" +
                    $"{Path.Combine(destinationParent, reviewed.FolderName)}\n\n{ModeName(options.Mode)} · {Schedule(options)}\n\n" +
                    $"Espacio disponible: {reviewed.AvailableBytes / (1024d * 1024 * 1024):N1} GiB. " +
                    "Guardar crea la carpeta dedicada y conserva la configuración.";
                ShowStep(3);
                SetStatus("Revisa la selección antes de guardarla.");
            });
    }

    private ProductJobOptions ReadOptions()
    {
        TimeOnly? time = null;
        if (!string.IsNullOrWhiteSpace(DailyTime.Text))
        {
            if (!TimeOnly.TryParseExact(DailyTime.Text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new InvalidDataException("Introduce una hora en formato HH:mm, por ejemplo 20:30.");
            time = parsed;
        }
        var options = new ProductJobOptions(JobName.Text.Trim(), (ProductBackupMode)BackupMode.SelectedIndex, time);
        options.Validate();
        return options;
    }

    private void SaveJobClick(object sender, RoutedEventArgs e)
    {
        if (busy || step != 3 || selection is not { } current) return;
        Start(async () =>
        {
            try
            {
                await Task.Run(() =>
                {
                    using var registry = openRegistry();
                    registry.Create(current.Options, current.Destination);
                });
                ShowJobs();
                await LoadListAsync();
                SelectJob(current.Destination.FolderId);
                SetStatus("Trabajo guardado. Puedes volver a abrir su configuración.");
            }
            catch (Exception error) when (VersionJob.Expected(error))
            {
                ShowJobs();
                await LoadListAsync();
                SelectJob(current.Destination.FolderId);
                SetStatus(error.Message + " Si el registro quedó pendiente, puedes seleccionarlo y reanudarlo.");
            }
        });
    }

    private void BackStepClick(object sender, RoutedEventArgs e)
    {
        if (busy || step == 0) return;
        selection = null;
        ShowStep(step - 1);
    }

    private void CancelWizardClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        ShowJobs();
        SetStatus("Selección descartada.");
    }

    private void RefreshJobsClick(object sender, RoutedEventArgs e)
    {
        if (!busy) Start(LoadListAsync);
    }

    private void ResumeRegistrationClick(object sender, RoutedEventArgs e)
    {
        if (busy || JobList.SelectedItem is not JobRow { Pending: true } row) return;
        Start(async () =>
        {
            await Task.Run(() =>
            {
                using var registry = openRegistry();
                registry.Resume(row.Id);
            });
            await LoadListAsync();
            SelectJob(row.Id);
            SetStatus("Registro completado.");
        });
    }

    private void JobSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResumeRegistrationButton is not null)
            ResumeRegistrationButton.IsEnabled = !busy && JobList.SelectedItem is JobRow { Pending: true };
        if (BackupJobButton is not null) RefreshProductActions();
    }

    private void SelectJob(Guid id)
    {
        JobList.SelectedItem = JobList.Items.OfType<JobRow>().FirstOrDefault(row => row.Id == id);
    }

    private async Task LoadListAsync()
    {
        var list = await Task.Run(() =>
        {
            using var registry = openRegistry();
            return registry.List();
        });
        JobRow[] rows = [.. list.Jobs.Select(job => new JobRow(job.Id, job.Options.Name,
            $"{ModeName(job.Options.Mode)} · {Schedule(job.Options)}", false, job)),
            .. list.Pending.Select(id => new JobRow(id, "Registro pendiente",
                $"Selecciona Reanudar registro para continuar · {id.ToString("N")[..8]}", true))];
        JobList.ItemsSource = rows;
        EmptyJobsText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetStatus(list.Unreadable > 0 ? $"{list.Unreadable} registros necesitan revisión." : "");
    }

    private void ShowJobs()
    {
        selection = null;
        RecoveryPanel.Visibility = Visibility.Collapsed;
        Wizard.Visibility = Visibility.Collapsed;
        JobListPanel.Visibility = Visibility.Visible;
        NewJobButton.Focus();
    }

    private void ShowStep(int value)
    {
        step = value;
        StepNumber.Text = $"Paso {value + 1} de 4";
        StepTitle.Text = value switch { 0 => "Qué quieres respaldar", 1 => "Dónde guardarlo", 2 => "Cómo respaldarlo", _ => "Revisa el trabajo" };
        OriginStep.Visibility = VisibilityFor(0);
        DestinationStep.Visibility = VisibilityFor(1);
        OptionsStep.Visibility = VisibilityFor(2);
        ReviewStep.Visibility = VisibilityFor(3);
        BackStepButton.IsEnabled = value > 0;
        NextStepButton.Visibility = value == 3 ? Visibility.Collapsed : Visibility.Visible;
        NextStepButton.IsDefault = value != 3;
        NextStepButton.IsEnabled = value switch { 0 => origin is not null, 1 => parent is not null, _ => true };
        SaveJobButton.Visibility = VisibilityFor(3);
        SaveJobButton.IsDefault = value == 3;
        if (!busy) FocusCurrentStep();

        Visibility VisibilityFor(int target) => value == target ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FocusCurrentStep()
    {
        int expectedStep = step;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!busy && step == expectedStep && Wizard.IsVisible)
                (step switch { 0 => ChooseOriginButton, 1 => ChooseParentButton, 2 => (Control)JobName, _ => SaveJobButton }).Focus();
        }, DispatcherPriority.Input);
    }

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (ModeDetail is null) return;
        ModeDetail.Text = (ProductBackupMode)BackupMode.SelectedIndex switch
        {
            ProductBackupMode.Accumulative => "Añade y actualiza archivos, conserva los exclusivos del destino y detiene los conflictos con ediciones externas.",
            ProductBackupMode.Versions => "Conserva fechas recuperables, incluso si se borra un archivo del origen.",
            ProductBackupMode.ProtectedMirror => "Mantiene una carpeta actual y conserva el contenido retirado.",
            _ => ""
        };
    }

    private void Start(Func<Task> action) => activeTask = RunAsync(action);

    private async Task RunAsync(Func<Task> action)
    {
        int previousStep = step;
        bool wasWizard = Wizard.Visibility == Visibility.Visible;
        busy = true;
        Wizard.IsEnabled = JobListPanel.IsEnabled = RecoveryPanel.IsEnabled = false;
        SetStatus("Comprobando la selección…");
        try { await action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { SetStatus(error.Message); }
        finally
        {
            busy = false;
            Wizard.IsEnabled = JobListPanel.IsEnabled = RecoveryPanel.IsEnabled = true;
            ResumeRegistrationButton.IsEnabled = JobList.SelectedItem is JobRow { Pending: true };
            RefreshProductActions();
            if (Wizard.Visibility == Visibility.Visible && previousStep != step) FocusCurrentStep();
            if (wasWizard && JobListPanel.Visibility == Visibility.Visible)
                (JobList.SelectedItem is null ? (Control)NewJobButton : JobList).Focus();
        }
    }

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
            SetStatus("Espera a que termine la operación para cerrar la ventana.");
        }
        base.OnClosing(e);
    }

    private static string ModeName(ProductBackupMode mode) => mode switch
    {
        ProductBackupMode.Accumulative => "Copia acumulativa",
        ProductBackupMode.ProtectedMirror => "Espejo protegido",
        _ => "Versionado"
    };

    private static string Schedule(ProductJobOptions options) =>
        options.DailyAt is { } time ? "Horario propuesto " + time.ToString("HH:mm", CultureInfo.InvariantCulture) : "A demanda";
}
