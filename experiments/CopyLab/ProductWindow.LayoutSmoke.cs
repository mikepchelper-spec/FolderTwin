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
                "The dashboard lost access to its selected job details.");
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
        Console.Error.WriteLine("PASS WPF dashboard keeps a complete job row and reachable details at desktop and minimum sizes, exposes job names to UI Automation and preserves selection on refresh.");
    }
}
