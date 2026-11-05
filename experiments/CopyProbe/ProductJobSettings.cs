using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

internal sealed partial class ProductJobRegistry
{
    private sealed record JobSettings(int Version, Guid Job, byte[] JobStamp, long Sequence, string Name,
        TimeOnly? DailyAt, bool Enabled, bool Retired, byte[] PreviousHash);

    internal ProductJob UpdateSettings(Guid id, string name, TimeOnly? dailyAt, bool enabled, bool retired)
    {
        using var job = OpenJob(id);
        var intent = ReadIntent(job, id);
        _ = ReadConfirmed(job, intent);
        var options = intent.Options with { Name = name, DailyAt = dailyAt };
        options.Validate();
        if (retired && enabled) throw new InvalidDataException("Un trabajo retirado no puede permanecer habilitado.");
        using var operation = WindowsProbeNative.OpenPlainFile(job, "settings.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
        var (previous, hash) = ReadSettings(job, id);
        var settings = new JobSettings(1, id, JobStamp(job), checked((previous?.Sequence ?? 0) + 1), name,
            dailyAt, enabled, retired, hash);
        using var revisions = WindowsProbeNative.OpenOrCreatePlainDirectory(job, "settings");
        RetentionJournal.WriteFile(revisions, SettingsName(settings.Sequence), Encode(settings));
        return ReadConfirmed(job, intent);
    }

    internal string RegistrationStamp(Guid id)
    {
        using var job = OpenJob(id);
        _ = ReadConfirmed(job, ReadIntent(job, id));
        return Convert.ToHexStringLower(JobStamp(job));
    }

    private static ProductJob ApplySettings(SafeFileHandle job, ProductJob definition)
    {
        var (settings, _) = ReadSettings(job, definition.Id);
        return settings is null ? definition : definition with
        {
            Options = definition.Options with { Name = settings.Name, DailyAt = settings.DailyAt },
            Enabled = settings.Enabled, Retired = settings.Retired
        };
    }

    private static (JobSettings? Settings, byte[] Hash) ReadSettings(SafeFileHandle job, Guid id)
    {
        byte[] stamp = JobStamp(job);
        if (!DirectoryPresent(job, "settings")) return (null, stamp);
        using var revisions = WindowsProbeNative.OpenPlainDirectory(job, "settings");
        JobSettings? latest = null;
        byte[] hash = stamp;
        foreach (var entry in WindowsProbeNative.ReadDirectory(revisions).Where(entry => entry.Name is not ("." or "..") &&
            !entry.Name.EndsWith(".tmp", StringComparison.Ordinal)).OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            var settings = Decode<JobSettings>(revisions, entry.Name);
            new ProductJobOptions(settings.Name, ProductBackupMode.Versions, settings.DailyAt).Validate();
            if (settings.Version != 1 || settings.Job != id || settings.Sequence != (latest?.Sequence ?? 0) + 1 ||
                entry.Name != SettingsName(settings.Sequence) || !settings.JobStamp.AsSpan().SequenceEqual(stamp) ||
                !settings.PreviousHash.AsSpan().SequenceEqual(hash) || settings.Retired && settings.Enabled)
                throw new InvalidDataException("La configuración tiene una revisión ausente, contradictoria o de otro trabajo.");
            hash = SHA256.HashData(Encode(settings));
            latest = settings;
        }
        return (latest, hash);
    }

    private static string SettingsName(long sequence) => sequence.ToString("D20", CultureInfo.InvariantCulture) + ".json";
}
