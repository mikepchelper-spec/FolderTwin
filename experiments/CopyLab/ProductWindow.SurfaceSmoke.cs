using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CopyLab;

public partial class ProductWindow
{
    internal static async Task RunSurfaceSmokeAsync()
    {
        var fixture = LabWorkspace.Create();
        ProductWindow? window = null;
        try
        {
            string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
            string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
            string parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "backups")).FullName;
            string configuration = Path.Combine(fixture.Root, "job.foldertwin.json");
            ProductJobRegistry Open() => new(settings);
            ProductJob job;
            using (var registry = Open())
            {
                job = registry.Create(new("Trabajo sin puntos", ProductBackupMode.Versions, null),
                    LocalJobRoots.ReviewDedicatedDestination(source, parent));
                registry.ExportConfiguration(job.Id, configuration);
            }
            var picks = new Queue<string>();
            window = new ProductWindow(Open, _ => picks.Dequeue(), testSchedule: (_, _, _) => { },
                testConfigurationPicker: _ => configuration);
            window.Show();
            await window.InitializeAsync();
            await Click(window, window.RecoverJobButton);
            Check(window.RecoveryPanel.Visibility == Visibility.Collapsed && window.JobListPanel.IsVisible &&
                window.StatusText.Text.Length > 0, "A job without points entered ordinary recovery without an explanation.");
            using (var registry = Open())
                Check(ProductRunner.Run(registry, job.Id).State == ProductRunState.Confirmed, "Could not prepare a recovery point.");
            // Theme overrides are confined to the test fixture, as in the desktop preview.
#pragma warning disable WPF0001
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                window.ThemeMode = theme;
#pragma warning restore WPF0001
                window.Width = 600; window.Height = 500;
                await Click(window, window.NewJobButton);
                await FocusIs(window.ChooseOriginButton);
                picks.Enqueue(source); picks.Enqueue(parent);
                await Click(window, window.ChooseOriginButton);
                await Click(window, window.NextStepButton);
                await FocusIs(window.ChooseParentButton);
                await Click(window, window.ChooseParentButton);
                await Click(window, window.NextStepButton);
                await FocusIs(window.JobName);
                window.JobName.Text = "Revisión de teclado";
                window.DailyTime.Text = "29:00";
                await Click(window, window.NextStepButton);
                Check(window.StatusText.Text.Contains("HH:mm", StringComparison.Ordinal), "The wizard lost its actionable error.");
                window.DailyTime.Text = "";
                await Click(window, window.NextStepButton);
                await FocusIs(window.SaveJobButton);
                window.CancelWizardClick(window, new RoutedEventArgs());
                await FocusIs(window.NewJobButton);

                window.SelectJob(job.Id);
                window.RunHistoryPanel.IsExpanded = true;
                window.RunHistoryList.BringIntoView();
                await Click(window, window.RecoverJobButton);
                await FocusIs(window.RecoveryDates);
                Check(window.RecoveryFiles.Items.Count == 0 && !window.RestorePointButton.IsEnabled,
                    "An empty point bypassed destination review.");
                await ReturnThroughHeaderAsync();

                await Click(window, window.ManageApplicationButton);
                await FocusIs(window.AcceptApplicationUpdate);
                Check(!window.InstallApplicationButton.IsEnabled && !window.RemoveInstalledVersionButton.IsEnabled,
                    "Application management bypassed its review.");
                await ReturnThroughHeaderAsync();

                await Click(window, window.ImportConfigurationButton);
                await FocusIs(window.ChooseImportDestinationButton);
                Check(!window.SaveImportButton.IsEnabled, "An unreviewed import could be saved.");
                await ReturnThroughHeaderAsync();

                window.SelectJob(job.Id);
                window.RunHistoryList.SelectedIndex = 0;
                window.RunHistoryList.UpdateLayout();
                var historyItem = window.RunHistoryList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                    ?? throw new InvalidOperationException("No history row was generated.");
                window.RestoreKeyboardFocus(historyItem);
                await FocusIs(historyItem);
                await window.RefreshRuntimeAsync();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                Check(window.RunHistoryList.SelectedIndex == 0 &&
                    ReferenceEquals(historyItem, window.RunHistoryList.ItemContainerGenerator.ContainerFromIndex(0)),
                    "Polling an unchanged history replaced its selected keyboard row.");
                await FocusIs(historyItem);
                window.RunHistoryPanel.IsExpanded = false;
                window.JobSettingsPanel.IsExpanded = true;
                await TabFrom(window.EditJobName);
                window.ProductRetentionPanel.IsExpanded = true;
                await window.activeTask;
                await TabFrom(window.ProductRetentionDays);
                Check(!window.ApplyProductRetentionButton.IsEnabled && !window.PurgeProductRetentionButton.IsEnabled,
                    "Retention bypassed its review on an empty job.");
                window.ProductRetentionPanel.IsExpanded = window.JobSettingsPanel.IsExpanded = false;
            }
            Console.Error.WriteLine("PASS WPF light/dark, minimum size, wizard steps/error/cancel, empty recovery, visible return in recovery/application/import, unchanged history retains its keyboard row, settings/retention Tab and review gates. This does not certify a screen reader or real DPI.");

            async Task ReturnThroughHeaderAsync()
            {
                var back = window.ReturnToJobsButton;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
                var location = back.TranslatePoint(new Point(), window);
                Check(back.IsVisible && back.IsEnabled && back.ActualHeight > 0 &&
                    location.Y >= 0 && location.Y + back.ActualHeight <= 120 &&
                    location.X >= 0 && location.X + back.ActualWidth <= window.ActualWidth,
                    "The return action is not reachable in the top of this compact view.");
                window.RestoreKeyboardFocus(back);
                await FocusIs(back);
                await Click(window, back);
                await FocusIs(window.NewJobButton);
                Check(window.JobListPanel.IsVisible && !back.IsVisible && (window.JobList.SelectedItem as JobRow)?.Id == job.Id,
                    "Returning lost the selected job or left the navigation action visible.");
            }

            async Task FocusIs(Control control)
            {
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
                Check(FocusManager.GetFocusedElement(window) == control && (!window.IsActive || Keyboard.FocusedElement == control),
                    $"Navigation lost focus on {control.Name}.");
            }
            async Task TabFrom(Control control)
            {
                control.BringIntoView();
                window.RestoreKeyboardFocus(control);
                await FocusIs(control);
                Check(control.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)), $"Tab could not leave {control.Name}.");
                Check(FocusManager.GetFocusedElement(window) is Control { IsVisible: true, IsEnabled: true } next && next != control,
                    $"Tab from {control.Name} reached an unavailable control.");
            }
        }
        finally
        {
            window?.Close();
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }
    }
}
