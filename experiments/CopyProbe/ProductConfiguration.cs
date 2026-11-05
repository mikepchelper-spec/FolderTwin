using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

internal sealed record ProductConfiguration(int Version, Guid OriginalJob, ProductJobOptions Options, LocalJobRoot Source,
    DestinationBinding Destination, string[] SourceAliases);
internal sealed record ProductImportReview(ProductConfiguration Configuration, Guid Id, LocalJobRoot Source,
    LocalJobRoot Parent, DestinationBinding Target, bool SourceReviewed);

internal sealed partial class ProductJobRegistry
{
    private sealed record ConfigurationEnvelope(string Format, ProductConfiguration Job, byte[] Checksum);
    private sealed record ImportedBinding(Guid OriginalJob, DestinationBinding Target, bool SourceReviewed, string[] SourceAliases);

    internal void ExportConfiguration(Guid id, string output)
    {
        using var operation = OpenLock();
        using var job = OpenJob(id);
        var definition = ReadConfirmed(job, ReadIntent(job, id));
        var configuration = new ProductConfiguration(1, id, definition.Options, definition.Source, definition.Destination,
            KnownSourcePaths(definition.SourceAliases, definition.Source.VolumePath));
        ValidateConfiguration(configuration);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(configuration, Json);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ConfigurationEnvelope("FolderTwin job", configuration, SHA256.HashData(body)), Json);
        if (bytes.Length > MaxRecordBytes) throw new InvalidDataException("La configuración exportada excede el tamaño permitido.");
        string full = Path.GetFullPath(output);
        using var parent = new DirectoryFence(Path.GetDirectoryName(full)!);
        string name = Path.GetFileName(full);
        DirectoryFence.CheckName(name);
        string canonical = Path.Combine(WindowsProbeNative.VolumePath(parent.Root), name);
        RequireOutsideJobData(canonical);
        if (WindowsProbeNative.PlainFileExists(parent.Root, name))
            throw new InvalidDataException("El archivo ya existe. Elige un nombre nuevo para exportar la configuración.");
        using var file = WindowsProbeNative.OpenPlainFile(parent.Root, name, FileAccess.Write, FileShare.None, FileMode.CreateNew);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    internal static ProductConfiguration ReadConfiguration(string input)
    {
        string full = Path.GetFullPath(input);
        using var parent = new DirectoryFence(Path.GetDirectoryName(full)!);
        using var file = WindowsProbeNative.OpenPlainFile(parent.Root, Path.GetFileName(full), FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > MaxRecordBytes) throw new InvalidDataException("El archivo de configuración tiene un tamaño inválido.");
        byte[] bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        var envelope = JsonSerializer.Deserialize<ConfigurationEnvelope>(bytes, Json)
            ?? throw new InvalidDataException("La configuración está vacía.");
        if (envelope.Format != "FolderTwin job" || !SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope.Job, Json)).AsSpan().SequenceEqual(envelope.Checksum))
            throw new InvalidDataException("La configuración está dañada o tiene otro formato.");
        ValidateConfiguration(envelope.Job);
        return envelope.Job;
    }

    internal ProductImportReview ReviewImport(ProductConfiguration configuration, string destination, string? source = null)
    {
        ValidateConfiguration(configuration);
        var targetRoot = LocalJobRoots.InspectSelected(destination, writable: true);
        var target = Rebind(configuration.Destination, targetRoot);
        using var root = new DirectoryFence(targetRoot.VolumePath);
        using var marker = target.OpenVerifiedMarker(root.Root);
        var origin = source is null ? configuration.Source : LocalJobRoots.InspectSelected(source, writable: false);
        if (!origin.Identity.AsSpan().SequenceEqual(configuration.Source.Identity))
            throw new InvalidDataException("El origen elegido tiene otra identidad. No se habilitó ningún trabajo.");
        var parent = LocalJobRoots.InspectSelected(Path.GetDirectoryName(targetRoot.VolumePath)!, writable: true, allowVolumeRoot: true);
        var review = new ProductImportReview(configuration, Guid.NewGuid(), origin, parent, target, source is not null);
        var intent = ImportIntent(review);
        Validate(intent, review.Id);
        using var operation = OpenLock();
        RequireDisjoint(intent);
        VersionStore.InspectProductForImport(new(review.Id, configuration.Options, origin, target, false,
            Imported: true, SourceReviewed: source is not null, SourceAliases: intent.Imported!.SourceAliases));
        return review;
    }

    internal ProductJob ImportConfiguration(ProductImportReview review, Action<ProductRegistrationStep>? checkpoint = null)
    {
        ValidateConfiguration(review.Configuration);
        if (!review.Source.Identity.AsSpan().SequenceEqual(review.Configuration.Source.Identity))
            throw new InvalidDataException("La revisión de origen no coincide con la configuración importada.");
        _ = Rebind(review.Configuration.Destination, new(review.Target.LastVolumePath, review.Target.Identity));
        if (review.Target.Marker != review.Configuration.Destination.Marker || review.Target.Version != 1)
            throw new InvalidDataException("La identidad revisada del respaldo cambió.");
        var intent = ImportIntent(review);
        Validate(intent, review.Id);
        using var operation = OpenLock();
        RequireDisjoint(intent);
        using var job = WindowsProbeNative.CreatePlainDirectory(directory.Root, review.Id.ToString("N"));
        RetentionJournal.WriteFile(job, IntentName, Encode(intent));
        checkpoint?.Invoke(ProductRegistrationStep.Prepared);
        return FinishImported(job, intent, checkpoint);
    }

    private Intent ImportIntent(ProductImportReview review) => new(1, identity, review.Id, review.Configuration.Options,
        review.Source, review.Parent, new(review.Configuration.OriginalJob, review.Target, review.SourceReviewed,
            KnownSourcePaths(review.Configuration.SourceAliases, review.Configuration.Source.VolumePath, review.Source.VolumePath)));

    private ProductJob FinishImported(SafeFileHandle job, Intent intent, Action<ProductRegistrationStep>? checkpoint)
    {
        var imported = intent.Imported ?? throw new InvalidDataException("Falta la revisión de importación.");
        using var parent = new DirectoryFence(intent.Parent.VolumePath);
        if (!WindowsProbeNative.DirectoryIdentity(parent.Root).AsSpan().SequenceEqual(intent.Parent.Identity))
            throw new InvalidDataException("La carpeta padre del respaldo cambió desde la revisión.");
        using var root = new DirectoryFence(imported.Target.LastVolumePath);
        WindowsProbeNative.RequireNtfsVolume(root.Root, writable: true);
        using var marker = imported.Target.OpenVerifiedMarker(root.Root);
        if (imported.SourceReviewed)
        {
            var origin = LocalJobRoots.InspectSelected(intent.Source.VolumePath, writable: false);
            if (!origin.Identity.AsSpan().SequenceEqual(intent.Source.Identity)) throw new InvalidDataException("El origen cambió desde la revisión.");
        }
        var record = new Target(1, intent.Id, SHA256.HashData(Encode(intent)), imported.Target);
        VersionStore.InspectProductForImport(new(intent.Id, intent.Options, intent.Source, imported.Target, false,
            Imported: true, SourceReviewed: imported.SourceReviewed, SourceAliases: imported.SourceAliases));
        RetentionJournal.WriteFile(job, TargetName, Encode(record));
        checkpoint?.Invoke(ProductRegistrationStep.DestinationRecorded);
        checkpoint?.Invoke(ProductRegistrationStep.MarkerWritten);
        RetentionJournal.WriteFile(job, CommitName, Confirmation(intent, record));
        checkpoint?.Invoke(ProductRegistrationStep.Confirmed);
        return ReadConfirmed(job, intent);
    }

    private static void ValidateConfiguration(ProductConfiguration configuration)
    {
        configuration.Options.Validate();
        if (configuration.Version != 1 || configuration.OriginalJob == Guid.Empty || !ValidRoot(configuration.Source))
            throw new InvalidDataException("La configuración no contiene un trabajo compatible.");
        ValidateBinding(configuration.Destination);
        ValidateSourceAliases(configuration.SourceAliases, configuration.Source.VolumePath);
        if (LocalJobRoots.Overlap(configuration.Source.VolumePath, configuration.Destination.LastVolumePath))
            throw new InvalidDataException("La configuración contiene carpetas solapadas.");
    }

    private static void ValidateImportedBinding(ImportedBinding imported, LocalJobRoot source, LocalJobRoot parent)
    {
        ValidateBinding(imported.Target);
        ValidateSourceAliases(imported.SourceAliases, source.VolumePath);
        if (imported.OriginalJob == Guid.Empty || !Path.GetDirectoryName(imported.Target.LastVolumePath)!.Equals(parent.VolumePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("El respaldo importado no corresponde a la carpeta revisada.");
    }

    private static void ValidateBinding(DestinationBinding binding)
    {
        if (binding.Version != 1 || binding.Marker == Guid.Empty || !ValidRoot(new(binding.LastVolumePath, binding.Identity)))
            throw new InvalidDataException("La identidad del respaldo exportado no es válida.");
    }

    private static DestinationBinding Rebind(DestinationBinding expected, LocalJobRoot actual)
    {
        if (!actual.Identity.AsSpan().SequenceEqual(expected.Identity))
            throw new InvalidDataException("El respaldo elegido tiene otra identidad. Se conservó sin cambios.");
        return new() { Version = 1, Marker = expected.Marker, Identity = actual.Identity, LastVolumePath = actual.VolumePath };
    }

    private static string[] KnownSourcePaths(string[]? existing, params string[] paths) =>
        (existing ?? []).Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static void ValidateSourceAliases(string[]? paths, string required)
    {
        if (paths is null || paths.Length is < 1 or > 32 || !paths.Contains(required, StringComparer.OrdinalIgnoreCase) ||
            paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length ||
            paths.Any(path => path is null || !ValidRoot(new(path, new byte[24]))))
            throw new InvalidDataException("Las rutas conocidas del origen no son válidas.");
    }

    private void RequireOutsideJobData(string output)
    {
        if (LocalJobRoots.Overlap(output, volumePath)) throw new InvalidDataException("Exporta fuera de la configuración de FolderTwin.");
        foreach (var entry in WindowsProbeNative.ReadDirectory(directory.Root))
        {
            if (!Guid.TryParseExact(entry.Name, "N", out Guid id)) continue;
            using var job = OpenJob(id);
            var intent = ReadIntent(job, id, allowDraft: !WindowsProbeNative.PlainFileExists(job, CommitName));
            string source = WindowsProbeNative.PlainFileExists(job, CommitName) ? ReadConfirmed(job, intent).Source.VolumePath : intent.Source.VolumePath;
            if (LocalJobRoots.Overlap(output, source) || LocalJobRoots.Overlap(output, TargetPath(intent)))
                throw new InvalidDataException("Exporta fuera de los orígenes y respaldos de los trabajos.");
        }
    }
}
