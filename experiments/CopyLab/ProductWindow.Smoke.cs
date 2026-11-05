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
            var window = new ProductWindow(Open, _ => picks.Dequeue());
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
            Console.Error.WriteLine("PASS the WPF wizard reviews and registers all three modes and reloads offline jobs.");
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
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
