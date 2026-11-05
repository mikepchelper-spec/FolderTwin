using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal enum RegistryStep { PrimaryPartial, PrimaryFlushed, ReplicaPartial, ReplicaFlushed }
internal sealed record RegistryState(Dictionary<string, byte[]> Generations, HashSet<string> Expired,
    long NextSequence, byte[] LastHash, string[] RegistrationOrder, string? MirrorGeneration, string? PendingMirrorGeneration);

// Replicated append-only expectations, separate from the manifests being retained.
// Repair only an exact prefix of the event supplied by the caller's durable plan.
internal static class GenerationRegistry
{
    internal const string PrimaryName = ".foldertwin-generations-v1.jsonl";
    internal const string ReplicaName = ".foldertwin-generations-replica-v1.jsonl";
    private const int MaximumBytes = 16 * 1024 * 1024;
    private const int MaximumLineBytes = 2048;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };
    private sealed record Payload(int Version, long Sequence, string Kind, string Generation, byte[] DocumentHash,
        Guid JobId, byte[] Identity, byte[] PreviousHash);
    private sealed record Envelope(Payload Payload, byte[] Checksum);

    internal static void Initialize(SafeFileHandle root, DestinationBinding binding)
    {
        byte[] genesis = Encode(new(1, 0, "initialize", "", new byte[32], binding.Marker, binding.Identity, new byte[32]));
        foreach (string name in new[] { PrimaryName, ReplicaName })
        {
            using var file = WindowsProbeNative.OpenPlainFile(root, name, FileAccess.Write, FileShare.None, FileMode.CreateNew);
            file.Write(genesis);
            file.Flush(flushToDisk: true);
        }
        _ = Read(root, binding);
    }

    internal static RegistryState Read(SafeFileHandle root, DestinationBinding binding)
    {
        using var primary = WindowsProbeNative.OpenPlainFile(root, PrimaryName, FileAccess.Read, FileShare.Read);
        using var replica = WindowsProbeNative.OpenPlainFile(root, ReplicaName, FileAccess.Read, FileShare.Read);
        byte[] first = ReadBytes(primary);
        byte[] second = ReadBytes(replica);
        if (!first.AsSpan().SequenceEqual(second))
            throw new InvalidDataException("Las réplicas del registro de generaciones difieren. La limpieza queda suspendida.");
        return Parse(first, binding);
    }

    internal static void Register(SafeFileHandle root, DestinationBinding binding, string generation, byte[] documentHash,
        Action<RegistryStep>? checkpoint = null) =>
        Append(root, binding, "register", generation, documentHash, checkpoint);

    internal static void SetExpired(SafeFileHandle root, DestinationBinding binding, string generation, byte[] documentHash,
        bool expired, Action<RegistryStep>? checkpoint = null) =>
        Append(root, binding, expired ? "expire" : "retain", generation, documentHash, checkpoint);

    internal static void SetMirror(SafeFileHandle root, DestinationBinding binding, string generation, byte[] documentHash,
        Action<RegistryStep>? checkpoint = null, bool legacy = false) =>
        Append(root, binding, "mirror", generation, documentHash, checkpoint, legacy);

    internal static void SetMirrorPending(SafeFileHandle root, DestinationBinding binding, string generation, byte[] documentHash,
        Action<RegistryStep>? checkpoint = null) => Append(root, binding, "mirror-begin", generation, documentHash, checkpoint);

    // Only a durable journal may supply the missing transitions. This prefix is
    // never sufficient authority for copying, retention selection or deletion.
    internal static RegistryState ReadCommonPrefix(SafeFileHandle root, DestinationBinding binding)
    {
        using var primary = WindowsProbeNative.OpenPlainFile(root, PrimaryName, FileAccess.Read, FileShare.Read);
        using var replica = WindowsProbeNative.OpenPlainFile(root, ReplicaName, FileAccess.Read, FileShare.Read);
        byte[] first = ReadBytes(primary);
        byte[] second = ReadBytes(replica);
        int prefix = 0;
        for (int i = 0; i < Math.Min(first.Length, second.Length) && first[i] == second[i]; i++)
            if (first[i] == 10)
                prefix = i + 1;
        return Parse(first.AsSpan(0, prefix), binding);
    }

    private static void Append(SafeFileHandle root, DestinationBinding binding, string kind, string generation, byte[] hash,
        Action<RegistryStep>? checkpoint, bool legacyMirror = false)
    {
        _ = VersionStore.GenerationTime(generation);
        if (hash.Length != 32)
            throw new InvalidDataException("Hash de generación no válido.");
        using var primary = WindowsProbeNative.OpenPlainFile(root, PrimaryName, FileAccess.ReadWrite, FileShare.None);
        using var replica = WindowsProbeNative.OpenPlainFile(root, ReplicaName, FileAccess.ReadWrite, FileShare.None);
        byte[] first = ReadBytes(primary);
        byte[] second = ReadBytes(replica);
        int shared = 0;
        int prefix = 0;
        while (shared < Math.Min(first.Length, second.Length) && first[shared] == second[shared])
        {
            if (first[shared] == 10)
                prefix = shared + 1;
            shared++;
        }
        var state = Parse(first.AsSpan(0, prefix), binding);
        if (state.Generations.TryGetValue(generation, out var prior))
        {
            if (!prior.AsSpan().SequenceEqual(hash))
                throw new InvalidDataException("El registro ya contiene otro catálogo para esta generación.");
            bool alreadyApplied = kind == "register" || kind == "expire" && state.Expired.Contains(generation) ||
                kind == "retain" && !state.Expired.Contains(generation) || kind == "mirror" && state.MirrorGeneration == generation ||
                kind == "mirror-begin" && state.PendingMirrorGeneration == generation;
            if (alreadyApplied)
            {
                if (prefix != first.Length || prefix != second.Length)
                    throw new InvalidDataException("Otra transición del registro sigue pendiente.");
                return;
            }
        }
        else if (kind != "register")
            throw new InvalidDataException("La generación que se quiere expirar no está registrada.");
        if (kind == "expire" && (state.MirrorGeneration == generation || state.PendingMirrorGeneration == generation) ||
            kind is "mirror" or "mirror-begin" && state.Expired.Contains(generation))
            throw new InvalidDataException("La generación actual del espejo debe permanecer retenida.");
        if (kind == "mirror-begin" && (state.PendingMirrorGeneration is not null || state.MirrorGeneration == generation) ||
            kind == "mirror" && state.PendingMirrorGeneration != generation && !(legacyMirror && state.PendingMirrorGeneration is null))
            throw new InvalidDataException("La actualización del espejo no coincide con el registro replicado.");
        byte[] expected = Encode(new(1, state.NextSequence, kind, generation, hash,
            binding.Marker, binding.Identity, state.LastHash));
        if (prefix + expected.Length > MaximumBytes)
            throw new InvalidDataException("El registro de generaciones alcanzó su límite.");
        RequirePrefix(first.AsSpan(prefix));
        RequirePrefix(second.AsSpan(prefix));
        Complete(primary, first.Length - prefix, RegistryStep.PrimaryPartial, RegistryStep.PrimaryFlushed);
        Complete(replica, second.Length - prefix, RegistryStep.ReplicaPartial, RegistryStep.ReplicaFlushed);

        void RequirePrefix(ReadOnlySpan<byte> observed)
        {
            if (observed.Length > expected.Length || !expected.AsSpan(0, observed.Length).SequenceEqual(observed))
                throw new InvalidDataException("El registro no coincide con la transición esperada. Se conserva para revisión.");
        }
        void Complete(FileStream output, int existing, RegistryStep partial, RegistryStep flushed)
        {
            output.Seek(0, SeekOrigin.End);
            int middle = expected.Length / 2;
            if (existing < middle)
            {
                output.Write(expected.AsSpan(existing, middle - existing));
                existing = middle;
                output.Flush(flushToDisk: true);
                checkpoint?.Invoke(partial);
            }
            output.Write(expected.AsSpan(existing));
            output.Flush(flushToDisk: true);
            checkpoint?.Invoke(flushed);
        }
    }

    private static byte[] ReadBytes(Stream input)
    {
        if (input.Length is < 1 or > MaximumBytes)
            throw new InvalidDataException("Tamaño del registro de generaciones no válido.");
        var bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] Encode(Payload payload)
    {
        byte[] checksum = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, Json));
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(new Envelope(payload, checksum), Json);
        if (line.Length + 1 > MaximumLineBytes)
            throw new InvalidDataException("El registro de generación es demasiado grande.");
        return [.. line, 10];
    }

    private static RegistryState Parse(ReadOnlySpan<byte> bytes, DestinationBinding binding)
    {
        if (bytes.Length == 0 || bytes[^1] != 10)
            throw new InvalidDataException("El registro de generaciones quedó incompleto.");
        var generations = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var expired = new HashSet<string>(StringComparer.Ordinal);
        var registrationOrder = new List<string>();
        string? mirror = null;
        string? pendingMirror = null;
        long sequence = 0;
        byte[] previous = new byte[32];
        while (!bytes.IsEmpty)
        {
            int end = bytes.IndexOf((byte)10);
            if (end < 1 || end + 1 > MaximumLineBytes)
                throw new InvalidDataException("Un registro de generación está incompleto o es excesivo.");
            _ = Utf8.GetCharCount(bytes[..end]);
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes[..end], Json)
                ?? throw new InvalidDataException("Falta un registro de generación.");
            var payload = envelope.Payload;
            if (payload is null || payload.Version != 1 || payload.Sequence != sequence ||
                payload.JobId != binding.Marker || payload.Identity is not { Length: 24 } ||
                !payload.Identity.AsSpan().SequenceEqual(binding.Identity) ||
                payload.DocumentHash is not { Length: 32 } || payload.PreviousHash is not { Length: 32 } ||
                !payload.PreviousHash.AsSpan().SequenceEqual(previous) || envelope.Checksum is not { Length: 32 } ||
                !SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, Json)).AsSpan().SequenceEqual(envelope.Checksum))
                throw new InvalidDataException("La cadena del registro está dañada o pertenece a otro almacén.");
            if (sequence == 0)
            {
                if (payload.Kind != "initialize" || payload.Generation != "" || payload.DocumentHash.Any(value => value != 0))
                    throw new InvalidDataException("La cabecera del registro no es válida.");
            }
            else
            {
                _ = VersionStore.GenerationTime(payload.Generation);
                if (payload.Kind == "register")
                {
                    if (!generations.TryAdd(payload.Generation, payload.DocumentHash))
                        throw new InvalidDataException("Una generación está registrada dos veces.");
                    registrationOrder.Add(payload.Generation);
                }
                else
                {
                    if (!generations.TryGetValue(payload.Generation, out var hash) ||
                        !hash.AsSpan().SequenceEqual(payload.DocumentHash))
                        throw new InvalidDataException("La transición no corresponde al catálogo registrado.");
                    if (payload.Kind == "expire")
                    {
                        if (mirror == payload.Generation || pendingMirror == payload.Generation || !expired.Add(payload.Generation))
                            throw new InvalidDataException("La generación ya estaba expirada.");
                    }
                    else if (payload.Kind == "mirror")
                    {
                        // The first mirror format published the final event directly.
                        if (mirror == payload.Generation || expired.Contains(payload.Generation) ||
                            pendingMirror is not null && pendingMirror != payload.Generation)
                            throw new InvalidDataException("La referencia actual del espejo no es válida.");
                        mirror = payload.Generation;
                        pendingMirror = null;
                    }
                    else if (payload.Kind == "mirror-begin")
                    {
                        if (pendingMirror is not null || mirror == payload.Generation || expired.Contains(payload.Generation))
                            throw new InvalidDataException("El registro contiene actualizaciones de espejo contradictorias.");
                        pendingMirror = payload.Generation;
                    }
                    else if (payload.Kind != "retain" || !expired.Remove(payload.Generation))
                        throw new InvalidDataException("La transición de retención no es válida.");
                }
            }
            previous = envelope.Checksum;
            sequence++;
            bytes = bytes[(end + 1)..];
        }
        return new(generations, expired, sequence, previous, registrationOrder.ToArray(), mirror, pendingMirror);
    }
}
