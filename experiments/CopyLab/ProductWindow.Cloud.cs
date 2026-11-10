using System.IO;
using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private void CloudBackupClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var job = (JobList.SelectedItem as JobRow)?.Job;
        new ProductCloudWindow(openRegistry, job, ProductCloudWorker.ReviewedKopia,
            registry => (testMonitor ?? ProductAutomation.StartMonitor)(registry, null)) { Owner = this }.ShowDialog();
    }
}
