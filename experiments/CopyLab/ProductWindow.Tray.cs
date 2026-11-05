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
                notifications: testPicker is null && testWorker is null && testSchedule is null);
            await productTray.RefreshAsync();
            Hide();
        });
    }

    private void ShowProductWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
