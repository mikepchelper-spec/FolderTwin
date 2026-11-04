using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CopyLab;

public partial class MainWindow : Window
{
    private readonly BatchFixture session;
    private readonly Queue<string?>? testSelections;
    private bool sourceSelected;
    private bool destinationSelected;
    private bool analyzed;
    private bool copyStarted;
    private bool copied;
    private bool restored;
    private bool busy;
    private Task activeTask = Task.CompletedTask;

    internal MainWindow(bool smoke)
    {
        InitializeComponent();
        session = new BatchFixture();
        if (smoke)
            testSelections = new Queue<string?>([null, session.Destination, session.Source, session.Destination]);
    }

    private void ChooseSource(object sender, RoutedEventArgs e) =>
        SelectFolder("Elegir origen", session.Source, SourceText, ref sourceSelected);

    private void ChooseDestination(object sender, RoutedEventArgs e) =>
        SelectFolder("Elegir destino", session.Destination, DestinationText, ref destinationSelected);

    private void SelectFolder(string title, string expected, TextBlock label, ref bool selected)
    {
        string? candidate = PickFolder(title);
        if (candidate is null)
            return;
        selected = string.Equals(Path.TrimEndingDirectorySeparator(candidate), expected, StringComparison.OrdinalIgnoreCase);
        analyzed = false;
        PreviewText.Text = "Analiza las carpetas para revisar qué se copiará.";
        string folder = Path.GetFileName(expected);
        label.Text = selected ? $"{folder} · carpeta de prueba" : $"Elige la carpeta {folder} del laboratorio.";
        SetStatus(selected
            ? sourceSelected && destinationSelected ? "Todo listo para analizar las carpetas de prueba." : "Selecciona la otra carpeta."
            : $"Para esta prueba, elige la carpeta {folder}.");
        RefreshActions();
    }

