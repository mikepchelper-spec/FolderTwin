using System.Windows;

namespace CopyLab;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool smoke = e.Args is ["--smoke"];
        bool indexScale = e.Args is ["--index-scale"];
        bool scheduleSmoke = e.Args is ["--schedule-smoke"];
        bool worker = e.Args is ["--run-version-job", _, _];
        if (e.Args.Length != 0 && !smoke && !indexScale && !worker && !scheduleSmoke)
        {
            Shutdown(2);
            return;
        }

        try
        {
            if (scheduleSmoke)
            {
                await ScheduleProbe.RunAsync();
                Shutdown(0);
                return;
            }
            if (worker)
            {
                if (!Guid.TryParseExact(e.Args[2], "N", out Guid id))
                {
                    Shutdown(2);
                    return;
                }
                int result = await Task.Run(() => VersionJob.RunCommand(e.Args[1], id));
                Shutdown(result);
                return;
            }
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
            if (smoke || indexScale || worker || scheduleSmoke)
                Console.Error.WriteLine(error);
            else
                MessageBox.Show(error.Message, "FolderTwin · Laboratorio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
