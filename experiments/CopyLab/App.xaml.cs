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
        bool productPreview = e.Args is ["--product-preview"];
        bool productSmoke = e.Args is ["--product-smoke"];
        bool trayStartup = e.Args is ["--tray-workspace", _];
        bool scheduled = e.Args is ["--run-scheduled-version-job", _, _];
        bool productWorker = e.Args is ["--run-product-job", _, _] or ["--run-product-job", _, _, _] or ["--verify-product-job", _, _, _];
        bool worker = productWorker || scheduled || e.Args is ["--run-version-job", _, _];
        if (e.Args.Length != 0 && !smoke && !indexScale && !worker && !scheduleSmoke && !trayStartup && !productPreview && !productSmoke)
        {
            Shutdown(2);
            return;
        }

        try
        {
            if (productSmoke)
            {
                await ProductWindow.RunSmokeAsync();
                Shutdown(0);
                return;
            }
            if (productPreview)
            {
                var product = new ProductWindow();
                MainWindow = product;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                product.Show();
                await product.InitializeAsync();
                return;
            }
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
                if (productWorker)
                {
                    if (e.Args[0] == "--verify-product-job")
                    {
                        int verified = await Task.Run(() => ProductRunner.VerifyCommand(e.Args[1], id, e.Args[3]));
                        Shutdown(verified);
                        return;
                    }
                    Guid? approval = null;
                    if (e.Args.Length == 4)
                    {
                        if (!Guid.TryParseExact(e.Args[3], "N", out Guid selected)) { Shutdown(2); return; }
                        approval = selected;
                    }
                    int productResult = await Task.Run(() => ProductRunner.RunCommand(e.Args[1], id, approval));
                    Shutdown(productResult);
                    return;
                }
                int result = scheduled ? await VersionJob.RunScheduledCommandAsync(e.Args[1], id, canRetry: MayRetry) :
                    await Task.Run(() => VersionJob.RunCommand(e.Args[1], id));
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
            if (trayStartup)
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                await window.StartTrayAsync(e.Args[1]);
                return;
            }
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
            if (smoke || indexScale || worker || scheduleSmoke || productSmoke)
                Console.Error.WriteLine(error);
            else
                MessageBox.Show(error.Message, "FolderTwin · Laboratorio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static bool MayRetry(LabWorkspace workspace, Guid id)
    {
        using var scheduler = new WindowsSchedule();
        return scheduler.Read(workspace, id) is { Enabled: true, MeetsPolicy: true } task &&
            string.Equals(task.Executable, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }
}
