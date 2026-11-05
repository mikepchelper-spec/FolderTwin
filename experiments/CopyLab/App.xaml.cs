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
        bool productPreview = e.Args.Length == 0 || e.Args is ["--product-preview"];
        bool labPreview = e.Args is ["--lab"];
        bool startupSmoke = e.Args is ["--product-startup-smoke"];
        bool productSyntheticPreview = e.Args is ["--product-synthetic-preview"] or ["--product-synthetic-preview", "compact" or "light" or "empty"];
        bool productSmoke = e.Args is ["--product-smoke"] or ["--product-preserved-smoke"] or ["--product-surface-smoke"];
        bool productScheduleSmoke = e.Args is ["--product-schedule-smoke"];
        bool heldProductChild = e.Args is ["--held-product-child", _, _];
        bool heldRestoreChild = e.Args is ["--held-restore-child", _, _, _];
        bool heldMirrorChild = e.Args is ["--held-mirror-review-child", _, _];
        bool heldAbandonChild = e.Args is ["--held-product-abandon-child", _, _, _];
        bool trayStartup = e.Args is ["--tray-workspace", _];
        bool scheduled = e.Args is ["--run-scheduled-version-job", _, _];
        bool productScheduled = e.Args is ["--run-scheduled-product-job", _, _];
        bool productWorker = productScheduled || e.Args is ["--run-product-job", _, _] or ["--run-product-job", _, _, _] or ["--verify-product-job", _, _, _] or ["--restore-product-job", _, _, _] or ["--review-product-mirror", _, _] or ["--review-product-pending", _, _] or ["--abandon-product-version", _, _, _];
        bool worker = productWorker || scheduled || e.Args is ["--run-version-job", _, _];
        if (e.Args.Length != 0 && !smoke && !indexScale && !worker && !scheduleSmoke && !trayStartup && !productPreview && !labPreview && !startupSmoke && !productSyntheticPreview && !productSmoke && !heldProductChild && !heldRestoreChild && !heldMirrorChild && !heldAbandonChild && !productScheduleSmoke)
        {
            Shutdown(2);
            return;
        }

        try
        {
            if (startupSmoke)
            {
                await ProductStartupProbe.RunAsync();
                Shutdown(0);
                return;
            }
            if (heldAbandonChild)
            {
                if (!Guid.TryParseExact(e.Args[2], "N", out var abandonId) || !Guid.TryParseExact(e.Args[3], "N", out var reviewRun)) { Shutdown(2); return; }
                int heldResult = await Task.Run(() => VersionAbandonProbe.HeldProductChild(e.Args[1], abandonId, reviewRun));
                Shutdown(heldResult);
                return;
            }
            if (heldMirrorChild)
            {
                if (!Guid.TryParseExact(e.Args[2], "N", out var reviewId)) { Shutdown(2); return; }
                int heldResult = await Task.Run(() => ProductMirrorReviewProbe.HeldChild(e.Args[1], reviewId));
                Shutdown(heldResult);
                return;
            }
            if (heldRestoreChild)
            {
                if (!Guid.TryParseExact(e.Args[2], "N", out var restoreId) || !Guid.TryParseExact(e.Args[3], "N", out var requestId)) { Shutdown(2); return; }
                int heldResult = await Task.Run(() => ProductRestoreProbe.HeldChild(e.Args[1], restoreId, requestId));
                Shutdown(heldResult);
                return;
            }
            if (heldProductChild)
            {
                if (!Guid.TryParseExact(e.Args[2], "N", out var heldId)) { Shutdown(2); return; }
                int heldResult = await Task.Run(() => ProductRunProbe.HeldChild(e.Args[1], heldId));
                Shutdown(heldResult);
                return;
            }
            if (productScheduleSmoke)
            {
                await ProductScheduleProbe.RunAsync();
                Shutdown(0);
                return;
            }
            if (productSmoke)
            {
                if (e.Args[0] == "--product-preserved-smoke") await ProductWindow.RunPreservedJobSmokeAsync();
                else if (e.Args[0] == "--product-surface-smoke") await ProductWindow.RunSurfaceSmokeAsync();
                else await ProductWindow.RunSmokeAsync();
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
            if (productSyntheticPreview)
            {
                await ProductWindow.OpenSyntheticPreviewAsync(e.Args.Length == 2 ? e.Args[1] : null);
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
                    if (e.Args[0] is "--review-product-pending" or "--abandon-product-version")
                    {
                        Guid? reviewedRun = null;
                        if (e.Args.Length == 4)
                        {
                            if (!Guid.TryParseExact(e.Args[3], "N", out var reviewedId)) { Shutdown(2); return; }
                            reviewedRun = reviewedId;
                        }
                        int conserved = await Task.Run(() => ProductRunner.AbandonCommand(e.Args[1], id, reviewedRun));
                        Shutdown(conserved);
                        return;
                    }
                    if (e.Args[0] == "--review-product-mirror")
                    {
                        int reviewed = await Task.Run(() => ProductRunner.ReviewMirrorCommand(e.Args[1], id));
                        Shutdown(reviewed);
                        return;
                    }
                    if (e.Args[0] == "--restore-product-job")
                    {
                        if (!Guid.TryParseExact(e.Args[3], "N", out var requestId)) { Shutdown(2); return; }
                        int restored = await Task.Run(() => ProductRunner.RestoreCommand(e.Args[1], id, requestId));
                        Shutdown(restored);
                        return;
                    }
                    if (productScheduled)
                    {
                        int scheduledResult = await ProductRunner.RunScheduledAsync(e.Args[1], id, () => ProductAutomation.CanRun(e.Args[1], id));
                        Shutdown(scheduledResult);
                        return;
                    }
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
            if (smoke || indexScale || worker || scheduleSmoke || productSmoke || heldProductChild || heldRestoreChild || heldMirrorChild || heldAbandonChild || productScheduleSmoke || startupSmoke)
                Console.Error.WriteLine(error);
            else
                MessageBox.Show(error.Message, "FolderTwin", MessageBoxButton.OK, MessageBoxImage.Error);
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
