namespace FolderTwin;

internal static class AppWorkerHost
{
    internal static bool IsWorkerCommand(string[] args)
    {
        bool scheduled = args is ["--run-scheduled-version-job", _, _];
        bool productScheduled = args is ["--run-scheduled-product-job", _, _];
        bool productMonitor = args is ["--watch-product-jobs" or "--watch-scheduled-product-jobs" or "--supervise-product-monitor", _];
        bool cloudDispatch = args is ["--dispatch-cloud-jobs", _];
        bool cloudRecovery = args is ["--restore-cloud-point", _, _];
        bool automaticWorker = args is ["--run-automatic-product-job", _, _, ..] && args.Length <= 3 + ProductWatchSignal.MaximumEditHints;
        bool productWorker = productScheduled || args is ["--run-product-job", _, _] or ["--run-product-job", _, _, _] or
            ["--verify-product-job", _, _, _] or ["--restore-product-job", _, _, _] or
            ["--review-product-mirror", _, _] or ["--review-product-pending", _, _] or ["--abandon-product-version", _, _, _];
        bool legacyWorker = scheduled || args is ["--run-version-job", _, _];
        return productMonitor || cloudDispatch || cloudRecovery || automaticWorker || productWorker || legacyWorker;
    }

    internal static async Task<int> ExecuteWorkerAsync(string[] args)
    {
        if (args is ["--dispatch-cloud-jobs", _])
            return await Task.Run(() => ProductCloudWorker.RunAsync(args[1]));
        if (args is ["--restore-cloud-point", _, _])
            return Guid.TryParseExact(args[2], "N", out var request)
                ? await Task.Run(() => ProductCloudWorker.RecoverAsync(args[1], request)) : 2;
        if (args is ["--watch-product-jobs" or "--watch-scheduled-product-jobs" or "--supervise-product-monitor", _])
            return await Task.Run(() => args[0] == "--supervise-product-monitor"
                ? ProductMonitor.SuperviseAsync(args[1])
                : ProductMonitor.RunAsync(args[1], args[0] == "--watch-scheduled-product-jobs"));
        if (args is ["--run-automatic-product-job", _, _, ..] && args.Length <= 3 + ProductWatchSignal.MaximumEditHints)
            return Guid.TryParseExact(args[2], "N", out var autoId)
                ? await Task.Run(() => ProductRunner.RunAutomaticCommand(args[1], autoId, args.Length > 3 ? args[3..] : null)) : 1;

        if (args.Length < 3 || !Guid.TryParseExact(args[2], "N", out Guid id))
            return 2;

        bool scheduled = args is ["--run-scheduled-version-job", _, _];
        bool legacyWorker = scheduled || args is ["--run-version-job", _, _];
        if (legacyWorker)
            return scheduled
                ? await VersionJob.RunScheduledCommandAsync(args[1], id, canRetry: MayRetry)
                : await Task.Run(() => VersionJob.RunCommand(args[1], id));

        if (args[0] is "--review-product-pending" or "--abandon-product-version")
        {
            Guid? reviewedRun = null;
            if (args.Length == 4)
            {
                if (!Guid.TryParseExact(args[3], "N", out var reviewedId)) return 2;
                reviewedRun = reviewedId;
            }
            return await Task.Run(() => ProductRunner.AbandonCommand(args[1], id, reviewedRun));
        }
        if (args[0] == "--review-product-mirror")
            return await Task.Run(() => ProductRunner.ReviewMirrorCommand(args[1], id));
        if (args[0] == "--restore-product-job")
            return args.Length == 4 && Guid.TryParseExact(args[3], "N", out var requestId)
                ? await Task.Run(() => ProductRunner.RestoreCommand(args[1], id, requestId)) : 2;
        if (args[0] == "--run-scheduled-product-job")
            return await ProductRunner.RunScheduledAsync(args[1], id, () => ProductAutomation.CanRun(args[1], id));
        if (args[0] == "--verify-product-job")
            return args.Length == 4 ? await Task.Run(() => ProductRunner.VerifyCommand(args[1], id, args[3])) : 2;

        Guid? approval = null;
        if (args.Length == 4)
        {
            if (!Guid.TryParseExact(args[3], "N", out Guid selected)) return 2;
            approval = selected;
        }
        return await Task.Run(() => ProductRunner.RunCommand(args[1], id, approval));
    }

    private static bool MayRetry(LabWorkspace workspace, Guid id)
    {
        using var scheduler = new WindowsSchedule();
        return scheduler.Read(workspace, id) is { Enabled: true, MeetsPolicy: true } task &&
            string.Equals(task.Executable, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }
}
