using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CopyLab;

public partial class MainWindow : Window
{
    private readonly LabSession session;
    private readonly Queue<string?>? testSelections;
    private bool sourceSelected;
    private bool destinationSelected;
    private bool analyzed;
    private bool copied;
    private bool restored;
    private bool busy;
    private Task activeTask = Task.CompletedTask;

    internal MainWindow(bool smoke)
    {
        InitializeComponent();
        session = new LabSession();
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
            InventoryResult result = await Task.Run(session.Analyze);
            analyzed = result.Complete;
            PreviewText.Text = analyzed
                ? $"Archivo nuevo · muestra.txt · {result.Bytes:N0} bytes\nSe copiará a Destino y se restaurará en una carpeta separada."
                : $"Análisis incompleto · {result.Issues.Length:N0} incidencia(s). Revisa el origen antes de copiar.";
            SetStatus(analyzed ? "Revisión completada. Puedes copiar el archivo de prueba." : "El análisis necesita revisión.");
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
        SetStatus(restore ? "Restaurando y verificando…" : "Copiando y verificando…");
        try
        {
            if (restore)
            {
                await Task.Run(session.Restore);
                restored = true;
                SetStatus("Restauración verificada. El archivo en Restaurado coincide con el original.");
            }
            else
            {
                await Task.Run(session.Copy);
                copied = true;
                SetStatus("Copia verificada. Ya puedes probar la restauración.");
            }
        }
        catch (Exception error) when (FileUseDiagnostics.IsSharingViolation(error))
        {
            var users = await Task.Run(() => FileUseDiagnostics.TryGetUsers(restore ? session.CopyFile : session.SourceFile));
            string[] applications = users.Select(user => user.Application.Trim())
                .Where(name => name.Length != 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
            string detail = applications.Length == 0 ? "" : " Aplicaciones detectadas por Windows: " + string.Join(", ", applications) + ".";
            SetStatus("La prueba no se completó. Un archivo está en uso. Cierra el archivo y vuelve a intentarlo." + detail);
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
        SourceButton.IsEnabled = DestinationButton.IsEnabled = !busy && !copied;
        AnalyzeButton.IsEnabled = !busy && !copied && sourceSelected && destinationSelected;
        CopyButton.IsEnabled = AnalyzeButton.IsEnabled && analyzed;
        RestoreButton.IsEnabled = !busy && copied && !restored;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
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
        Check(analyzed && CopyButton.IsEnabled && PreviewText.Text.Contains("muestra.txt", StringComparison.Ordinal),
            "The valid preview did not enable the copy.");
        using (var writer = new FileStream(session.SourceFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            Click(CopyButton);
            await activeTask;
            Check(!copied && !File.Exists(session.CopyFile) && !RestoreButton.IsEnabled &&
                StatusText.Text.StartsWith("La prueba no se completó.", StringComparison.Ordinal) &&
                StatusText.Text.Contains("está en uso", StringComparison.Ordinal), "Copy failure was hidden.");
        }
        Click(CopyButton);
        await activeTask;
        Check(copied && File.Exists(session.CopyFile) && RestoreButton.IsEnabled, StatusText.Text);
        Click(RestoreButton);
        await activeTask;
        Check(restored && !RestoreButton.IsEnabled, StatusText.Text);
        Check(File.ReadAllBytes(session.RestoredFile).AsSpan().SequenceEqual(File.ReadAllBytes(session.SourceFile)),
            "Restored file differs from the source.");

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