    private string? PickFolder(string title)
    {
        if (testSelections is not null)
            return testSelections.Dequeue();
        var dialog = new OpenFolderDialog
        {
            Title = title,
            RootDirectory = session.Root,
            InitialDirectory = session.Root,
            AddToRecent = false,
            DereferenceLinks = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private void AnalyzeClick(object sender, RoutedEventArgs e)
    {
        if (AnalyzeButton.IsEnabled)
            activeTask = AnalyzeAsync();
    }

    private async Task AnalyzeAsync()
    {
        busy = true;
        analyzed = false;
        PreviewText.Text = "Analizando las carpetas…";
        SetStatus("Analizando sin modificar los archivos…");
        RefreshActions();
        try
        {
            BatchPlan plan = await Task.Run(session.Analyze);
            analyzed = true;
            PreviewText.Text = $"{plan.Files.Length:N0} archivos · {plan.Bytes:N0} bytes · {plan.Directories.Length:N0} carpetas\n" +
                string.Join("\n", plan.Files.Select(file => file.RelativePath));
            ResultsList.ItemsSource = null;
            SetStatus("Revisión completada. Puedes copiar el lote de prueba.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or Win32Exception or ArgumentException)
        {
            PreviewText.Text = "No hay una vista previa válida para copiar.";
            SetStatus("El análisis no se completó. " + error.Message);
        }
        finally
        {
            busy = false;
            RefreshActions();
        }
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        if (CopyButton.IsEnabled)
            activeTask = RunAsync(restore: false);
    }

    private void RestoreClick(object sender, RoutedEventArgs e)
    {
        if (RestoreButton.IsEnabled)
            activeTask = RunAsync(restore: true);
    }

    private async Task RunAsync(bool restore)
    {
        busy = true;
        RefreshActions();
        ResultsList.ItemsSource = null;
        SetStatus(restore ? "Restaurando y verificando…" : "Copiando y verificando…");
        if (!restore)
            copyStarted = true;
        try
        {
            BatchResult result = await Task.Run(() => restore ? session.Restore() : session.Copy());
            if (restore)
                restored = result.Complete;
            else
                copied = result.Complete;
            ResultsList.ItemsSource = result.Entries.Select(entry => $"{entry.RelativePath} · {Describe(entry)}").ToArray();
            string[] inUse = result.Entries.Where(entry => entry.InUse).Take(16)
                .Select(entry => Path.Combine(restore ? session.Destination : session.Source, entry.RelativePath)).ToArray();
            var users = await Task.Run(() => FileUseDiagnostics.TryGetUsers(inUse));
            string[] applications = users.Select(user => user.Application.Trim())
                .Where(name => name.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            string detail = applications.Length == 0 ? "" : " Aplicaciones detectadas por Windows: " + string.Join(", ", applications) + ".";
            string status = result.Complete
                ? restore ? $"Restauración verificada: {result.VerifiedFiles:N0} archivos y sus carpetas."
                    : $"Lote copiado y verificado: {result.VerifiedFiles:N0} archivos. Ya puedes restaurarlo."
                : $"Resultado parcial: {result.VerifiedFiles:N0} de {BatchFixture.SampleNames.Length:N0} archivos verificados. Revisa las incidencias y reintenta.";
            SetStatus(status + (result.Problem is null ? "" : " " + result.Problem) +
                (result.Note is null ? "" : " " + result.Note) + detail);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or Win32Exception or ArgumentException)
        {
            SetStatus($"La prueba no se completó. {error.Message}");
        }
        finally
        {
            busy = false;
            RefreshActions();
        }
    }

    private void RefreshActions()
    {
        SourceButton.IsEnabled = DestinationButton.IsEnabled = !busy && !copyStarted;
        AnalyzeButton.IsEnabled = !busy && !copyStarted && sourceSelected && destinationSelected;
        CopyButton.IsEnabled = !busy && !copied && analyzed;
        CopyButton.Content = copyStarted && !copied ? "_Reintentar copia" : "_Copiar y verificar";
        RestoreButton.IsEnabled = !busy && copied && !restored;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        UIElementAutomationPeer.FromElement(StatusText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static string Describe(BatchEntryResult entry) => entry.Status switch
    {
        BatchStatus.Copied => "Copia verificada",
        BatchStatus.Recovered => "Temporal recuperado y verificado",
        BatchStatus.AlreadyVerified => "Copia existente verificada",
        BatchStatus.DirectoryReady => "Carpeta preparada",
        _ => entry.Detail ?? "Pendiente de revisión"
    };

    protected override void OnClosing(CancelEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            SetStatus("Espera a que termine la operación para cerrar la ventana.");
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            session.Dispose();
        }
        catch (Exception error) when (testSelections is null &&
            error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show("La carpeta temporal necesita revisión. " + error.Message,
                "FolderTwin · Laboratorio", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        base.OnClosed(e);
    }

    internal async Task RunSmokeAsync()
    {
        Click(SourceButton); // Cancelled selection.
        Check(!sourceSelected && !CopyButton.IsEnabled, "Cancellation changed selection.");
        Click(SourceButton); // Generated folder, but the wrong role.
        Check(!sourceSelected && !CopyButton.IsEnabled, "Wrong source enabled copying.");
        Click(SourceButton);
        Check(sourceSelected && !CopyButton.IsEnabled, "Copy requires a destination.");
        Click(DestinationButton);
        Check(AnalyzeButton.IsEnabled && !CopyButton.IsEnabled && !RestoreButton.IsEnabled,
            "Copy became available before analysis.");
        File.WriteAllText(session.CopyFile, "synthetic conflict");
        try
        {
            Click(AnalyzeButton);
            await activeTask;
            Check(!analyzed && !CopyButton.IsEnabled && File.ReadAllText(session.CopyFile) == "synthetic conflict" &&
                StatusText.Text.StartsWith("El análisis no se completó.", StringComparison.Ordinal), "Analysis accepted or changed a conflicting destination.");
        }
        finally
        {
            File.Delete(session.CopyFile);
        }
        Click(AnalyzeButton);
        await activeTask;
        Check(analyzed && CopyButton.IsEnabled && PreviewText.Text.Contains("3 archivos", StringComparison.Ordinal),
            "The valid preview did not enable the copy.");
        using (var writer = new FileStream(session.SourceFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            Click(CopyButton);
            await activeTask;
            Check(!copied && !File.Exists(session.CopyFile) && !RestoreButton.IsEnabled &&
                StatusText.Text.StartsWith("Resultado parcial: 2 de 3", StringComparison.Ordinal) &&
                ResultsList.Items.Cast<string>().Any(row => row.Contains("está en uso", StringComparison.Ordinal)) &&
                !AnalyzeButton.IsEnabled && CopyButton.IsEnabled, "A partial batch or its retry was hidden.");
        }
        Click(CopyButton);
        await activeTask;
        Check(copied && File.Exists(session.CopyFile) && RestoreButton.IsEnabled, StatusText.Text);
        session.ForgetPlan();
        string primaryCatalog = Path.Combine(session.Destination, BatchCatalog.PrimaryName);
        string backupCatalog = Path.Combine(session.Destination, BatchCatalog.BackupName);
        byte[] intactCatalog = File.ReadAllBytes(backupCatalog);
        File.WriteAllText(primaryCatalog, "truncated\n");
        File.WriteAllText(backupCatalog, "truncated\n");
        Click(RestoreButton);
        await activeTask;
        Check(!restored && !File.Exists(session.RestoredFile) && RestoreButton.IsEnabled &&
            StatusText.Text.StartsWith("La prueba no se completó.", StringComparison.Ordinal),
            "Invalid catalogs enabled restoration or hid the error.");
        File.WriteAllBytes(backupCatalog, intactCatalog);
        Click(RestoreButton);
        await activeTask;
        Check(restored && !RestoreButton.IsEnabled && StatusText.Text.Contains("réplica íntegra", StringComparison.Ordinal), StatusText.Text);
        session.CheckRestored();

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
