using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CopyLab;

public partial class ProductWindow
{
    internal static async Task RunSmokeAsync()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            string source = Folder("Origen sintético");
            string parent = Folder("Destinos");
            string settings = Folder("Configuración");
            File.WriteAllText(Path.Combine(source, "datos.txt"), "original");
            ProductJobRegistry Open() => new(settings);
            var picks = new Queue<string>(Enumerable.Range(0, 3).SelectMany(_ => new[] { source, parent }));
            var window = new ProductWindow(Open, _ => picks.Dequeue(), testSchedule: (_, _, _) => { });
            try
            {
                await window.InitializeAsync();
                Check(window.EmptyJobsText.Visibility == Visibility.Visible, "The empty job list was not visible.");
                foreach (var mode in Enum.GetValues<ProductBackupMode>())
                {
                    await Click(window, window.NewJobButton);
                    Check(!window.NextStepButton.IsEnabled, "The wizard advanced without a source.");
                    await Click(window, window.ChooseOriginButton);
                    await Click(window, window.NextStepButton);
                    Check(!window.NextStepButton.IsEnabled, "The wizard advanced without a destination.");
                    await Click(window, window.ChooseParentButton);
                    await Click(window, window.NextStepButton);
                    window.JobName.Text = ModeName(mode);
                    window.BackupMode.SelectedIndex = (int)mode;
                    window.DailyTime.Text = "29:00";
                    await Click(window, window.NextStepButton);
                    Check(window.step == 2 && window.StatusText.Text.Contains("HH:mm", StringComparison.Ordinal), "Invalid time passed review.");
                    window.DailyTime.Text = mode == ProductBackupMode.Versions ? "20:30" : "";
                    await Click(window, window.NextStepButton);
                    var review = window.selection?.Destination ?? throw new InvalidOperationException(window.StatusText.Text);
                    string destination = Path.Combine(parent, review.FolderName);
                    Check(window.step == 3 && window.ReviewText.Text.Contains(destination, StringComparison.Ordinal) &&
                        !Directory.Exists(destination), "The review did not disclose the new folder or it created it before saving.");
                    await Click(window, window.SaveJobButton);
                    using var registry = Open();
                    var job = registry.List().Jobs.Single(entry => entry.Id == review.FolderId);
                    Check(window.Wizard.Visibility == Visibility.Collapsed && job.Options.Mode == mode &&
                        window.JobList.SelectedItem is JobRow row && row.Id == job.Id &&
                        Directory.EnumerateFileSystemEntries(destination).Select(Path.GetFileName)
                            .SequenceEqual([DestinationBinding.MarkerName]), window.StatusText.Text);
                }

                ProductJob[] jobs;
                using (var registry = Open()) jobs = registry.List().Jobs;
                var cumulative = jobs.Single(job => job.Options.Mode == ProductBackupMode.Accumulative);
                window.SelectJob(cumulative.Id);
                await Click(window, window.BackupJobButton);
                var accumulated = VersionStore.OpenProduct(cumulative);
                Check(File.ReadAllText(Path.Combine(accumulated.MirrorCurrentPath, "datos.txt")) == "original", window.StatusText.Text);
                File.WriteAllText(Path.Combine(accumulated.MirrorCurrentPath, "exclusivo.txt"), "conservado");
                File.WriteAllText(Path.Combine(source, "datos.txt"), "actualización acumulativa");
                await Click(window, window.BackupJobButton);
                Check(File.ReadAllText(Path.Combine(accumulated.MirrorCurrentPath, "datos.txt")) == "actualización acumulativa" &&
                    File.ReadAllText(Path.Combine(accumulated.MirrorCurrentPath, "exclusivo.txt")) == "conservado", window.StatusText.Text);
                File.Delete(Path.Combine(source, "datos.txt"));
                await Click(window, window.BackupJobButton);
                Check(File.ReadAllText(Path.Combine(accumulated.MirrorCurrentPath, "datos.txt")) == "actualización acumulativa" &&
                    window.StatusText.Text.StartsWith("Respaldo completo", StringComparison.Ordinal), window.StatusText.Text);
                await Click(window, window.RecoverJobButton);
                Check(window.RecoveryDates.Items.Count == 1 && window.RecoveryFiles.Items.Count == 2,
                    "Cumulative recovery did not expose the current accumulated content.");
                await Click(window, window.VerifyPointButton);
                Check(window.StatusText.Text.StartsWith("Verificación completa", StringComparison.Ordinal), window.StatusText.Text);
                File.WriteAllText(Path.Combine(accumulated.MirrorCurrentPath, "exclusivo.txt"), "edición posterior");
                await Click(window, window.VerifyPointButton);
                Check(!window.StatusText.Text.StartsWith("Verificación completa", StringComparison.Ordinal),
                    "Current-copy verification ignored a destination edit.");
                string cumulativeOutput = Folder("Recuperación acumulativa desde WPF");
                picks.Enqueue(cumulativeOutput);
                await Click(window, window.ReviewRestoreButton);
                await Click(window, window.RestorePointButton);
                Check(File.ReadAllText(Path.Combine(Directory.EnumerateDirectories(cumulativeOutput).Single(), "exclusivo.txt")) == "conservado",
                    window.StatusText.Text);
                await Click(window, window.ReturnToJobsButton);
                File.WriteAllText(Path.Combine(source, "datos.txt"), "original");
                File.WriteAllText(Path.Combine(accumulated.MirrorCurrentPath, "datos.txt"), "edición externa");
                int confirmed = accumulated.ListPoints().Length;
                await Click(window, window.BackupJobButton);
                Check(window.StatusText.Text.Contains("datos.txt", StringComparison.Ordinal) && accumulated.ListPoints().Length == confirmed &&
                    File.ReadAllText(Path.Combine(accumulated.MirrorCurrentPath, "datos.txt")) == "edición externa",
                    "The WPF cumulative flow did not preserve and report a destination conflict.");
                var versions = jobs.Single(job => job.Options.Mode == ProductBackupMode.Versions);
                window.SelectJob(versions.Id);
                await Click(window, window.BackupJobButton);
                var store = VersionStore.OpenProduct(versions);
                Check(store.ListPoints().Length == 1 && window.StatusText.Text.StartsWith("Respaldo completo", StringComparison.Ordinal),
                    "The WPF product entry point did not create a version.");
                using (var writer = new FileStream(Path.Combine(source, "datos.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                {
                    await Click(window, window.BackupJobButton);
                    Check(!window.StatusText.Text.StartsWith("Respaldo completo", StringComparison.Ordinal) && store.ListPoints().Length == 1,
                        "A busy file was reported as a complete product backup.");
                }
                File.WriteAllText(Path.Combine(source, "datos.txt"), "actualizado");
                string documents = Directory.CreateDirectory(Path.Combine(source, "Documentos")).FullName;
                string deleted = Path.Combine("Documentos", "Recuperar.txt");
                File.WriteAllText(Path.Combine(source, deleted), "archivo eliminado recuperable");
                File.WriteAllText(Path.Combine(documents, "Informe ÁRBOL.txt"), "contenido Unicode");
                for (int number = 0; number < 301; number++)
                    File.WriteAllText(Path.Combine(source, $"archivo {number:D3}.txt"), "datos de página");
                await Click(window, window.BackupJobButton);
                Check(store.ListPoints().Length == 2, "Retry did not create the second version.");
                string beforeDeletion = store.RegisteredGenerations().RegistrationOrder[^1];
                File.Delete(Path.Combine(source, deleted));
                await Click(window, window.BackupJobButton);
                await Click(window, window.RecoverJobButton);
                Check(window.recoveryIndexKey is not null && window.RecoveryFiles.Items.Count == 250 && window.NextRecoveryPageButton.IsEnabled,
                    "The product view did not page its local index.");
                var firstPage = window.RecoveryFiles.Items.OfType<VersionEntry>().ToArray();
                await Click(window, window.NextRecoveryPageButton);
                Check(window.RecoveryFiles.Items.Count > 0 && !window.NextRecoveryPageButton.IsEnabled &&
                    !window.RecoveryFiles.Items.OfType<VersionEntry>().Intersect(firstPage).Any(), "Product pagination repeated entries.");
                await Click(window, window.PreviousRecoveryPageButton);
                window.RecoveryFiles.SelectedItem = window.RecoveryFiles.Items.OfType<VersionEntry>().Single(entry => entry.IsDirectory);
                await Click(window, window.OpenRecoveryFolderButton);
                Check(window.RecoveryFiles.Items.OfType<VersionEntry>().Any(entry => entry.Path.EndsWith("Informe ÁRBOL.txt", StringComparison.Ordinal)),
                    "The product browser did not open a nested folder.");
                window.RecoveryOnlyChanges.IsChecked = true;
                await window.activeTask;
                Check(window.RecoveryFiles.Items.OfType<VersionEntry>().Single().Change == VersionChange.Removed &&
                    window.RecoveryComparison.Text.Contains("Comparando", StringComparison.Ordinal),
                    "The change filter did not isolate the deletion and identify the compared dates.");
                window.RecoveryOnlyChanges.IsChecked = false;
                await window.activeTask;
                await Click(window, window.RecoveryParentButton);
                window.RecoverySearch.Text = "árbol";
                await Click(window, window.SearchRecoveryButton);
                Check(window.RecoveryFiles.Items.OfType<VersionEntry>().Single().Path == Path.Combine("Documentos", "Informe ÁRBOL.txt"),
                    "Unicode search did not match case-insensitively across folders.");
                window.RecoverySearch.Text = "Recuperar.txt";
                await Click(window, window.SearchRecoveryButton);
                var removed = window.RecoveryFiles.Items.OfType<VersionEntry>().Single();
                Check(removed.Change == VersionChange.Removed, "Search did not show the recoverable deleted file.");
                window.RecoveryFiles.SelectedItem = removed;
                string recoveredFileParent = Folder("Recuperación de archivo eliminado");
                picks.Enqueue(recoveredFileParent);
                await Click(window, window.ReviewFileRestoreButton);
                Check(window.restoreChoice is { RelativeFile: not null } fileChoice && fileChoice.RelativeFile == deleted &&
                    fileChoice.Generation == beforeDeletion && !Directory.EnumerateFileSystemEntries(recoveredFileParent).Any(),
                    "Deleted-file review did not select the earlier point without writing data.");
                await Click(window, window.RestorePointButton);
                string recoveredFile = Directory.EnumerateDirectories(recoveredFileParent).Single();
                Check(File.ReadAllText(Path.Combine(recoveredFile, deleted)) == "archivo eliminado recuperable" &&
                    !File.Exists(Path.Combine(recoveredFile, "datos.txt")), "Single-file recovery restored the wrong scope.");
                string database = Path.Combine(settings, VersionIndex.CacheDirectory, VersionIndex.DatabaseName);
                File.WriteAllText(database, "synthetic invalid SQLite");
                window.RecoverySearch.Text = "árbol";
                await Click(window, window.SearchRecoveryButton);
                Check(window.recoveryIndexKey is null && window.RecoveryFiles.Items.Count == 1 &&
                    window.StatusText.Text.Contains("Índice local no disponible", StringComparison.Ordinal),
                    "An unavailable index stopped catalog browsing or hid its warning.");
                window.RecoveryDates.SelectedIndex = 2;
                await window.activeTask;
                await Click(window, window.VerifyPointButton);
                Check(window.StatusText.Text.StartsWith("Verificación completa", StringComparison.Ordinal), window.StatusText.Text);
                string absent = Path.Combine(workspace.Root, "Origen apartado para recuperar");
                Directory.Move(source, absent);
                string recoveredParent = Folder("Recuperaciones de prueba");
                picks.Enqueue(recoveredParent);
                await Click(window, window.ReviewRestoreButton);
                Check(window.restoreChoice is not null && !Directory.EnumerateFileSystemEntries(recoveredParent).Any(),
                    "The restoration review wrote data before confirmation.");
                await Click(window, window.RestorePointButton);
                string output = Directory.EnumerateDirectories(recoveredParent).Single();
                Check(File.ReadAllText(Path.Combine(output, "datos.txt")) == "original" &&
                    window.StatusText.Text.StartsWith("Recuperación completa", StringComparison.Ordinal), window.StatusText.Text);
                Directory.Move(absent, source);
                await Click(window, window.ReturnToJobsButton);
                Directory.Delete(documents, recursive: true);
                for (int number = 0; number < 301; number++) File.Delete(Path.Combine(source, $"archivo {number:D3}.txt"));

                var mirror = jobs.Single(job => job.Options.Mode == ProductBackupMode.ProtectedMirror);
                window.SelectJob(mirror.Id);
                await Click(window, window.BackupJobButton);
                Check(window.mirrorReview?.Added == 1 && window.MirrorReviewPanel.Visibility == Visibility.Visible,
                    "The WPF mirror entry point did not show its review.");
                await Click(window, window.ApplyMirrorButton);
                var mirrored = VersionStore.OpenProduct(mirror);
                Check(File.ReadAllText(Path.Combine(mirrored.MirrorCurrentPath, "datos.txt")) == "actualizado", window.StatusText.Text);
                File.WriteAllText(Path.Combine(source, "datos.txt"), "tercera versión");
                await Click(window, window.BackupJobButton);
                Check(window.mirrorReview?.RequiresMassReview == true, "A mass mirror change was not reviewed.");
                await Click(window, window.ApplyMirrorButton);
                Check(File.ReadAllText(Path.Combine(mirrored.MirrorCurrentPath, "datos.txt")) == "actualizado",
                    "A mass mirror change was applied without review.");
                window.AcceptMassChanges.IsChecked = true;
                await Click(window, window.ApplyMirrorButton);
                Check(File.ReadAllText(Path.Combine(mirrored.MirrorCurrentPath, "datos.txt")) == "tercera versión", window.StatusText.Text);
                window.SelectJob(jobs.Single(job => job.Options.Mode == ProductBackupMode.Versions).Id);
                var edited = (window.JobList.SelectedItem as JobRow)!.Job!;
                window.EditJobName.Text = "Trabajo renombrado";
                window.EditDailyTime.Text = "30:00";
                await Click(window, window.SaveSettingsButton);
                Check(window.StatusText.Text.Contains("HH:mm", StringComparison.Ordinal), "Invalid edited time was accepted.");
                window.EditDailyTime.Text = "19:15";
                window.EditEnabled.IsChecked = false;
                await Click(window, window.SaveSettingsButton);
                Check(!window.BackupJobButton.IsEnabled && window.RecoverJobButton.IsEnabled &&
                    ((JobRow)window.JobList.SelectedItem).Job is { Enabled: false, Options.Name: "Trabajo renombrado" },
                    "Disabling a job lost recovery or still allowed a backup.");
                window.EditRetired.IsChecked = true;
                await Click(window, window.SaveSettingsButton);
                Check(((JobRow)window.JobList.SelectedItem).Job is { Retired: true } &&
                    VersionStore.OpenProduct(edited).ListPoints().Length > 0, "Retirement deleted the stored points.");
                window.EditRetired.IsChecked = false;
                window.EditEnabled.IsChecked = true;
                await Click(window, window.SaveSettingsButton);
                Check(window.BackupJobButton.IsEnabled && ((JobRow)window.JobList.SelectedItem).Job!.Destination.Marker == edited.Destination.Marker,
                    "Reactivation changed the job identity or failed to enable manual backup.");
                await window.RefreshScheduleAsync();
                Check(window.SpaceStatusText.Text.Contains("destino comprobado", StringComparison.Ordinal), "The dashboard did not check destination space.");
                await Click(window, window.ProductTrayButton);
                Check(window.productTray is not null && !window.IsVisible, "The product window did not create its optional tray.");
            }
            finally { window.Close(); }

            Directory.Move(source, Path.Combine(workspace.Root, "Origen apartado"));
            var reopened = new ProductWindow(Open);
            try
            {
                await reopened.InitializeAsync();
                Check(reopened.JobList.Items.Count == 3 && reopened.EmptyJobsText.Visibility == Visibility.Collapsed,
                    "The reopened window lost jobs when the source was unavailable.");
            }
            finally { reopened.Close(); }
            Console.Error.WriteLine("PASS WPF executes and recovers all three modes, preserves cumulative extras, retries busy files and reviews mass mirror changes.");
            Console.Error.WriteLine("PASS product recovery pages files, browses folders, searches Unicode, restores a deleted file and tolerates a corrupt local index.");
            Console.Error.WriteLine("PASS WPF edits name and hour, rejects invalid time, disables, retires and reactivates while keeping recovery and data identity; native schedule operations are injected separately.");
            await RunWorkerSmokeAsync();
            await ProductTrayProbe.RunAsync();
            await RunProductRetentionSmokeAsync();
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(workspace.Root, name)).FullName;
    }

    private static async Task Click(ProductWindow window, Button button)
    {
        Check(button.IsEnabled, "A wizard action was unexpectedly disabled.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.activeTask;
        if (button == window.BackupJobButton || button == window.ApplyMirrorButton || button == window.VerifyPointButton)
            await window.WaitForWorkersAsync();
    }


    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
