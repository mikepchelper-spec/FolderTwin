using System.Windows;

namespace CopyLab;

public partial class ProductWindow
{
    private void LegacyRecoveryClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        OpenLegacyWindow().ShowDialog();
    }

    private MainWindow OpenLegacyWindow() => new(smoke: false)
    { Owner = this, Title = "FolderTwin · Compatibilidad con respaldos de alfa" };
}
