using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private LabTray? productTray;

    private void ProductTrayClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        Start(async () =>
        {
            productTray ??= new LabTray(openRegistry, ShowProductWindow, () => { ShowProductWindow(); Close(); },
                notifications: testPicker is null && testWorker is null && testSchedule is null,
                openIssue: OpenTrayIssue);
            await productTray.RefreshAsync();
            Hide();
        });
    }

    private void OpenTrayIssue(Guid id)
    {
        ShowProductWindow();
        if (busy) return;
        ShowJobs();
        Start(async () =>
        {
            await LoadListAsync();
            SelectJob(id);
            await RefreshRuntimeAsync();
            RunHistoryPanel.IsExpanded = true;
            RunHistoryPanel.BringIntoView();
        });
    }

    private void ShowProductWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
