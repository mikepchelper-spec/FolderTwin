using System.Windows;

namespace CopyLab;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool smoke = e.Args is ["--smoke"];
        bool indexScale = e.Args is ["--index-scale"];
        if (e.Args.Length != 0 && !smoke && !indexScale)
        {
            Shutdown(2);
            return;
        }

        try
        {
            if (indexScale)
            {
                await Task.Run(IndexScaleProbe.Run);
                Shutdown(0);
                return;
            }
            var window = new MainWindow(smoke);
            MainWindow = window;
            if (smoke)
            {
                try
                {
                    await window.RunSmokeAsync();
                }
                finally
                {
                    window.Close();
                }
                Shutdown(0);
            }
            else
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                window.Show();
            }
        }
        catch (Exception error)
        {
            if (smoke || indexScale)
                Console.Error.WriteLine(error);
            else
                MessageBox.Show(error.Message, "FolderTwin · Laboratorio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
