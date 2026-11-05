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
                Check(reopened.indexKey is not null && reopened.browser is null, reopened.StatusText.Text);
                reopened.Files.SelectedItem = reopened.Files.Items.Cast<VersionEntry>().Single(entry => entry.IsDirectory);
                Click(reopened.OpenDirectoryButton);
                await reopened.activeTask;
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
                string indexPath = Path.Combine(workspace.Root, VersionIndex.CacheDirectory, VersionIndex.DatabaseName);
                File.Delete(indexPath);
                Click(reopened.RefreshButton);
                await reopened.activeTask;
                Check(reopened.indexKey is not null && File.Exists(indexPath) &&
                    reopened.Files.Items.Cast<VersionEntry>().Single().Path == "Carpeta ñ",
                    "The missing index was not rebuilt. " + reopened.StatusText.Text);
                const string brokenIndex = "índice sintético dañado";
                File.WriteAllText(indexPath, brokenIndex);
                Click(reopened.RefreshButton);
                await reopened.activeTask;
                Check(reopened.indexKey is null && reopened.browser is not null &&
                    reopened.StatusText.Text.Contains("Índice local no disponible", StringComparison.Ordinal) &&
                    File.ReadAllText(indexPath) == brokenIndex,
                    "The damaged index was changed or bypassed without notice.");
                reopened.Files.SelectedItem = reopened.Files.Items.Cast<VersionEntry>().Single();
                Click(reopened.OpenDirectoryButton);
                await reopened.activeTask;
                Check(reopened.Files.Items.Cast<VersionEntry>().SequenceEqual(changes),
                    "The direct catalog view differed from the indexed view.");
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
        await RunBackgroundSmokeAsync();
        await RunRetentionSmokeAsync();
        await RunMirrorSmokeAsync();
        await RunLegacyFormatsSmokeAsync();

        string NewFolder(string name) => Directory.CreateDirectory(Path.Combine(workspace.Root, name)).FullName;
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
    }

    private static async Task RunBackgroundSmokeAsync()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "origen trabajador")).FullName;
            string destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "almacén trabajador")).FullName;
            byte[] bytes = new byte[8 * 1024 * 1024 + 17];
            new Random(42).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(source, "contenido.bin"), bytes);
            var selections = new Queue<string>([destination, source]);
            var window = new VersionWindow(workspace, _ => selections.Dequeue());
            Guid id;
            try
            {
                window.NewStoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.activeTask;
                window.ChooseSourceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.BackgroundButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.activeTask;
                id = window.backgroundJob ?? throw new InvalidOperationException("The background job was not created.");
            }
            finally { window.Close(); }
            var timeout = DateTime.UtcNow.AddSeconds(30);
            VersionJobResult? result = null;
            while (DateTime.UtcNow < timeout)
            {
                result = VersionJob.History(workspace, id).Results.FirstOrDefault();
                if (result is not null)
                    break;
                await Task.Delay(100);
            }
            if (result is not { State: VersionJobState.Confirmed })
                throw new InvalidOperationException("The worker did not confirm its point after the window closed.");
            var store = VersionStore.Open(workspace, destination);
            if (store.ListPoints().Single().Id != result.Generation || store.Verify(result.Generation))
                throw new InvalidOperationException("The background point did not verify.");
            var reopened = new VersionWindow(LabWorkspace.Open(workspace.Root), _ => destination);
            try
            {
                reopened.OpenStoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await reopened.activeTask;
                if (!reopened.JobsText.Text.Contains("punto confirmado", StringComparison.Ordinal))
                    throw new InvalidOperationException("The reopened window hid its background job result.");
                string moved = Path.Combine(workspace.Root, "origen apartado");
                Directory.Move(source, moved);
                if (VersionJob.Run(workspace, id).State != VersionJobState.WaitingForSource)
                    throw new InvalidOperationException("The missing-source worker fixture was not observed.");
                reopened.RefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await reopened.activeTask;
                if (!reopened.JobsText.Text.Contains("origen ausente", StringComparison.Ordinal) ||
                    !reopened.JobsText.Text.Contains("pendiente de resolver", StringComparison.Ordinal) ||
                    !reopened.RestorePointButton.IsEnabled)
                    throw new InvalidOperationException("A failed job was hidden or blocked an independently confirmed point.");
            }
            finally { reopened.Close(); }
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }
    }
}
