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
        foreach (var size in new[] { new Size(1040, 740), new Size(760, 620), new Size(600, 500) })
        {
            Width = size.Width; Height = size.Height;
            await Dispatcher.InvokeAsync(() => { UpdateWorkspaceLayout(); UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
            var first = JobList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            Check(first is not null && JobList.ActualHeight >= first.ActualHeight && first.ActualHeight > 0,
                $"The dashboard collapsed its job list below one complete row at {size}: list={JobList.ActualHeight}, row={first?.ActualHeight}.");
            Check(new ListBoxItemAutomationPeer(JobList.Items[0], new ListBoxAutomationPeer(JobList)).GetName() == ((JobRow)JobList.Items[0]).Label,
                "The job list exposes an implementation type instead of its accessible name.");
            Check(JobDetailsScroll.ActualHeight > 0 && JobDetailsScroll.ActualWidth > 0,
                $"The dashboard lost details at {size}: workspace={JobWorkspace.ActualWidth}x{JobWorkspace.ActualHeight}, panel={JobDetailsPanel.ActualWidth}x{JobDetailsPanel.ActualHeight}, scroll={JobDetailsScroll.ActualWidth}x{JobDetailsScroll.ActualHeight}, header={PrimaryJobActions.ActualHeight}, rows={JobWorkspace.RowDefinitions[0].ActualHeight}/{JobWorkspace.RowDefinitions[2].ActualHeight}.");
            if (size.Width >= 900)
                Check(JobList.TranslatePoint(new Point(), JobWorkspace).X < JobDetailsScroll.TranslatePoint(new Point(), JobWorkspace).X,
                    "The desktop layout did not separate jobs and details.");
            else
                Check(JobList.TranslatePoint(new Point(), JobWorkspace).Y < JobDetailsScroll.TranslatePoint(new Point(), JobWorkspace).Y,
                    "The compact layout did not stack jobs and scrollable details.");
        }
        JobList.SelectedIndex = JobList.Items.Count - 1;
        Guid selected = ((JobRow)JobList.SelectedItem).Id;
        await LoadListAsync();
        Check((JobList.SelectedItem as JobRow)?.Id == selected, "Refreshing the dashboard lost the selected job.");
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
