using System.IO;
using System.Windows;

namespace FolderTwin;

public partial class ProductWindow
{
    private void OpenExistingBackupClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        new ExistingBackupWindow(openRegistry) { Owner = this }.ShowDialog();
        RestoreKeyboardFocus(OpenExistingBackupButton);
    }

    private void CloudBackupClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var job = (JobList.SelectedItem as JobRow)?.Job;
        new ProductCloudWindow(openRegistry, job, ProductCloudWorker.ReviewedKopia,
            registry => (testMonitor ?? ProductAutomation.StartMonitor)(registry, null)) { Owner = this }.ShowDialog();
        cloudRefreshedAt = null;
    }
}
