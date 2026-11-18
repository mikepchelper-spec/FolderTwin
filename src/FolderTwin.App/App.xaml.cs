using System.Windows;

namespace FolderTwin;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool defaultUi = e.Args.Length == 0;
        bool trayStartup = e.Args is ["--tray-workspace", _];
        bool worker = AppWorkerHost.IsWorkerCommand(e.Args);
        if (!defaultUi && !trayStartup && !worker)
        {
            Shutdown(2);
            return;
        }

        try
        {
            if (worker)
            {
                Shutdown(await AppWorkerHost.ExecuteWorkerAsync(e.Args));
                return;
            }
            if (trayStartup)
            {
                var legacyWindow = new MainWindow(smoke: false);
                MainWindow = legacyWindow;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                await legacyWindow.StartTrayAsync(e.Args[1]);
                return;
            }
            var product = new ProductWindow();
            MainWindow = product;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            product.Show();
            await product.InitializeAsync();
        }
        catch (Exception error)
        {
            if (worker)
                Console.Error.WriteLine(error);
            else
                MessageBox.Show(error.Message, "FolderTwin", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
