using System.Security.Cryptography;

internal sealed record ProductMaintenanceReview(string Stamp, ProductJob[] Jobs);

internal sealed partial class ProductJobRegistry
{
    internal ProductMaintenanceReview ReviewMaintenance()
    {
        using var operation = OpenLock();
        return MaintenanceSnapshot();
    }

    private ProductMaintenanceReview MaintenanceSnapshot()
    {
        var list = ReadList();
        if (list.Unreadable != 0 || list.Pending.Length != 0)
            throw new InvalidDataException("Resuelve los registros pendientes o dañados antes de gestionar la instalación.");
        var jobs = list.Jobs.OrderBy(job => job.Id).ToArray();
        return new(Convert.ToHexStringLower(SHA256.HashData(Encode(jobs))), jobs);
    }

    internal MaintenanceLease BeginMaintenance(ProductMaintenanceReview review)
    {
        var held = new List<IDisposable>();
        try
        {
            held.Add(OpenLock());
            var current = MaintenanceSnapshot();
            if (current.Stamp != review.Stamp)
                throw new InvalidDataException("La configuración cambió. Revisa de nuevo antes de gestionar la instalación.");
            foreach (var definition in current.Jobs)
            {
                var job = OpenJob(definition.Id);
                held.Add(job);
                held.Add(WindowsProbeNative.OpenPlainFile(job, "execution.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate));
                held.Add(WindowsProbeNative.OpenPlainFile(job, "scheduled.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate));
            }
            if (MaintenanceSnapshot().Stamp != review.Stamp)
                throw new InvalidDataException("Un trabajo cambió durante la revisión de instalación.");
            return new(this, current.Jobs, held);
        }
        catch { foreach (var item in held.AsEnumerable().Reverse()) item.Dispose(); throw; }
    }

    internal sealed class MaintenanceLease(ProductJobRegistry owner, ProductJob[] jobs, List<IDisposable> held) : IDisposable
    {
        internal ProductJob[] Jobs => jobs;
        internal void Disable(Guid id)
        {
            var definition = jobs.Single(job => job.Id == id);
            using var job = owner.OpenJob(id);
            _ = owner.UpdateSettingsLocked(job, id, definition.Options.Name, definition.Options.DailyAt, false, definition.Retired);
        }
        public void Dispose()
        { foreach (var item in held.AsEnumerable().Reverse()) item.Dispose(); held.Clear(); }
    }
}
