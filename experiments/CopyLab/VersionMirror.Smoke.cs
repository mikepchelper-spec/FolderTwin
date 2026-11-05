using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CopyLab;

public partial class VersionWindow
{
    private static async Task RunMirrorSmokeAsync()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            string source = Folder("origen espejo"), destination = Folder("almacén espejo"), output = Folder("recuperación espejo");
            string input = Path.Combine(source, "anterior.txt");
            File.WriteAllText(input, "contenido anterior");
            var selections = new Queue<string>([destination, source, output]);
            var window = new VersionWindow(workspace, _ => selections.Dequeue());
            try
            {
                await Click(window.NewStoreButton);
                await Click(window.CreateMirrorButton);
                await Click(window.ChooseSourceButton);
                await Click(window.ReviewMirrorButton);
                Check(window.MirrorText.Text.Contains("1 archivos nuevos", StringComparison.Ordinal) && !window.ApplyMirrorButton.IsEnabled);
                window.AcceptMirror.IsChecked = true;
                await Click(window.ApplyMirrorButton);
                var store = window.store!;
                Check(File.ReadAllText(Path.Combine(store.MirrorCurrentPath, "anterior.txt")) == "contenido anterior");
                string first = store.RegisteredGenerations().MirrorGeneration!;
                File.Delete(input);
                await Click(window.ReviewMirrorButton);
                window.AcceptMirror.IsChecked = true;
                Check(window.AcceptEmptyMirror.IsEnabled && window.AcceptMassMirror.IsEnabled && !window.ApplyMirrorButton.IsEnabled);
                window.AcceptEmptyMirror.IsChecked = true;
                window.AcceptMassMirror.IsChecked = true;
                await Click(window.ApplyMirrorButton);
                Check(!Directory.EnumerateFileSystemEntries(store.MirrorCurrentPath).Any());
                window.Timeline.SelectedItem = window.points.Single(point => point.Id == first);
                await window.activeTask;
                await Click(window.RestorePointButton);
                Check(File.ReadAllText(Path.Combine(output, "anterior.txt")) == "contenido anterior");
                File.WriteAllText(input, "después");
                var interrupted = store.PreviewMirror(source);
                try
                {
                    store.BeginMirror(interrupted, checkpoint: step =>
                    {
                        if (step == MirrorStep.Ready) throw new IOException("WPF mirror interruption.");
                    });
                    throw new InvalidOperationException("The WPF fixture did not interrupt the mirror.");
                }
                catch (IOException error) when (error.Message == "WPF mirror interruption.") { }

                async Task Click(Button button)
                {
                    Check(button.IsEnabled);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await window.activeTask;
                }
                void Check(bool condition)
                {
                    if (!condition) throw new InvalidOperationException("WPF mirror: " + window.StatusText.Text + " " + window.MirrorText.Text);
                }
            }
            finally { window.Close(); }
            Directory.Move(source, Path.Combine(workspace.Root, "origen apartado"));
            var reopened = new VersionWindow(LabWorkspace.Open(workspace.Root), _ => destination);
            try
            {
                reopened.OpenStoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await reopened.activeTask;
                if (!reopened.ResumeMirrorButton.IsEnabled || reopened.OpenMirrorFolderButton.IsEnabled)
                    throw new InvalidOperationException("WPF did not expose the pending mirror safely.");
                reopened.ResumeMirrorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await reopened.activeTask;
                if (!reopened.OpenMirrorFolderButton.IsEnabled ||
                    File.ReadAllText(Path.Combine(reopened.store!.MirrorCurrentPath, "anterior.txt")) != "después")
                    throw new InvalidOperationException("WPF did not resume the mirror. " + reopened.StatusText.Text);
            }
            finally { reopened.Close(); }
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(workspace.Root, name)).FullName;
    }
}
