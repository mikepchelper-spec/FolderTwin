using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal enum ProductBackupMode { Accumulative, Versions, ProtectedMirror }
internal enum ProductWatchMode { Off, Watching, Paused }
internal enum ProductWatchActivity : byte { Watching = 1, Pending, Running, SourceUnavailable }
internal sealed record ProductWatchTiming(int QuietSeconds, int MaximumWaitSeconds, int ReconcileSeconds)
{
    internal static readonly ProductWatchTiming Default = new(2, 20, 900);

    internal void Validate()
    {
        if (QuietSeconds is < 1 or > 10 || MaximumWaitSeconds is < 5 or > 60 || MaximumWaitSeconds <= QuietSeconds ||
            ReconcileSeconds is < 10 or > 3600 || ReconcileSeconds <= MaximumWaitSeconds)
            throw new InvalidDataException("Los intervalos del monitor no son válidos. Revisa monitor-timing.json o retíralo para usar los valores iniciales.");
    }
}
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
    bool Enabled = true, bool Retired = false, bool Imported = false, bool SourceReviewed = true, string[]? SourceAliases = null,
    ProductWatchMode WatchMode = ProductWatchMode.Off, int AutomaticMirrorPolicy = 0);
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
        LocalJobRoot Source, LocalJobRoot Parent,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ImportedBinding? Imported = null);
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

    internal FileStream AcquireMonitorLock() =>
        WindowsProbeNative.OpenPlainFile(directory.Root, ".foldertwin-monitor.lock",
            FileAccess.ReadWrite, FileShare.Read, FileMode.OpenOrCreate);

    internal ProductWatchTiming ReadWatchTiming()
    {
        const string name = "monitor-timing.json";
        if (!WindowsProbeNative.PlainFileExists(directory.Root, name)) return ProductWatchTiming.Default;
        using var file = WindowsProbeNative.OpenPlainFile(directory.Root, name, FileAccess.Read, FileShare.Read, FileMode.Open);
        if (file.Length is < 1 or > 1024) throw new InvalidDataException("El archivo de intervalos del monitor tiene un tamaño inválido.");
        var timing = JsonSerializer.Deserialize<ProductWatchTiming>(file, Json) ??
            throw new InvalidDataException("El archivo de intervalos del monitor está vacío.");
        timing.Validate();
        return timing;
    }

    internal bool MonitorOwnsRegistry()
    {
        const string name = ".foldertwin-monitor.lock";
        if (!WindowsProbeNative.PlainFileExists(directory.Root, name)) return false;
        try
        {
            using var idle = WindowsProbeNative.OpenPlainFile(directory.Root, name, FileAccess.ReadWrite, FileShare.Read);
            return false;
        }
        catch (Exception error) when (VersionJob.SharingViolation(error)) { return true; }
    }

    internal int? ReadMonitorProcess() => ReadMonitorProcess(null, out _);

    internal int? ReadMonitorProcess(Guid? job, out ProductWatchActivity? activity)
    {
        activity = null;
        if (!MonitorOwnsRegistry()) return null;
        using var heartbeat = WindowsProbeNative.OpenPlainFile(directory.Root, ".foldertwin-monitor.lock", FileAccess.Read, FileShare.ReadWrite);
        Span<byte> bytes = stackalloc byte[12];
        if (heartbeat.ReadAtLeast(bytes, bytes.Length, false) != bytes.Length) return null;
        int process = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        long age = Environment.TickCount64 - BinaryPrimitives.ReadInt64LittleEndian(bytes[4..]);
        if (process <= 0 || age is < 0 or >= 15_000) return null;
        if (job is not null)
        {
            Span<byte> entry = stackalloc byte[17];
            for (int i = 0; i < 4096 && heartbeat.ReadAtLeast(entry, entry.Length, false) == entry.Length; i++)
            {
                if (new Guid(entry[..16]) != job.Value) continue;
                var observed = (ProductWatchActivity)entry[16];
                if (Enum.IsDefined(observed)) activity = observed;
                break;
            }
        }
        return process;
    }

    internal static void WriteMonitorHeartbeat(FileStream lease, IEnumerable<(Guid Job, ProductWatchActivity Activity)> activities)
    {
        using var snapshot = new MemoryStream();
        Span<byte> bytes = stackalloc byte[17];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, Environment.ProcessId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[4..], Environment.TickCount64);
        snapshot.Write(bytes[..12]);
        foreach (var (job, activity) in activities.Take(4096))
        {
            job.TryWriteBytes(bytes[..16]);
            bytes[16] = (byte)activity;
            snapshot.Write(bytes);
        }
        lease.Position = 0;
        lease.Write(snapshot.GetBuffer().AsSpan(0, (int)snapshot.Length));
        if (lease.Length != snapshot.Length) lease.SetLength(snapshot.Length);
        lease.Flush(); // Volatile health indication, not an execution record.
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
        if (intent.Imported is not null) return FinishImported(job, intent, checkpoint);
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
            RequireDisjointFromActiveRestores(job, other, intent.Source.VolumePath, destination);
            var existing = ReadIntent(job, other, allowDraft: !WindowsProbeNative.PlainFileExists(job, CommitName));
            string existingSource = WindowsProbeNative.PlainFileExists(job, CommitName) ? ReadConfirmed(job, existing).Source.VolumePath : existing.Source.VolumePath;
            string existingTarget = TargetPath(existing);
            if (LocalJobRoots.Overlap(destination, existingTarget) ||
                LocalJobRoots.Overlap(destination, existingSource) ||
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
        return ApplySettings(job, new(intent.Id, intent.Options, intent.Source, target.Binding,
            Enabled: intent.Imported is null, Imported: intent.Imported is not null,
            SourceReviewed: intent.Imported?.SourceReviewed ?? true, SourceAliases: intent.Imported?.SourceAliases));
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
        if (intent.Imported is { } imported) ValidateImportedBinding(imported, intent.Source, intent.Parent);
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

    private static string TargetPath(Intent intent) => intent.Imported?.Target.LastVolumePath ?? Path.Combine(intent.Parent.VolumePath, $"FolderTwin-{intent.Id:N}");

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
