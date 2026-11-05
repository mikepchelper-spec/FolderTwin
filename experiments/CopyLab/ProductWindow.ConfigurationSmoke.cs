using System.IO;

namespace CopyLab;

public partial class ProductWindow
{
    private static async Task RunConfigurationSmokeAsync()
    {
        var fixture = LabWorkspace.Create();
        ProductWindow? window = null;
        try
        {
            string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
            string original = Folder("original settings"), imported = Folder("imported settings"), source = Folder("source");
            string parent = Folder("backups"), exports = Folder("exports"), wrong = Folder("wrong source");
            File.WriteAllText(Path.Combine(source, "datos.txt"), "datos de configuración WPF");
            var jobs = new List<ProductJob>();
            var files = new Queue<string>();
            ProductJobRegistry Original() => new(original);
            ProductJobRegistry Imported() => new(imported);
            using (var registry = Original())
            {
                foreach (var mode in Enum.GetValues<ProductBackupMode>())
                {
                    var job = registry.Create(new(ModeName(mode), mode, new TimeOnly(20, 30)), LocalJobRoots.ReviewDedicatedDestination(source, parent));
                    Check(ProductRunner.Run(registry, job.Id).State == ProductRunState.Confirmed, "Configuration UI fixture failed.");
                    jobs.Add(job);
                    files.Enqueue(Path.Combine(exports, mode + ".foldertwin.json"));
                }
            }
            string[] exportedFiles = files.ToArray();
            window = new ProductWindow(Original, testSchedule: (_, _, _) => { }, testConfigurationPicker: save =>
            {
                Check(save, "Export attempted to import a file.");
                return files.Dequeue();
            });
            await window.InitializeAsync();
            foreach (var job in jobs)
            {
                window.SelectJob(job.Id);
                await Click(window, window.ExportConfigurationButton);
                Check(window.StatusText.Text.StartsWith("Configuración exportada", StringComparison.Ordinal), window.StatusText.Text);
            }
            window.Close();
            string moved = Path.Combine(fixture.Root, "source renamed");
            Directory.Move(source, moved);
            var folders = new Queue<string>();
            int configured = 0;
            files = new Queue<string>(exportedFiles);
            window = new ProductWindow(Imported, _ => folders.Dequeue(), testSchedule: (_, _, _) => configured++,
                testConfigurationPicker: save =>
                {
                    Check(!save, "Import attempted to save a configuration file.");
                    return files.Dequeue();
                });
            await window.InitializeAsync();
            foreach (var job in jobs)
            {
                int beforeSchedule = configured;
                await Click(window, window.ImportConfigurationButton);
                Check(window.ImportPanel.Visibility == System.Windows.Visibility.Visible && !window.ReviewImportButton.IsEnabled,
                    "Import skipped destination selection.");
                folders.Enqueue(job.Destination.LastVolumePath);
                await Click(window, window.ChooseImportDestinationButton);
                if (job.Options.Mode != ProductBackupMode.Accumulative)
                {
                    folders.Enqueue(moved);
                    await Click(window, window.ChooseImportSourceButton);
                }
                await Click(window, window.ReviewImportButton);
                Check(window.importReview is not null && !window.SaveImportButton.IsEnabled, window.StatusText.Text);
                window.AcceptImport.IsChecked = true;
                await Click(window, window.SaveImportButton);
                var row = (window.JobList.SelectedItem as JobRow)!;
                Check(row.Job is { Enabled: false, Imported: true } && !window.BackupJobButton.IsEnabled &&
                    window.RecoverJobButton.IsEnabled && configured == beforeSchedule,
                    "Import enabled a job, installed a schedule or lost recovery.");
                await Click(window, window.RecoverJobButton);
                Check(window.RecoveryDates.Items.Count == 1, "Import lost its original recovery point.");
                string output = Folder("recovery " + job.Options.Mode);
                folders.Enqueue(output);
                await Click(window, window.ReviewRestoreButton);
                await Click(window, window.RestorePointButton);
                Check(File.ReadAllText(Path.Combine(Directory.EnumerateDirectories(output).Single(), "datos.txt")) ==
                    "datos de configuración WPF", window.StatusText.Text);
                await Click(window, window.ReturnToJobsButton);
                if (job.Options.Mode == ProductBackupMode.Accumulative)
                {
                    window.EditEnabled.IsChecked = true;
                    await Click(window, window.SaveSettingsButton);
                    Check(window.StatusText.Text.Contains("Revisa el origen importado", StringComparison.Ordinal) && !window.BackupJobButton.IsEnabled,
                        "Import without source was enabled before review.");
                    folders.Enqueue(wrong);
                    await Click(window, window.ReviewImportedSourceButton);
                    Check(window.StatusText.Text.Contains("otra identidad", StringComparison.Ordinal), "A replacement source passed UI review.");
                    folders.Enqueue(moved);
                    await Click(window, window.ReviewImportedSourceButton);
                }
                window.EditEnabled.IsChecked = true;
                await Click(window, window.SaveSettingsButton);
                Check(window.BackupJobButton.IsEnabled && configured == beforeSchedule + 1,
                    "Explicit activation did not configure the reviewed imported job.");
                await Click(window, window.BackupJobButton);
                if (job.Options.Mode == ProductBackupMode.ProtectedMirror) await Click(window, window.ApplyMirrorButton);
                using var registry = Imported();
                Check(registry.RunHistory(row.Id).Runs[0].Result?.State == ProductRunState.Confirmed,
                    "An activated imported job did not complete through WPF.");
            }
            Check(files.Count == 0 && folders.Count == 0, "The configuration flow skipped an expected picker.");
            Console.Error.WriteLine("PASS WPF exports and reviews all three imported modes, preserves old recovery points, restores without the original source, refuses substituted sources and enables schedules only after explicit activation.");
        }
        finally
        {
            window?.Close();
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }
    }
}
