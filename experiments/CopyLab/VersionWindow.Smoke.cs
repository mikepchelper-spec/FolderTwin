using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CopyLab;

public partial class VersionWindow
{
    internal static async Task RunSmokeAsync()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            string source = NewFolder("entrada");
            string storePath = NewFolder("almacén");
            string outputFile = NewFolder("archivo recuperado");
            string outputPoint = NewFolder("punto recuperado");
            string nested = Directory.CreateDirectory(Path.Combine(source, "Carpeta ñ")).FullName;
            string changed = Path.Combine(nested, "informe.txt");
            string removed = Path.Combine(nested, "borrado.txt");
            File.WriteAllText(changed, "versión uno");
            File.WriteAllText(removed, "contenido eliminado");
            File.WriteAllText(Path.Combine(nested, "estable.txt"), "sin cambios");
            string first;
            var selections = new Queue<string>([storePath, source, storePath, outputFile, outputPoint]);
            var initial = new VersionWindow(workspace, _ => selections.Dequeue());
            try
            {
                Click(initial.NewStoreButton);
                await initial.activeTask;
                Click(initial.ChooseSourceButton);
                Click(initial.CreatePointButton);
                await initial.activeTask;
                Check(initial.Timeline.Items.Count == 1 && initial.RestorePointButton.IsEnabled &&
                    initial.Timeline.SelectedItem is PointRow { Confirmed: not null }, initial.StatusText.Text);
                first = ((PointRow)initial.Timeline.SelectedItem).Id;
            }
            finally { initial.Close(); }

            File.WriteAllText(changed, "versión dos");
            File.Delete(removed);
            File.WriteAllText(Path.Combine(nested, "nuevo.txt"), "nuevo");
            var store = VersionStore.Open(workspace, storePath);
            string second = VersionStore.NewGenerationId();
            try
            {
                store.CreatePoint(source, second, step =>
                {
                    if (step == CatalogStep.PrimaryPublished)
                        throw new IOException("Injected WPF resume fixture.");
                });
                throw new InvalidOperationException("The WPF fixture did not interrupt publication.");
            }
            catch (IOException error) when (error.Message == "Injected WPF resume fixture.") { }
            Directory.Move(source, Path.Combine(workspace.Root, "origen ausente"));
            var reopened = new VersionWindow(LabWorkspace.Open(workspace.Root), _ => selections.Dequeue());
            try
            {
                Click(reopened.OpenStoreButton);
                await reopened.activeTask;
                Check(reopened.Timeline.Items.Count == 2 && reopened.ResumeButton.IsEnabled && !reopened.RestorePointButton.IsEnabled,
                    "A pending point was hidden or allowed restoration.");
                Click(reopened.ResumeButton);
                await reopened.activeTask;
                Check(reopened.Timeline.SelectedItem is PointRow { Confirmed: not null, Id: var id } && id == second &&
                    reopened.OnlyChanges.IsEnabled && reopened.RestorePointButton.IsEnabled, reopened.StatusText.Text);
                reopened.OnlyChanges.IsChecked = true;
                await reopened.activeTask;
                reopened.Files.SelectedItem = reopened.Files.Items.Cast<VersionEntry>().Single(entry => entry.IsDirectory);
                Click(reopened.OpenDirectoryButton);
                var changes = reopened.Files.Items.Cast<VersionEntry>().ToArray();
                Check(changes.Length == 3 && changes.Any(entry => entry.Change == VersionChange.Added) &&
                    changes.Any(entry => entry.Change == VersionChange.Modified) &&
                    changes.Any(entry => entry.Change == VersionChange.Removed),
                    "The folder comparison did not isolate additions, edits and removals.");
                reopened.Files.SelectedItem = changes.Single(entry => entry.Change == VersionChange.Removed);
                Click(reopened.RestoreFileButton);
                await reopened.activeTask;
                Check(File.ReadAllText(Path.Combine(outputFile, "Carpeta ñ", "borrado.txt")) == "contenido eliminado" &&
                    !File.Exists(Path.Combine(outputFile, "Carpeta ñ", "informe.txt")), "The deleted file did not restore independently.");
                Click(reopened.VerifyButton);
                await reopened.activeTask;
                Check(reopened.StatusText.Text.Contains("SHA-256", StringComparison.Ordinal), reopened.StatusText.Text);
                Click(reopened.RestorePointButton);
                await reopened.activeTask;
                Check(File.ReadAllText(Path.Combine(outputPoint, "Carpeta ñ", "informe.txt")) == "versión dos" &&
                    !File.Exists(Path.Combine(outputPoint, "Carpeta ñ", "borrado.txt")), "The selected date was not restored.");

                File.WriteAllText(Path.Combine(storePath, "manifests", first + ".jsonl"), "truncado");
                File.WriteAllText(Path.Combine(storePath, "manifests_backup", first + ".jsonl"), "truncado");
                Click(reopened.RefreshButton);
                await reopened.activeTask;
                var damaged = reopened.Timeline.Items.Cast<PointRow>().Single(row => row.Id == first);
                Check(damaged.Confirmed?.Problem is not null && reopened.RestorePointButton.IsEnabled &&
                    !reopened.OnlyChanges.IsEnabled, "A damaged date hid or blocked an independent healthy date.");
                reopened.Timeline.SelectedItem = damaged;
                await reopened.activeTask;
                Check(!reopened.RestorePointButton.IsEnabled && reopened.StatusText.Text.Contains("requiere revisión", StringComparison.Ordinal),
                    "A damaged date was offered for restoration.");
            }
            finally { reopened.Close(); }
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true); // The test owns every entry in this workspace.
        }

        string NewFolder(string name) => Directory.CreateDirectory(Path.Combine(workspace.Root, name)).FullName;
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }
}
