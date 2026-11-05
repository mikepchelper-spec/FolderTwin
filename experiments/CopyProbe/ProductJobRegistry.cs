using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal enum ProductBackupMode { Accumulative, Versions, ProtectedMirror }
internal enum ProductRegistrationStep { Prepared, DestinationRecorded, MarkerWritten, Confirmed }
internal sealed record ProductJobOptions(string Name, ProductBackupMode Mode, TimeOnly? DailyAt)
{
    internal void Validate()
    {
        if (Name.Length is < 1 or > 100 || string.IsNullOrWhiteSpace(Name) || Name.Any(char.IsControl) || !Enum.IsDefined(Mode))
            throw new InvalidDataException("El trabajo necesita un nombre de hasta 100 caracteres y un modo válido.");
    }
}
internal sealed record ProductJob(Guid Id, ProductJobOptions Options, LocalJobRoot Source, DestinationBinding Destination,
    bool Enabled = true, bool Retired = false);
internal sealed record ProductJobList(ProductJob[] Jobs, Guid[] Pending, int Unreadable);

internal sealed partial class ProductJobRegistry : IDisposable
{
    private const string IntentName = "intent.json";
    private const string TargetName = "target.json";
    private const string CommitName = "confirmed";
    private const int MaxRecordBytes = 64 * 1024;
    private readonly DirectoryFence directory;
    private readonly byte[] identity;
    private readonly string volumePath;
    internal string Location => volumePath;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 12
    };
    private sealed record Intent(int Version, byte[] RegistryIdentity, Guid Id, ProductJobOptions Options,
        LocalJobRoot Source, LocalJobRoot Parent);
    private sealed record Target(int Version, Guid Id, byte[] IntentHash, DestinationBinding Binding);

    internal ProductJobRegistry(string path)
    {
        directory = new DirectoryFence(path);
        try
        {
            WindowsProbeNative.RequireNtfsVolume(directory.Root, writable: true);
            volumePath = WindowsProbeNative.VolumePath(directory.Root);
            identity = WindowsProbeNative.DirectoryIdentity(directory.Root);
        }
        catch { directory.Dispose(); throw; }
    }

    internal static ProductJobRegistry OpenLocal()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        using var baseDirectory = new DirectoryFence(local);
        if (new DriveInfo(Path.GetPathRoot(local)!).DriveType != DriveType.Fixed)
            throw new InvalidDataException("La configuración requiere almacenamiento fijo local.");
        WindowsProbeNative.RequireNtfsVolume(baseDirectory.Root, writable: true);
        using var app = WindowsProbeNative.OpenOrCreatePlainDirectory(baseDirectory.Root, "FolderTwin");
        using var jobs = WindowsProbeNative.OpenOrCreatePlainDirectory(app, "jobs");
        return new ProductJobRegistry(Path.Combine(local, "FolderTwin", "jobs"));
    }

    internal ProductJob Create(ProductJobOptions options, DedicatedDestinationReview review,
        Action<ProductRegistrationStep>? checkpoint = null)
    {
        var intent = new Intent(1, identity, review.FolderId, options, review.Source, review.Parent);
        Validate(intent, review.FolderId);
        using var operation = OpenLock();
        RequireDisjoint(intent);
        using var job = WindowsProbeNative.CreatePlainDirectory(directory.Root, intent.Id.ToString("N"));
        RetentionJournal.WriteFile(job, IntentName, Encode(intent));
        checkpoint?.Invoke(ProductRegistrationStep.Prepared);
        return Finish(job, intent, checkpoint);
    }

    internal ProductJob Resume(Guid id, Action<ProductRegistrationStep>? checkpoint = null)
    {
        using var operation = OpenLock();
        using var job = OpenJob(id);
        if (WindowsProbeNative.PlainFileExists(job, CommitName))
            return ReadConfirmed(job, ReadIntent(job, id));
        var intent = ReadIntent(job, id, allowDraft: true);
        RequireDisjoint(intent);
        RetentionJournal.WriteFile(job, IntentName, Encode(intent));
        return Finish(job, intent, checkpoint);
    }

    internal ProductJobList List()
    {
        using var operation = OpenLock();
        return ReadList();
    }

    internal DirectoryFence OpenIndexScope()
    {
        var scope = new DirectoryFence(volumePath);
        if (WindowsProbeNative.DirectoryIdentity(scope.Root).AsSpan().SequenceEqual(identity)) return scope;
        scope.Dispose();
        throw new InvalidDataException("La carpeta de configuración cambió de identidad.");
    }

    private ProductJob Finish(SafeFileHandle job, Intent intent, Action<ProductRegistrationStep>? checkpoint)
    {
        string destinationPath = TargetPath(intent);
        var (selectedRoots, record) = OpenRoots();
        using var roots = selectedRoots;
        if (!WindowsProbeNative.PlainFileExists(roots.DestinationHandle, DestinationBinding.MarkerName))
        {
            LabWorkspace.RequireEmpty(roots.DestinationHandle);
            record.Binding.WriteNewMarker(roots.DestinationHandle);
        }
        using var marker = record.Binding.OpenVerifiedMarker(roots.DestinationHandle);
        checkpoint?.Invoke(ProductRegistrationStep.MarkerWritten);
        RetentionJournal.WriteFile(job, CommitName, Confirmation(intent, record));
        checkpoint?.Invoke(ProductRegistrationStep.Confirmed);
        return ReadConfirmed(job, intent);

        (LocalJobRoots Roots, Target Record) OpenRoots()
        {
            bool published = WindowsProbeNative.PlainFileExists(job, TargetName);
            if (published || WindowsProbeNative.PlainFileExists(job, TargetName + ".tmp"))
            {
                var known = ReadTarget(job, intent, published ? TargetName : TargetName + ".tmp");
                var reopened = LocalJobRoots.Reopen(new LocalRootReview(intent.Source,
                    new LocalJobRoot(destinationPath, known.Binding.Identity), 0, false));
                try
                {
                    if (!published)
                        RetentionJournal.WriteFile(job, TargetName, Encode(known));
                    return (reopened, known);
                }
                catch { reopened.Dispose(); throw; }
            }
            var created = LocalJobRoots.CreateDedicatedDestination(new(intent.Source, intent.Parent, intent.Id, 0));
            try
            {
                var bound = new Target(1, intent.Id, SHA256.HashData(Encode(intent)), DestinationBinding.Capture(created.DestinationHandle));
                RetentionJournal.WriteFile(job, TargetName, Encode(bound));
                checkpoint?.Invoke(ProductRegistrationStep.DestinationRecorded);
                return (created, bound);
            }
            catch { created.Dispose(); throw; }
        }
    }

    private ProductJobList ReadList()
    {
        var jobs = new List<ProductJob>();
        var pending = new List<Guid>();
        int unreadable = 0;
        foreach (var entry in WindowsProbeNative.ReadDirectory(directory.Root))
        {
            if (!Guid.TryParseExact(entry.Name, "N", out Guid id))
                continue;
            try
            {
                using var job = OpenJob(id);
                bool confirmed = WindowsProbeNative.PlainFileExists(job, CommitName);
                var intent = ReadIntent(job, id, allowDraft: !confirmed);
                if (confirmed)
                    jobs.Add(ReadConfirmed(job, intent));
                else
                    pending.Add(id);
            }
            catch (Exception error) when (VersionJob.Expected(error)) { unreadable++; }
        }
        return new(jobs.OrderBy(job => job.Options.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(job => job.Id).ToArray(),
            pending.ToArray(), unreadable);
    }

    private void RequireDisjoint(Intent intent)
    {
        string destination = TargetPath(intent);
        if (LocalJobRoots.Overlap(intent.Source.VolumePath, volumePath) || LocalJobRoots.Overlap(destination, volumePath))
            throw new InvalidDataException("Los datos del trabajo deben quedar fuera de la configuración de FolderTwin.");
        foreach (var entry in WindowsProbeNative.ReadDirectory(directory.Root))
        {
            if (!Guid.TryParseExact(entry.Name, "N", out Guid other) || other == intent.Id)
                continue;
            using var job = OpenJob(other);
            var existing = ReadIntent(job, other, allowDraft: !WindowsProbeNative.PlainFileExists(job, CommitName));
            string existingTarget = TargetPath(existing);
            if (LocalJobRoots.Overlap(destination, existingTarget) ||
                LocalJobRoots.Overlap(destination, existing.Source.VolumePath) ||
                LocalJobRoots.Overlap(intent.Source.VolumePath, existingTarget))
                throw new InvalidDataException("Una carpeta se solapa con el destino de otro trabajo.");
        }
    }

    private ProductJob ReadConfirmed(SafeFileHandle job, Intent intent)
    {
        var target = ReadTarget(job, intent);
        using var confirmed = WindowsProbeNative.OpenPlainFile(job, CommitName, FileAccess.Read, FileShare.Read);
        Span<byte> observed = stackalloc byte[32];
        if (confirmed.ReadAtLeast(observed, observed.Length, false) != observed.Length || confirmed.ReadByte() != -1 ||
            !observed.SequenceEqual(Confirmation(intent, target)))
            throw new InvalidDataException("La confirmación del trabajo está dañada.");
        return ApplySettings(job, new(intent.Id, intent.Options, intent.Source, target.Binding));
    }

    private Intent ReadIntent(SafeFileHandle job, Guid id, bool allowDraft = false)
    {
        string name = allowDraft && !WindowsProbeNative.PlainFileExists(job, IntentName) ? IntentName + ".tmp" : IntentName;
        var intent = Decode<Intent>(job, name);
        Validate(intent, id);
        return intent;
    }

    private void Validate(Intent intent, Guid id)
    {
        intent.Options.Validate();
        if (intent.Version != 1 || id == Guid.Empty || intent.Id != id ||
            !intent.RegistryIdentity.AsSpan().SequenceEqual(identity) ||
            !ValidRoot(intent.Source) || !ValidRoot(intent.Parent) ||
            LocalJobRoots.Overlap(intent.Source.VolumePath, TargetPath(intent)))
            throw new InvalidDataException("El registro de selección del trabajo no es válido.");
    }

    private static bool ValidRoot(LocalJobRoot root) => root.Identity is { Length: 24 } &&
        root.VolumePath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) &&
        Path.IsPathFullyQualified(root.VolumePath) && Path.GetFullPath(root.VolumePath) == root.VolumePath;

    private static Target ReadTarget(SafeFileHandle job, Intent intent, string name = TargetName)
    {
        var record = Decode<Target>(job, name);
        if (record.Version != 1 || record.Id != intent.Id || record.Binding.Version != 1 ||
            record.Binding.Marker == Guid.Empty || record.Binding.Identity is not { Length: 24 } ||
            !record.IntentHash.AsSpan().SequenceEqual(SHA256.HashData(Encode(intent))) ||
            !record.Binding.LastVolumePath.Equals(TargetPath(intent), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("El destino guardado no corresponde a la selección.");
        return record;
    }

    private static string TargetPath(Intent intent) => Path.Combine(intent.Parent.VolumePath, $"FolderTwin-{intent.Id:N}");

    private static byte[] Confirmation(Intent intent, Target target) =>
        SHA256.HashData([.. SHA256.HashData(Encode(intent)), .. SHA256.HashData(Encode(target))]);

    private static byte[] Encode<T>(T value)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (body.Length > MaxRecordBytes - 32)
            throw new InvalidDataException("El registro de configuración excede el tamaño admitido.");
        return [.. SHA256.HashData(body), .. body];
    }

    private static T Decode<T>(SafeFileHandle job, string name)
    {
        using var file = WindowsProbeNative.OpenPlainFile(job, name, FileAccess.Read, FileShare.Read);
        if (file.Length is < 33 or > MaxRecordBytes)
            throw new InvalidDataException("El tamaño del registro de configuración no es válido.");
        byte[] bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (!SHA256.HashData(bytes.AsSpan(32)).AsSpan().SequenceEqual(bytes.AsSpan(0, 32)))
            throw new InvalidDataException("El registro de configuración está dañado.");
        return JsonSerializer.Deserialize<T>(bytes.AsSpan(32), Json) ??
            throw new InvalidDataException("El registro de configuración está vacío.");
    }

    private FileStream OpenLock() => WindowsProbeNative.OpenPlainFile(directory.Root, ".foldertwin-jobs.lock", FileAccess.ReadWrite,
        FileShare.None, FileMode.OpenOrCreate);
    private SafeFileHandle OpenJob(Guid id) => WindowsProbeNative.OpenPlainDirectory(directory.Root, id.ToString("N"));
    public void Dispose() => directory.Dispose();
}
