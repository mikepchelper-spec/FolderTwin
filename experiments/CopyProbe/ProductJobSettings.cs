using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal sealed partial class ProductJobRegistry
{
    private sealed record JobSettings(int Version, Guid Job, byte[] JobStamp, long Sequence, string Name,
        TimeOnly? DailyAt, bool Enabled, bool Retired, byte[] PreviousHash,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LocalJobRoot? ReviewedSource = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? SourceAliases = null);

    internal ProductJob UpdateSettings(Guid id, string name, TimeOnly? dailyAt, bool enabled, bool retired)
    {
        using var job = OpenJob(id);
        var intent = ReadIntent(job, id);
        var definition = ReadConfirmed(job, intent);
        var options = intent.Options with { Name = name, DailyAt = dailyAt };
        options.Validate();
        if (retired && enabled) throw new InvalidDataException("Un trabajo retirado no puede permanecer habilitado.");
        if (enabled && definition.Imported)
        {
            if (!definition.SourceReviewed) throw new InvalidDataException("Revisa el origen importado antes de habilitar el trabajo.");
            using var roots = LocalJobRoots.Reopen(new(definition.Source, new(definition.Destination.LastVolumePath, definition.Destination.Identity), 0, false));
            using var marker = definition.Destination.OpenVerifiedMarker(roots.DestinationHandle);
        }
        using var operation = WindowsProbeNative.OpenPlainFile(job, "settings.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
        var (previous, hash) = ReadSettings(job, id);
        var settings = new JobSettings(1, id, JobStamp(job), checked((previous?.Sequence ?? 0) + 1), name,
            dailyAt, enabled, retired, hash, previous?.ReviewedSource, previous?.SourceAliases);
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
            Enabled = settings.Enabled, Retired = settings.Retired,
            Source = settings.ReviewedSource ?? definition.Source,
            SourceAliases = settings.SourceAliases ?? definition.SourceAliases,
            SourceReviewed = settings.ReviewedSource is not null || definition.SourceReviewed
        };
    }

    private static (JobSettings? Settings, byte[] Hash) ReadSettings(SafeFileHandle job, Guid id)
    {
        byte[] stamp = JobStamp(job);
        var baseline = Decode<Intent>(job, IntentName);
        if (!DirectoryPresent(job, "settings")) return (null, stamp);
        using var revisions = WindowsProbeNative.OpenPlainDirectory(job, "settings");
        JobSettings? latest = null;
        byte[] hash = stamp;
        foreach (var entry in WindowsProbeNative.ReadDirectory(revisions).Where(entry => entry.Name is not ("." or "..") &&
            !entry.Name.EndsWith(".tmp", StringComparison.Ordinal)).OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            var settings = Decode<JobSettings>(revisions, entry.Name);
            new ProductJobOptions(settings.Name, ProductBackupMode.Versions, settings.DailyAt).Validate();
            if (settings.ReviewedSource is { } source)
            {
                if (baseline.Imported is null || !ValidRoot(source) || !source.Identity.AsSpan().SequenceEqual(baseline.Source.Identity))
                    throw new InvalidDataException("La revisión del origen no corresponde al trabajo importado.");
                ValidateSourceAliases(settings.SourceAliases, baseline.Source.VolumePath);
                if (latest?.SourceAliases is { } known && known.Any(path => !settings.SourceAliases!.Contains(path, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Una revisión perdió rutas de origen ya reconocidas.");
            }
            else if (settings.SourceAliases is not null) throw new InvalidDataException("Las rutas de origen carecen de revisión.");
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

    internal ProductJob ReviewImportedSource(Guid id, string sourcePath)
    {
        using var registryLock = OpenLock();
        using var job = OpenJob(id);
        var intent = ReadIntent(job, id);
        var definition = ReadConfirmed(job, intent);
        if (!definition.Imported || definition.Enabled)
            throw new InvalidDataException("Deshabilita el trabajo importado antes de revisar su origen.");
        using var execution = WindowsProbeNative.OpenPlainFile(job, "execution.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
        using var scheduled = WindowsProbeNative.OpenPlainFile(job, "scheduled.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
        var source = LocalJobRoots.InspectSelected(sourcePath, writable: false);
        if (!source.Identity.AsSpan().SequenceEqual(intent.Source.Identity))
            throw new InvalidDataException("El origen elegido tiene otra identidad. Se conservó la configuración anterior.");
        var aliases = KnownSourcePaths(definition.SourceAliases, intent.Source.VolumePath, definition.Source.VolumePath, source.VolumePath);
        var candidate = intent with { Source = source, Imported = intent.Imported! with { SourceAliases = aliases } };
        Validate(candidate, id);
        RequireDisjoint(candidate);
        using var roots = LocalJobRoots.Reopen(new(source, new(definition.Destination.LastVolumePath, definition.Destination.Identity), 0, false));
        using var marker = definition.Destination.OpenVerifiedMarker(roots.DestinationHandle);
        using var settingsLock = WindowsProbeNative.OpenPlainFile(job, "settings.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
        var (previous, hash) = ReadSettings(job, id);
        var settings = new JobSettings(1, id, JobStamp(job), checked((previous?.Sequence ?? 0) + 1), definition.Options.Name,
            definition.Options.DailyAt, false, definition.Retired, hash, source, aliases);
        using var revisions = WindowsProbeNative.OpenOrCreatePlainDirectory(job, "settings");
        RetentionJournal.WriteFile(revisions, SettingsName(settings.Sequence), Encode(settings));
        return ReadConfirmed(job, intent);
    }
}
