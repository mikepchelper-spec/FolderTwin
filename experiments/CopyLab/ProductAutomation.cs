using System.IO;
using System.Runtime.InteropServices;

namespace CopyLab;

internal static class ProductAutomation
{
    internal static void Apply(string location, Guid id)
    {
        using var registry = new ProductJobRegistry(location);
        var job = registry.Get(id);
        using var scheduler = new WindowsSchedule();
        if (!job.Enabled || job.Retired || job.Options.DailyAt is not { } hour)
        {
            scheduler.RemoveProduct(registry, id);
            return;
        }
        string executable = LabInstallation.EnsureWorker(product: true);
        DateTime first = DateTime.Today.Add(hour.ToTimeSpan());
        if (first <= DateTime.Now) first = first.AddDays(1);
        scheduler.RegisterProduct(registry, id, executable, first);
    }

    internal static bool CanRun(string location, Guid id)
    {
        using var registry = new ProductJobRegistry(location);
        var job = registry.Get(id);
        if (!job.Enabled || job.Retired || job.Options.DailyAt is null) return false;
        using var scheduler = new WindowsSchedule();
        return scheduler.ReadProduct(registry, id) is { Enabled: true, MeetsPolicy: true } task &&
            string.Equals(task.Executable, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Remove(string location, Guid id)
    {
        using var registry = new ProductJobRegistry(location);
        using var scheduler = new WindowsSchedule();
        scheduler.RemoveProduct(registry, id);
    }

    internal static bool Expected(Exception error) => VersionJob.Expected(error) || error is COMException;
}
