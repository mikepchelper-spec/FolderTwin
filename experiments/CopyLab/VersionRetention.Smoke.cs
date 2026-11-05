using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CopyLab;

public partial class VersionWindow
{
    private static async Task RunRetentionSmokeAsync()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "retention source")).FullName;
            string destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "retention store")).FullName;
            var store = VersionStore.Initialize(workspace, destination);
            foreach (int days in new[] { -3, -2, 0 })
            {
                File.WriteAllText(Path.Combine(source, "shared.txt"), "shared");
                File.WriteAllText(Path.Combine(source, "change.txt"), days.ToString(CultureInfo.InvariantCulture));
                string id = DateTimeOffset.UtcNow.AddDays(days).ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) +
                    "-" + Guid.NewGuid().ToString("N");
                store.CreatePoint(source, id);
            }
            var window = new VersionWindow(workspace, _ => destination);
            try
            {
                await Click(window.OpenStoreButton, window);
                window.RetentionDays.Text = "1";
                window.RetentionWeeks.Text = window.RetentionMonths.Text = "0";
                window.RetentionGrace.Text = "1";
                await Click(window.PreviewRetentionButton, window);
                Check(window.retentionReview is { Expire.Length: 2, Keep.Length: 1, Objects.Length: 2 } &&
                    !window.ApplyRetentionButton.IsEnabled, window.StatusText.Text);
                window.AcceptRetention.IsChecked = true;
                Check(window.ApplyRetentionButton.IsEnabled, "Retention was not enabled after review.");
                await Click(window.ApplyRetentionButton, window);
                Check(window.points.Count(point => point.Confirmed?.Expired == true) == 2 && window.UndoRetentionButton.IsEnabled &&
                    !window.PurgeRetentionButton.IsEnabled, window.StatusText.Text);
                window.Timeline.SelectedItem = window.points.First(point => point.Confirmed?.Expired == true);
                await window.activeTask;
                Check(!window.RestorePointButton.IsEnabled && window.Files.Items.Count > 0,
                    "An expired point allowed ordinary restoration or hid its catalog.");
                await Click(window.UndoRetentionButton, window);
                Check(window.points.All(point => point.Confirmed?.Expired == false) && window.RestorePointButton.IsEnabled,
                    window.StatusText.Text);
                await Click(window.PreviewRetentionButton, window);
                window.AcceptRetention.IsChecked = true;
                await Click(window.ApplyRetentionButton, window);
                window.AcceptPurge.IsChecked = true;
                Check(window.PurgeRetentionButton.IsEnabled, "The explicit purge choice was ignored.");
                await Click(window.PurgeRetentionButton, window);
                Check(window.StatusText.Text.Contains("plazo de gracia", StringComparison.Ordinal) &&
                    store.ListRetention().Any(operation => operation.State == "quarantined"),
                    "The WPF flow bypassed grace or hid the rejected purge.");
            }
            finally { window.Close(); }
            var reopened = new VersionWindow(LabWorkspace.Open(workspace.Root), _ => destination);
            try
            {
                await Click(reopened.OpenStoreButton, reopened);
                Check(reopened.UndoRetentionButton.IsEnabled && reopened.RetentionChoice.Items.Count == 2,
                    "Reopening lost the quarantine history.");
                await Click(reopened.UndoRetentionButton, reopened);
                Check(store.ListPoints().All(point => !point.Expired && !store.Verify(point.Id)), "The reopened undo did not restore its points.");
            }
            finally { reopened.Close(); }
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }

        static async Task Click(Button button, VersionWindow window)
        {
            Check(button.IsEnabled, "A retention control was unexpectedly disabled.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await window.activeTask;
        }
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
