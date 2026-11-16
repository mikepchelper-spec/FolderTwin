using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CopyLab;

public partial class ExistingBackupWindow
{
    internal static async Task RunSmokeAsync()
    {
        var fixture = LabWorkspace.Create();
        ExistingBackupWindow? window = null;
        bool passed = false;
        try
        {
            string source = Folder("source"), backups = Folder("backups"), registryPath = Folder("registry");
            string empty = Folder("empty-profile"), output = Folder("output");
            File.WriteAllText(Path.Combine(source, "sample.txt"), "first");
            ProductJob job;
            using (var registry = new ProductJobRegistry(registryPath))
            {
                job = registry.Create(new("Recovery UI", ProductBackupMode.Versions, null),
                    LocalJobRoots.ReviewDedicatedDestination(source, backups));
                var first = ProductRunner.Run(registry, job.Id);
                Check(first.State == ProductRunState.Confirmed, "Could not create the initial recovery point.");
                File.WriteAllText(Path.Combine(source, "sample.txt"), "second");
                VersionStore.OpenProduct(job).CreatePoint(job.Source.VolumePath, VersionStore.NewGenerationId());
            }
            Directory.Move(source, source + "-absent");
            Directory.Move(registryPath, registryPath + "-absent");
            window = new ExistingBackupWindow(() => new(empty), () => job.Destination.LastVolumePath, () => output);
            window.Show();
            await ClickAsync(window.OpenButton);
            Check(window.Dates.Items.Count == 2, "The standalone recovery interface lost dates.");
            window.Dates.SelectedIndex = 0;
            await WaitIdleAsync();
            Check(window.RestoreAllButton.IsEnabled && window.Files.Items.Count == 1, "The selected date did not load.");
            await ClickAsync(window.VerifyButton);
            Check(window.OperationStatus.Text.Contains("Contenido verificado", StringComparison.Ordinal), "Verification did not complete.");
            await ClickAsync(window.RestoreAllButton);
            Check(window.OperationStatus.Text.Contains("completada", StringComparison.Ordinal), window.OperationStatus.Text);
            string recovered = Directory.EnumerateDirectories(output).Single();
            Check(File.ReadAllText(Path.Combine(recovered, "sample.txt")) == "second", "The recovery interface restored different bytes.");
            using (var registry = new ProductJobRegistry(empty))
                Check(registry.List().Jobs.Length == 0, "Emergency recovery created a job.");
            passed = true;
            Console.WriteLine("PASS standalone WPF recovery listed, verified and restored with no source, previous profile or configuration export.");
        }
        finally
        {
            window?.Close();
            if (passed)
            {
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
            else Console.Error.WriteLine("Standalone WPF fixture preserved at " + fixture.Root);
        }

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
        static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
        async Task WaitIdleAsync()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                await Task.Delay(20);
                if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new IOException("Standalone recovery UI timed out.");
            } while (window!.operation is not null);
        }
        async Task ClickAsync(Button button)
        {
            Check(button.IsEnabled, "A required recovery button was disabled.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitIdleAsync();
        }
    }
}
