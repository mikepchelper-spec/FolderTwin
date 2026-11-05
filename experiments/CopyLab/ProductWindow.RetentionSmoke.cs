using System.Globalization;
using System.IO;
using System.Windows.Controls;

namespace CopyLab;

public partial class ProductWindow
{
    private static async Task RunProductRetentionSmokeAsync()
    {
        var fixture = LabWorkspace.Create();
        ProductWindow? window = null;
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
            string parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "backups")).FullName;
            string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
            ProductJobRegistry Open() => new(settings);
            ProductJob job;
            using (var registry = Open()) job = registry.Create(new("Retención de prueba", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, parent));
            var store = VersionStore.OpenProduct(job, initialize: true);
            foreach (int days in new[] { -3, -2, 0 })
            {
                File.WriteAllText(Path.Combine(source, "compartido.txt"), "contenido compartido");
                File.WriteAllText(Path.Combine(source, "cambio.txt"), days.ToString(CultureInfo.InvariantCulture));
                string generation = DateTimeOffset.UtcNow.AddDays(days).ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N");
                store.CreatePoint(job.Source.VolumePath, generation);
            }
            window = new ProductWindow(Open, testSchedule: (_, _, _) => { });
            await window.InitializeAsync();
            window.SelectJob(job.Id);
            window.ProductRetentionPanel.IsExpanded = true;
            await window.activeTask;
            window.ProductRetentionDays.Text = window.ProductRetentionGrace.Text = "1";
            window.ProductRetentionWeeks.Text = window.ProductRetentionMonths.Text = "0";
            await Click(window, window.PreviewProductRetentionButton);
            Check(window.productRetentionReview is { Expire.Length: 2, Keep.Length: 1, Objects.Length: 2 } &&
                !window.ApplyProductRetentionButton.IsEnabled, window.StatusText.Text);
            window.AcceptProductRetention.IsChecked = true;
            window.ProductRetentionGrace.Text = "2";
            Check(!window.ApplyProductRetentionButton.IsEnabled && window.productRetentionReview is null,
                "An edited policy left a previous review enabled.");
            window.ProductRetentionGrace.Text = "1";
            await Click(window, window.PreviewProductRetentionButton);
            window.AcceptProductRetention.IsChecked = true;
            await Click(window, window.ApplyProductRetentionButton);
            Check(store.ListPoints().Count(point => point.Expired) == 2 && window.UndoProductRetentionButton.IsEnabled &&
                !window.PurgeProductRetentionButton.IsEnabled, window.StatusText.Text);
            await Click(window, window.RecoverJobButton);
            window.RecoveryDates.SelectedItem = window.RecoveryDates.Items.OfType<RecoveryDate>().First(date => date.Point.Expired);
            await window.activeTask;
            window.RecoveryFiles.SelectedIndex = 0;
            Check(!window.ReviewRestoreButton.IsEnabled && !window.ReviewFileRestoreButton.IsEnabled && !window.VerifyPointButton.IsEnabled &&
                window.RecoverySummary.Text.Contains("Punto expirado", StringComparison.Ordinal),
                "An expired point offered ordinary recovery before undo.");
            await Click(window, window.ReturnToJobsButton);
            string detached = Path.Combine(fixture.Root, "source absent");
            Directory.Move(source, detached);
            await Click(window, window.UndoProductRetentionButton);
            Check(store.ListPoints().All(point => !point.Expired && !store.Verify(point.Id)), window.StatusText.Text);
            await Click(window, window.PreviewProductRetentionButton);
            window.AcceptProductRetention.IsChecked = true;
            await Click(window, window.ApplyProductRetentionButton);
            window.AcceptProductPurge.IsChecked = true;
            await Click(window, window.PurgeProductRetentionButton);
            Check(window.StatusText.Text.Contains("plazo de gracia", StringComparison.Ordinal) &&
                store.ListRetention().Count(operation => operation.State == "quarantined") == 1,
                "Product WPF bypassed grace or lost its rejected purge.");
            window.Close();
            window = new ProductWindow(Open, testSchedule: (_, _, _) => { });
            await window.InitializeAsync();
            window.SelectJob(job.Id);
            window.ProductRetentionPanel.IsExpanded = true;
            await window.activeTask;
            Check(window.UndoProductRetentionButton.IsEnabled && window.ProductRetentionChoice.Items.Count == 2,
                "The reopened product window lost its quarantine history.");
            await Click(window, window.UndoProductRetentionButton);
            Check(store.ListPoints().All(point => !point.Expired && !store.Verify(point.Id)), "Reopened undo failed.");

            // A separate, wholly synthetic old quarantine exercises actual deletion from WPF.
            string oldSource = Directory.CreateDirectory(Path.Combine(fixture.Root, "old source")).FullName;
            ProductJob oldJob;
            using (var registry = Open()) oldJob = registry.Create(new("Cuarentena antigua sintética", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(oldSource, parent));
            var oldStore = VersionStore.OpenProduct(oldJob, initialize: true);
            foreach (int days in new[] { -6, -5, -4 })
            {
                File.WriteAllText(Path.Combine(oldSource, "cambio.txt"), days.ToString(CultureInfo.InvariantCulture));
                string generation = DateTimeOffset.UtcNow.AddDays(days).ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N");
                oldStore.CreatePoint(oldJob.Source.VolumePath, generation);
            }
            DateTimeOffset oldClock = DateTimeOffset.UtcNow.AddDays(-2);
            oldStore.BeginRetention(oldStore.PreviewRetention(new(1, 0, 0, 1), oldClock), clock: oldClock);
            await window.LoadListAsync();
            window.SelectJob(oldJob.Id);
            window.ProductRetentionPanel.IsExpanded = true;
            await window.activeTask;
            window.AcceptProductPurge.IsChecked = true;
            await Click(window, window.PurgeProductRetentionButton);
            Check(oldStore.ListRetention().Single().State == "purged" &&
                oldStore.ListPoints().Where(point => !point.Expired).All(point => !oldStore.Verify(point.Id)),
                "WPF did not purge an old synthetic quarantine or damaged the retained point.");
            window.SelectJob(job.Id);
            string first = store.ListPoints()[^1].Id;
            File.WriteAllText(Path.Combine(store.Destination, "manifests", first + ".jsonl"), "damaged replica");
            await Click(window, window.PreviewProductRetentionButton);
            Check(window.productRetentionReview is null && !window.ApplyProductRetentionButton.IsEnabled &&
                store.ListPoints().All(point => !point.Expired), "A damaged catalog enabled destructive retention.");
            Console.Error.WriteLine("PASS product WPF reviews retention, preserves shared content, hides expired restore actions, undoes without source, rejects early purge, purges an old synthetic quarantine and reopens history; damaged catalogs suspend cleanup.");
        }
        finally
        {
            window?.Close();
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }
    }
}
