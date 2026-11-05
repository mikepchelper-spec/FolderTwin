using System.IO;

namespace CopyLab;

public partial class MainWindow
{
    private static async Task RunCompatibleDemonstrationSmokeAsync()
    {
        using var old = new BatchFixture();
        old.Analyze();
        if (!old.Copy().Complete) throw new InvalidOperationException("The old demonstration fixture did not complete.");
        Directory.Move(old.Source, Path.Combine(old.Root, "Origen ausente"));
        var window = new MainWindow(smoke: false);
        try
        {
            await window.OpenPreviousAsync(old.Root);
            await window.RunAsync(AttemptOperation.Verify);
            await window.RunAsync(AttemptOperation.Restore);
            if (!window.restored) throw new InvalidOperationException(window.StatusText.Text);
            window.session.CheckRestored();
            window.Close();
            window.session.CheckRestored();
            Console.Error.WriteLine("PASS compatible demonstration recovery verifies and restores without source and preserves its new restored files after the UI closes.");
        }
        finally
        {
            window.Close();
            // The fixture explicitly owns this retained recovery session.
            window.session.Dispose();
            Directory.Move(Path.Combine(old.Root, "Origen ausente"), old.Source);
        }
    }
}
