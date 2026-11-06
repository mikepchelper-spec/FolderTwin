using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace CopyLab;

public partial class ProductWindow
{
    private ProductConfiguration? importedConfiguration;
    private ProductImportReview? importReview;
    private string? importDestination;
    private string? importSource;

    private string? ChooseConfigurationFile(bool save)
    {
        if (testConfigurationPicker is not null) return testConfigurationPicker(save);
        const string filter = "Configuración de FolderTwin (*.foldertwin.json)|*.foldertwin.json";
        if (save)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Exportar a un archivo nuevo", Filter = filter, DefaultExt = ".foldertwin.json", AddToRecent = false,
                FileName = $"FolderTwin-{DateTime.Now:yyyyMMdd-HHmmss}.foldertwin.json", OverwritePrompt = false
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        var open = new OpenFileDialog { Title = "Importar configuración", Filter = filter, AddToRecent = false, CheckFileExists = true };
        return open.ShowDialog(this) == true ? open.FileName : null;
    }

    private void ExportConfigurationClick(object sender, RoutedEventArgs e)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { } job || ChooseConfigurationFile(save: true) is not { } file) return;
        Start(async () =>
        {
            await Task.Run(() => { using var registry = openRegistry(); registry.ExportConfiguration(job.Id, file); });
            SetStatus("Configuración exportada. Incluye nombre, modo, horario, rutas e identidades. Revisa el archivo antes de compartirlo.");
        });
    }

    private void ImportConfigurationClick(object sender, RoutedEventArgs e)
    {
        if (busy || ChooseConfigurationFile(save: false) is not { } file) return;
        Start(async () =>
        {
            importedConfiguration = await Task.Run(() => ProductJobRegistry.ReadConfiguration(file));
            importDestination = importSource = null;
            JobListPanel.Visibility = Wizard.Visibility = RecoveryPanel.Visibility = Visibility.Collapsed;
            ImportPanel.Visibility = Visibility.Visible;
            ImportDestinationText.Text = "Elige la carpeta dedicada que contiene el respaldo original.";
            ImportSourceText.Text = "Origen sin revisar. Puedes importar para recuperar y revisarlo más adelante.";
            ImportSummaryText.Text = $"{importedConfiguration.Options.Name}\n{ModeName(importedConfiguration.Options.Mode)} · {Schedule(importedConfiguration.Options)}\n\n" +
                "El trabajo se guardará deshabilitado. Comprueba el respaldo existente. Para habilitar nuevas copias se exigirá revisar también el origen.";
            InvalidateImportReview();
            SetStatus("Revisa las carpetas antes de importar. El respaldo se conserva.");
        });
    }

    private void ChooseImportDestinationClick(object sender, RoutedEventArgs e)
    {
        if (busy || ChooseFolder("Elige la carpeta dedicada del respaldo existente") is not { } path) return;
        importDestination = path;
        ImportDestinationText.Text = path;
        InvalidateImportReview();
    }
    private void ChooseImportSourceClick(object sender, RoutedEventArgs e)
    {
        if (busy || ChooseFolder("Elige el origen original para futuras copias") is not { } path) return;
        importSource = path;
        ImportSourceText.Text = path;
        InvalidateImportReview();
    }
    private void ClearImportSourceClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        importSource = null;
        ImportSourceText.Text = "Origen sin revisar. Se habilita la recuperación; las nuevas copias requieren revisar el origen.";
        InvalidateImportReview();
    }
    private void InvalidateImportReview()
    {
        importReview = null;
        AcceptImport.IsChecked = false;
        ImportReviewText.Text = "";
        ReviewImportButton.IsEnabled = importDestination is not null;
        SaveImportButton.IsEnabled = false;
    }
    private void ImportAcceptedChanged(object sender, RoutedEventArgs e)
    {
        if (SaveImportButton is not null) SaveImportButton.IsEnabled = !busy && importReview is not null && AcceptImport.IsChecked == true;
    }
    private void ReviewImportClick(object sender, RoutedEventArgs e)
    {
        if (busy || importedConfiguration is not { } configuration || importDestination is not { } destination) return;
        Start(async () =>
        {
            importReview = null;
            AcceptImport.IsChecked = false;
            string? source = importSource;
            importReview = await Task.Run(() => { using var registry = openRegistry(); return registry.ReviewImport(configuration, destination, source); });
            ImportReviewText.Text = $"{configuration.Options.Name}\n{ModeName(configuration.Options.Mode)}\nRespaldo comprobado\n{destination}\n\n" +
                (source is null ? "Origen pendiente de revisión. Podrás recuperar los respaldos." : $"Origen comprobado\n{source}") +
                "\n\nEl trabajo se guardará deshabilitado y conservará las fechas del respaldo. Puedes habilitarlo después desde Editar trabajo y horario.";
            SetStatus("Identidades comprobadas. Revisa la selección antes de guardar.");
        });
    }
    private void SaveImportClick(object sender, RoutedEventArgs e)
    {
        if (busy || importReview is not { } review || AcceptImport.IsChecked != true) return;
        Start(async () =>
        {
            string message;
            try
            {
                await Task.Run(() => { using var registry = openRegistry(); registry.ImportConfiguration(review); });
                message = "Trabajo importado y deshabilitado. Puedes recuperar sus respaldos. Revisa el origen antes de habilitar nuevas copias.";
            }
            catch (Exception error) when (VersionJob.Expected(error))
            {
                message = error.Message + " Si el registro quedó pendiente, selecciónalo y usa Reanudar registro.";
            }
            ShowJobs();
            await LoadListAsync();
            SelectJob(review.Id);
            SetStatus(message);
        });
    }
    private void CancelImportClick(object sender, RoutedEventArgs e)
    {
        if (!busy) { importedConfiguration = null; importReview = null; ShowJobs(); SetStatus("Importación cerrada."); }
    }
    private void ReviewImportedSourceClick(object sender, RoutedEventArgs e)
    {
        if (busy || (JobList.SelectedItem as JobRow)?.Job is not { Imported: true, Enabled: false } job ||
            ChooseFolder("Elige el origen original del trabajo importado") is not { } source) return;
        Start(async () =>
        {
            await Task.Run(() => { using var registry = openRegistry(); registry.ReviewImportedSource(job.Id, source); });
            await LoadListAsync();
            SelectJob(job.Id);
            SetStatus("Origen e identidad comprobados. El trabajo continúa deshabilitado; puedes habilitarlo al guardar sus ajustes.");
        });
    }
}
