using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CopyLab;

public partial class ProductWindow
{
    private async Task CheckDesktopLayoutAsync()
    {
        Show();
        foreach (var size in new[] { new Size(1040, 740), new Size(1040, 500), new Size(760, 620), new Size(600, 500) })
        {
            Width = size.Width; Height = size.Height;
            await Dispatcher.InvokeAsync(() => { UpdateWorkspaceLayout(); UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
            // Fluent animates Expander collapse; an idle dispatcher turn alone
            // can still observe its old, expanded footer height.
            if (!MoreJobActions.IsExpanded)
            {
                for (int attempt = 0; attempt < 25 && MoreJobActions.ActualHeight > 50; attempt++)
                {
                    await Task.Delay(20);
                    await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
                }
            }
            var first = JobList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            Check(first is not null && JobList.ActualHeight >= first.ActualHeight && first.ActualHeight > 0,
                $"The dashboard collapsed its job list below one complete row at {size}: list={JobList.ActualHeight}, row={first?.ActualHeight}.");
            Check(new ListBoxItemAutomationPeer(JobList.Items[0], new ListBoxAutomationPeer(JobList)).GetName() == ((JobRow)JobList.Items[0]).Label,
                "The job list exposes an implementation type instead of its accessible name.");
            Check(JobDetailsScroll.ActualHeight > 0 && JobDetailsScroll.ActualWidth > 0,
                $"The dashboard lost details at {size}: workspace={JobWorkspace.ActualWidth}x{JobWorkspace.ActualHeight}, panel={JobDetailsPanel.ActualWidth}x{JobDetailsPanel.ActualHeight}, scroll={JobDetailsScroll.ActualWidth}x{JobDetailsScroll.ActualHeight}, header={PrimaryJobActions.ActualHeight}, rows={JobWorkspace.RowDefinitions[0].ActualHeight}/{JobWorkspace.RowDefinitions[2].ActualHeight}.");
            if (size.Width >= 900 && size.Height >= 650)
            {
                Check(JobList.TranslatePoint(new Point(), JobWorkspace).X > JobDetailsScroll.TranslatePoint(new Point(), JobWorkspace).X,
                    "The desktop layout did not put the job list to the right of the route.");
                Check(BackupRoute.IsVisible && BackupRoute.ActualHeight > 0 && !BackupRoute.IsDescendantOf(JobDetailsScroll),
                    "The route scrolls out of view in the desktop layout.");
            }
            else
            {
                Console.Error.WriteLine($"Compact metrics {size}: window={ActualHeight:F1}, rows={string.Join('/', ((Grid)Content).RowDefinitions.Select(row => row.ActualHeight.ToString("F1")))}, heading={RouteHeading.ActualHeight:F1}, workspace={JobWorkspace.ActualHeight:F1}, workspaceRows={string.Join('/', JobWorkspace.RowDefinitions.Select(row => row.ActualHeight.ToString("F1")))}, actions={PrimaryJobActions.ActualHeight:F1}, details={JobDetailsPanel.ActualHeight:F1}, secondary={MoreJobActions.ActualHeight:F1}.");
                Check(JobList.TranslatePoint(new Point(), JobWorkspace).Y < JobDetailsScroll.TranslatePoint(new Point(), JobWorkspace).Y,
                    "The compact layout did not stack jobs and scrollable details.");
                Check(MoreJobActions.IsExpanded || JobDetailsScroll.ActualHeight >= 96,
                    $"Compact details provide less than 96 device-independent pixels at {size}: {JobDetailsScroll.ActualHeight}; operation status={OperationStatusText.ActualHeight}; status={StatusText.ActualHeight}.");
                Console.Error.WriteLine($"Compact layout {size}: details viewport {JobDetailsScroll.ActualHeight:F1} DIP, secondary actions expanded={MoreJobActions.IsExpanded}.");
            }
        }
        JobList.SelectedIndex = JobList.Items.Count - 1;
        Guid selected = ((JobRow)JobList.SelectedItem).Id;
        await LoadListAsync();
        Check((JobList.SelectedItem as JobRow)?.Id == selected, "Refreshing the dashboard lost the selected job.");
        var job = ((JobRow)JobList.SelectedItem).Job!;
        await RefreshScheduleAsync();
        Check(BackupRoute.IsVisible && SelectedJobTitle.Text == $"{job.Options.Name} · {ModeName(job.Options.Mode)}" &&
            Equals(RouteSource.ToolTip, DisplayRoutePath(job.Source.VolumePath)) &&
            Equals(RouteDestination.ToolTip, DisplayRoutePath(job.Destination.LastVolumePath)) &&
            RouteSource.Text != RouteDestination.Text, "The route did not follow the selected job.");
        await Click(this, RecoverJobButton);
        foreach (var size in new[] { new Size(1040, 740), new Size(600, 500) })
        {
            Width = size.Width;
            Height = size.Height;
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
            var start = RecoveryActions.TranslatePoint(new Point(), RecoveryPanel);
            Check(RecoveryActions.ActualHeight > 0 && start.Y >= 0 &&
                start.Y + RecoveryActions.ActualHeight <= RecoveryPanel.ActualHeight + 0.5 && VerifyPointButton.IsEnabled,
                $"Recovery actions are not reachable without scrolling at {size}.");
        }
        await Click(this, ReturnToJobsButton);
        var compatible = OpenLegacyWindow();
        try
        {
            Check(compatible.Owner == this && compatible.Title.Contains("Compatibilidad", StringComparison.Ordinal),
                "The product dashboard cannot open the isolated legacy recovery tools.");
        }
        finally { compatible.Close(); }
        Console.Error.WriteLine("PASS WPF dashboard keeps a complete job row and reachable details, recovery actions stay visible at desktop and minimum sizes, job names are accessible and selection survives refresh.");
    }
}
