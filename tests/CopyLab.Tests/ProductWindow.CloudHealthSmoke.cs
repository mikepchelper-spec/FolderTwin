using System.IO;
using System.Windows;

namespace FolderTwin;

public partial class ProductWindow
{
    internal static async Task RunCloudHealthSmokeAsync()
    {
        var fixture = LabWorkspace.Create();
        ProductWindow? window = null;
        bool passed = false;
        try
        {
            string source = Folder("source"), backups = Folder("backups"), settings = Folder("registry");
            File.WriteAllText(Path.Combine(source, "sample.txt"), "synthetic cloud health");
            ProductJobRegistry Open() => new(settings);
            ProductJob job;
            using (var registry = Open())
            {
                job = registry.Create(new("Cloud health", ProductBackupMode.Versions, null),
                    LocalJobRoots.ReviewDedicatedDestination(source, backups));
                var result = ProductRunner.Run(registry, job.Id);
                Check(result.State == ProductRunState.Confirmed, "Cloud health fixture did not confirm.");
                string repository = new('a', 64);
                registry.SaveCloudConnection(job.Id,
                    new(repository, new(CloudProvider.CompatibleS3, "127.0.0.1:1", "us-east-1", "foldertwin")),
                    new("synthetic-password", "synthetic-key", "synthetic-secret"), lab: true);
                var ticket = registry.EnqueueCloud(job.Id, repository, result.Generation!);
                registry.RecordCloudConnectionFailure(ticket, CloudTransferIssue.Quota, retry: false);
            }
            window = new ProductWindow(Open, testSchedule: (_, _, _) => { });
            window.Show();
            await window.InitializeAsync();
            window.JobList.SelectedIndex = 0;
            await window.RefreshRuntimeAsync(rereadHistory: true);
            Check(window.RouteBackupDate.Text != "Todavía no disponible" &&
                window.RouteCloudStatus.IsVisible &&
                window.RouteCloudStatus.Text.Contains("cuota", StringComparison.Ordinal) &&
                ((JobRow)window.JobList.SelectedItem).Detail.Contains("Nube · Requiere atención", StringComparison.Ordinal),
                "Ruta did not show the quota problem alongside the confirmed local backup.");
            using (var registry = Open())
                Check(TraySnapshot.ReadProduct(registry).Attention.Contains($"{job.Id:N}-cloud-Quota"), "The tray lost the cloud warning.");
            passed = true;
            Console.WriteLine("PASS Ruta and tray preserve local confirmation and expose remote quota and backlog.");
        }
        finally
        {
            window?.Close();
            if (passed)
            {
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
            else Console.Error.WriteLine("Cloud health UI fixture preserved at " + fixture.Root);
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }
}
