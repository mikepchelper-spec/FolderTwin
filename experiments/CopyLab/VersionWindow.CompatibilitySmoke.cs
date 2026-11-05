using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace CopyLab;

public partial class VersionWindow
{
    private static async Task RunLegacyFormatsSmokeAsync()
    {
        static void Check(bool valid, string message)
        { if (!valid) throw new InvalidOperationException(message); }
        foreach (string magic in new[] { "FTVERS01", "FTVERS02" })
        {
            var fixture = LabWorkspace.Create();
            VersionWindow? window = null;
            try
            {
                string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "origen")).FullName;
                string storePath = Directory.CreateDirectory(Path.Combine(fixture.Root, "almacén anterior")).FullName;
                string output = Directory.CreateDirectory(Path.Combine(fixture.Root, "recuperado")).FullName;
                string data = Path.Combine(source, "archivo anterior.txt");
                File.WriteAllText(data, "datos sintéticos del formato " + magic);
                byte[] hash = SHA256.HashData(File.ReadAllBytes(data));
                _ = VersionStore.Initialize(fixture, storePath);
                if (magic == "FTVERS01")
                {
                    using (var header = new FileStream(Path.Combine(storePath, ".foldertwin-version-store"), FileMode.Open, FileAccess.Write, FileShare.None))
                    { header.Write(System.Text.Encoding.ASCII.GetBytes(magic)); header.Flush(flushToDisk: true); }
                    File.Delete(Path.Combine(storePath, GenerationRegistry.PrimaryName));
                    File.Delete(Path.Combine(storePath, GenerationRegistry.ReplicaName));
                }
                var store = VersionStore.Open(fixture, storePath);
                string generation = VersionStore.NewGenerationId();
                store.CreatePoint(source, generation);
                Directory.Move(source, Path.Combine(fixture.Root, "origen ausente"));
                var selections = new Queue<string>([storePath, output]);
                window = new VersionWindow(LabWorkspace.Open(fixture.Root), _ => selections.Dequeue());
                window.OpenStoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.activeTask;
                Check(window.Timeline.Items.Count == 1 && window.RestorePointButton.IsEnabled &&
                    window.Timeline.SelectedItem is PointRow { Confirmed: { Problem: null, Id: var point } } && point == generation,
                    "The legacy format did not reopen for recovery: " + magic);
                window.VerifyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.activeTask;
                Check(window.StatusText.Text.Contains("SHA-256", StringComparison.Ordinal), window.StatusText.Text);
                window.RestorePointButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.activeTask;
                Check(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "archivo anterior.txt"))).AsSpan().SequenceEqual(hash),
                    "A legacy date restored different bytes: " + magic);
                Check(store.SupportsRetention == (magic == "FTVERS02"), "Legacy retention eligibility changed.");
            }
            finally
            {
                window?.Close();
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
        }
        Console.Error.WriteLine("PASS WPF compatibility opens FTVERS01 and FTVERS02, verifies and restores without source or an existing index; unsupported legacy retention remains disabled.");
    }
}
