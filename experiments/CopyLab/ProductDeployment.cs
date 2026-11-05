using System.IO;

namespace CopyLab;

internal static class ProductDeployment
{
    internal static string Install(ProductJobRegistry registry, ProductMaintenanceReview review,
        Func<string>? install = null, Action<ProductJob, string?>? configure = null)
    {
        using var maintenance = registry.BeginMaintenance(review);
        using var scheduler = configure is null ? new WindowsSchedule() : null;
        Preflight(scheduler, registry, maintenance.Jobs);
        string executable = (install ?? (() => LabInstallation.EnsureWorker(product: true)))();
        foreach (var job in maintenance.Jobs)
        {
            bool enabled = job.Enabled && !job.Retired && job.Options.DailyAt is not null;
            if (configure is not null) configure(job, enabled ? executable : null);
            else if (enabled)
            {
                DateTime first = DateTime.Today.Add(job.Options.DailyAt!.Value.ToTimeSpan());
                if (first <= DateTime.Now) first = first.AddDays(1);
                scheduler!.RegisterProductReviewed(registry, job, executable, first);
            }
            else scheduler!.RemoveProductReviewed(registry, job);
        }
        return executable;
    }

    internal static void RemoveSchedules(ProductJobRegistry registry, ProductMaintenanceReview review,
        Action<ProductJob, string?>? configure = null)
    {
        using var maintenance = registry.BeginMaintenance(review);
        using var scheduler = configure is null ? new WindowsSchedule() : null;
        Preflight(scheduler, registry, maintenance.Jobs);
        foreach (var job in maintenance.Jobs)
        {
            if (configure is not null) configure(job, null);
            else scheduler!.RemoveProductReviewed(registry, job);
            if (job.Enabled) maintenance.Disable(job.Id);
        }
    }

    private static void Preflight(WindowsSchedule? scheduler, ProductJobRegistry registry, ProductJob[] jobs)
    {
        if (scheduler is null) return;
        foreach (var job in jobs)
            if (scheduler.ReadProductReviewed(registry, job) is { Running: true })
                throw new IOException("Hay un horario en ejecución. Espera a que termine antes de gestionar la aplicación.");
    }

    internal static void RemoveVersion(ProductJobRegistry registry, ProductMaintenanceReview review, string installations,
        InstallRemovalReview version, Action? checkTasks = null, Action<InstallRemovalStep>? checkpoint = null)
    {
        using var maintenance = registry.BeginMaintenance(review);
        if (maintenance.Jobs.Any(job => job.Enabled)) throw new InvalidDataException("Retira primero los horarios y deshabilita los trabajos antes de borrar versiones instaladas.");
        using var scheduler = checkTasks is null ? new WindowsSchedule() : null;
        if (scheduler is not null)
        {
            foreach (var job in maintenance.Jobs)
                if (scheduler.ReadProductReviewed(registry, job) is not null)
                    throw new InvalidDataException("Quedan horarios propios. Retíralos antes de borrar una versión instalada.");
            using var apps = new DirectoryFence(installations);
            scheduler.RequireNoVersionReferences(Path.Combine(installations, version.Fingerprint),
                Path.Combine(WindowsProbeNative.VolumePath(apps.Root), version.Fingerprint));
        }
        else checkTasks!();
        LabInstallation.RemoveAt(installations, version, checkpoint);
    }
}
