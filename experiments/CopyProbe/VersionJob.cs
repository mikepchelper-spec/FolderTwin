using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal enum VersionJobState { Confirmed, Busy, WaitingForSource, WaitingForDestination, Review }
internal sealed record VersionJobResult(Guid Job, DateTimeOffset FinishedUtc, VersionJobState State,
    string? Generation, long Sequence, bool HistoryProblem = false);
internal sealed record VersionJobHistory(VersionJobResult[] Results, int Unreadable);
internal sealed record VersionJobInfo(Guid Id, string Source, string Destination, byte[] SourceIdentity,
    Guid TargetMarker, byte[] TargetIdentity);
internal sealed record VersionJobCatalog(VersionJobInfo[] Jobs, int Unreadable);

internal static class VersionJob
{
    private const int MaximumRecordBytes = 64 * 1024;
    private const string Prefix = ".foldertwin-job-";
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 16
    };
    private sealed record Definition(int Version, Guid Job, byte[] ScopeIdentity, string Source, byte[] SourceIdentity,
        string Destination, DestinationBinding Target);
    private sealed record Envelope<T>(T Payload, byte[] Checksum);

    internal static int RunCommand(string root, Guid id)
    {
        try
        {
            var result = Run(LabWorkspace.Open(root), id);
            Console.WriteLine(JsonSerializer.Serialize(result, Json));
            return result.State == VersionJobState.Confirmed && !result.HistoryProblem ? 0 : 1;
        }
        catch (Exception error) when (Expected(error))
        {
            Console.Error.WriteLine("El trabajo requiere revisión. " + error.Message);
            return 1;
        }
    }

    internal static Guid Create(LabWorkspace workspace, string source, string destination)
    {
        using var scope = workspace.OpenRegisteredRoot();
        workspace.CheckRoots(source, destination);
        var store = VersionStore.Open(workspace, destination);
        using var origin = new DirectoryFence(source);
        byte[] sourceIdentity = WindowsProbeNative.DirectoryIdentity(origin.Root);
        var existing = List(workspace).Jobs.FirstOrDefault(job => job.Source == Path.GetFileName(source) &&
            job.Destination == Path.GetFileName(destination) && job.TargetMarker == store.Binding.Marker &&
            job.TargetIdentity.AsSpan().SequenceEqual(store.Binding.Identity) &&
            job.SourceIdentity.AsSpan().SequenceEqual(sourceIdentity));
        if (existing is not null)
            return existing.Id;
        Guid id = Guid.NewGuid();
        var definition = new Definition(1, id, WindowsProbeNative.DirectoryIdentity(scope.Root),
            Path.GetFileName(source), sourceIdentity,
            Path.GetFileName(destination), store.Binding);
        Publish(scope.Root, Name(id) + ".json", definition);
        return id;
    }

    internal static VersionJobCatalog List(LabWorkspace workspace)
    {
        using var scope = workspace.OpenRegisteredRoot();
        var jobs = new List<VersionJobInfo>();
        int unreadable = 0;
        foreach (var entry in WindowsProbeNative.ReadDirectory(scope.Root).Where(entry =>
            entry.Name.StartsWith(Prefix, StringComparison.Ordinal) && entry.Name.EndsWith(".json", StringComparison.Ordinal)))
        {
            try
            {
                if (!Guid.TryParseExact(entry.Name.AsSpan(Prefix.Length, entry.Name.Length - Prefix.Length - 5), "N", out var id))
                    throw new InvalidDataException("El nombre del trabajo no es válido.");
                var definition = ReadDefinition(workspace, scope.Root, id);
                jobs.Add(new(id, definition.Source, definition.Destination, definition.SourceIdentity,
                    definition.Target.Marker, definition.Target.Identity));
            }
            catch (Exception error) when (Expected(error)) { unreadable++; }
        }
        return new(jobs.ToArray(), unreadable);
    }

    internal static VersionJobResult Run(LabWorkspace workspace, Guid id, Action<CopyStep, string>? checkpoint = null,
        Func<DateTimeOffset>? clock = null)
    {
        using var scope = workspace.OpenRegisteredRoot();
        var definition = ReadDefinition(workspace, scope.Root, id);
        string? generation = null;
        VersionJobState state = VersionJobState.Review;
        FileStream acquired;
        try
        {
            acquired = WindowsProbeNative.OpenPlainFile(scope.Root, Name(id) + ".lock",
                FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
        }
        catch (Exception error) when (SharingViolation(error))
        {
            return new(id, clock?.Invoke() ?? DateTimeOffset.UtcNow, VersionJobState.Busy, null, 0);
        }
        using var jobLock = acquired;
        long sequence = checked(History(workspace, id).Results.Select(result => result.Sequence).DefaultIfEmpty(0).Max() + 1);
        try
        {
            string destination = Path.Combine(workspace.Root, definition.Destination);
            VersionStore store;
            try { store = VersionStore.Open(workspace, destination); }
            catch (Exception error) when (Unavailable(error))
            {
                state = VersionJobState.WaitingForDestination;
                return Finish();
            }
            if (store.Binding.Marker != definition.Target.Marker ||
                !store.Binding.Identity.AsSpan().SequenceEqual(definition.Target.Identity))
                throw new InvalidDataException("El destino del trabajo cambió de identidad.");
            string source = Path.Combine(workspace.Root, definition.Source);
            var pending = store.ListPending().Where(point => point.Source == source)
                .OrderBy(point => point.Id, StringComparer.Ordinal).FirstOrDefault();
            if (pending is not null)
            {
                generation = pending.Id;
                store.ResumePoint(generation, expectedSourceIdentity: definition.SourceIdentity);
            }
            else
            {
                DirectoryFence origin;
                try { origin = new DirectoryFence(source); }
                catch (Exception error) when (Unavailable(error))
                {
                    state = VersionJobState.WaitingForSource;
                    return Finish();
                }
                using (origin)
                {
                    if (!WindowsProbeNative.DirectoryIdentity(origin.Root).AsSpan().SequenceEqual(definition.SourceIdentity))
                        throw new InvalidDataException("El origen del trabajo cambió de identidad.");
                    generation = VersionStore.NewGenerationId();
                    store.CreatePoint(source, generation, objectCheckpoint: checkpoint);
                }
            }
            state = VersionJobState.Confirmed;
        }
        catch (Exception error) when (Expected(error))
        {
            state = SharingViolation(error) && generation is null ? VersionJobState.Busy : VersionJobState.Review;
        }
        return Finish();

        VersionJobResult Finish()
        {
            var result = new VersionJobResult(id, clock?.Invoke() ?? DateTimeOffset.UtcNow, state, generation, sequence);
            try { Publish(scope.Root, Name(id) + "-" + VersionStore.NewGenerationId() + ".result", result); }
            catch (Exception error) when (Expected(error)) { return result with { HistoryProblem = true }; }
            return result;
        }
    }

    internal static VersionJobHistory History(LabWorkspace workspace, Guid id)
    {
        using var scope = workspace.OpenRegisteredRoot();
        _ = ReadDefinition(workspace, scope.Root, id);
        var results = new List<VersionJobResult>();
        int unreadable = 0;
        string prefix = Name(id) + "-";
        foreach (var entry in WindowsProbeNative.ReadDirectory(scope.Root)
            .Where(entry => entry.Name.StartsWith(prefix, StringComparison.Ordinal) && entry.Name.EndsWith(".result", StringComparison.Ordinal)))
        {
            try
            {
                var result = Read<VersionJobResult>(scope.Root, entry.Name);
                if (result.Job != id || result.Sequence < 1 || !Enum.IsDefined(result.State) || result.HistoryProblem ||
                    result.State == VersionJobState.Confirmed && result.Generation is null)
                    throw new InvalidDataException("Resultado del trabajo no válido.");
                if (result.Generation is not null)
                    _ = VersionStore.GenerationTime(result.Generation);
                results.Add(result);
            }
            catch (Exception error) when (Expected(error)) { unreadable++; }
        }
        return new(results.OrderByDescending(result => result.Sequence).ToArray(), unreadable);
    }

    private static Definition ReadDefinition(LabWorkspace workspace, SafeFileHandle root, Guid id)
    {
        var definition = Read<Definition>(root, Name(id) + ".json");
        if (definition.Version != 1 || definition.Job != id ||
            !WindowsProbeNative.DirectoryIdentity(root).AsSpan().SequenceEqual(definition.ScopeIdentity) ||
            definition.SourceIdentity is not { Length: 24 } ||
            definition.Target is null || definition.Target.Version != 1 ||
            definition.Target.Marker == Guid.Empty || definition.Target.Identity is not { Length: 24 })
            throw new InvalidDataException("La configuración del trabajo está dañada o pertenece a otro espacio.");
        workspace.CheckRoots(Path.Combine(workspace.Root, definition.Source), Path.Combine(workspace.Root, definition.Destination));
        return definition;
    }

    private static void Publish<T>(SafeFileHandle root, string name, T payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope<T>(payload,
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, Json))), Json);
        if (bytes.Length > MaximumRecordBytes)
            throw new InvalidDataException("El registro del trabajo supera el límite.");
        string draft = name + ".tmp";
        using (var output = WindowsProbeNative.OpenPlainFile(root, draft, FileAccess.Write, FileShare.None, FileMode.CreateNew))
        {
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        using var verified = WindowsProbeNative.OpenPlainFile(root, draft, FileAccess.Read, FileShare.Read, rename: true);
        if (verified.Length != bytes.Length)
            throw new InvalidDataException("El registro del trabajo quedó incompleto.");
        var observed = new byte[bytes.Length];
        verified.ReadExactly(observed);
        if (!observed.AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("El registro del trabajo cambió antes de publicarse.");
        WindowsProbeNative.RenameNew(verified.SafeFileHandle, root, name);
    }

    private static T Read<T>(SafeFileHandle root, string name)
    {
        using var file = WindowsProbeNative.OpenPlainFile(root, name, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > MaximumRecordBytes)
            throw new InvalidDataException("Tamaño de registro del trabajo no válido.");
        var envelope = JsonSerializer.Deserialize<Envelope<T>>(file, Json)
            ?? throw new InvalidDataException("Falta el registro del trabajo.");
        if (envelope.Payload is null || envelope.Checksum is not { Length: 32 } ||
            !SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, Json)).AsSpan().SequenceEqual(envelope.Checksum))
            throw new InvalidDataException("El registro del trabajo no coincide con su checksum.");
        return envelope.Payload;
    }

    internal static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        ArgumentException or JsonException or Win32Exception;
    private static bool Unavailable(Exception error) => error is FileNotFoundException or DirectoryNotFoundException ||
        error.InnerException is Win32Exception { NativeErrorCode: 2 or 3 or 21 };
    private static bool SharingViolation(Exception error) => error is Win32Exception { NativeErrorCode: 32 } ||
        error.InnerException is Win32Exception { NativeErrorCode: 32 };
    private static string Name(Guid id) => id == Guid.Empty ? throw new ArgumentException("Falta el identificador del trabajo.") :
        $"{Prefix}{id:N}";
}
