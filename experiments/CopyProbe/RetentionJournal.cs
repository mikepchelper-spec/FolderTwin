using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal enum RetentionStep { Planned, Expired, Moved, Quarantined, Undoing, Returned, Retained, Undone, Purging, Deleted, Purged }
internal sealed record RetentionOperation(string Id, DateTimeOffset CreatedUtc, string State, int Objects);
internal sealed record RetentionPlan(int Version, Guid Marker, byte[] Identity, string Id, RetentionPreview Review);

internal static class RetentionJournal
{
    internal const string DirectoryName = "quarantine";
    private const int MaximumBytes = 128 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 16
    };

    internal static void CheckId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var value) || value.ToString("N") != id)
            throw new InvalidDataException("Identificador de cuarentena inválido.");
    }

    internal static byte[] Encode(RetentionPlan plan)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(plan, Json);
        if (body.Length > MaximumBytes - 32)
            throw new InvalidDataException("El plan de cuarentena supera el límite.");
        return [.. SHA256.HashData(body), .. body];
    }

    internal static RetentionPlan Read(SafeFileHandle directory, DestinationBinding binding, string id)
    {
        CheckId(id);
        byte[] encoded = ReadFile(directory, "plan");
        if (encoded.Length < 33 || !SHA256.HashData(encoded.AsSpan(32)).AsSpan().SequenceEqual(encoded.AsSpan(0, 32)))
            throw new InvalidDataException("El diario de cuarentena está incompleto o dañado.");
        var plan = JsonSerializer.Deserialize<RetentionPlan>(encoded.AsSpan(32), Json)
            ?? throw new InvalidDataException("Falta el plan de cuarentena.");
        if (plan.Version != 1 || plan.Id != id || plan.Marker != binding.Marker || plan.Identity is not { Length: 24 } ||
            !plan.Identity.AsSpan().SequenceEqual(binding.Identity) || plan.Review is null || plan.Review.Policy is null ||
            plan.Review.RegistryHash is not { Length: 32 } || plan.Review.Keep is null || plan.Review.Expire is null ||
            plan.Review.Objects is null || plan.Review.QuarantineBytes < 0 || plan.Review.ReviewedUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("El plan de cuarentena no pertenece al almacén o no es válido.");
        plan.Review.Policy.Validate();
        foreach (string generation in plan.Review.Keep.Concat(plan.Review.Expire))
        {
            if (generation is null)
                throw new InvalidDataException("Una referencia de generación es nula en el plan de cuarentena.");
            _ = VersionStore.GenerationTime(generation);
        }
        if (plan.Review.Keep.Concat(plan.Review.Expire).Distinct(StringComparer.Ordinal).Count() !=
            plan.Review.Keep.Length + plan.Review.Expire.Length || plan.Review.Keep.Length == 0 ||
            plan.Review.Objects.Distinct(StringComparer.Ordinal).Count() != plan.Review.Objects.Length ||
            plan.Review.Objects.Any(digest => digest is null || !IsDigest(digest)))
            throw new InvalidDataException("Referencias inválidas o repetidas en el plan de cuarentena.");
        return plan;
    }

    internal static bool IsDigest(string digest) => digest.Length == 64 && digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static void WriteFile(SafeFileHandle directory, string name, byte[] bytes)
    {
        if (!WindowsProbeNative.PlainFileExists(directory, name))
        {
            string temporary = name + ".tmp";
            if (!WindowsProbeNative.PlainFileExists(directory, temporary))
            {
                using var output = WindowsProbeNative.OpenPlainFile(directory, temporary, FileAccess.Write, FileShare.None, FileMode.CreateNew);
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            using var input = WindowsProbeNative.OpenPlainFile(directory, temporary, FileAccess.Read, FileShare.Read, rename: true);
            if (!ReadBytes(input).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("Un temporal del diario difiere del plan. Se conserva para revisión.");
            WindowsProbeNative.RenameNew(input.SafeFileHandle, directory, name);
        }
        if (!ReadFile(directory, name).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("El diario existente difiere del plan. Se conserva para revisión.");
    }

    internal static bool HasState(SafeFileHandle directory, RetentionPlan plan, string state)
    {
        if (!WindowsProbeNative.PlainFileExists(directory, state))
            return false;
        if (!ReadFile(directory, state).AsSpan().SequenceEqual(StateBytes(plan, state)))
            throw new InvalidDataException("Una transición de cuarentena está dañada.");
        return true;
    }

    internal static void SetState(SafeFileHandle directory, RetentionPlan plan, string state) =>
        WriteFile(directory, state, StateBytes(plan, state));

    internal static DateTimeOffset QuarantinedAt(SafeFileHandle directory, RetentionPlan plan, DateTimeOffset? completed = null)
    {
        const string name = "quarantined-at";
        if (completed is { } instant && !WindowsProbeNative.PlainFileExists(directory, name))
        {
            byte[] body = [.. SHA256.HashData(Encode(plan)), .. BitConverter.GetBytes(instant.UtcTicks)];
            WriteFile(directory, name, [.. body, .. SHA256.HashData(body)]);
        }
        byte[] encoded = ReadFile(directory, name);
        if (encoded.Length != 72 || !SHA256.HashData(Encode(plan)).AsSpan().SequenceEqual(encoded.AsSpan(0, 32)) ||
            !SHA256.HashData(encoded.AsSpan(0, 40)).AsSpan().SequenceEqual(encoded.AsSpan(40)))
            throw new InvalidDataException("La fecha de cuarentena está dañada.");
        return new DateTimeOffset(BitConverter.ToInt64(encoded, 32), TimeSpan.Zero);
    }

    internal static string State(SafeFileHandle directory, RetentionPlan plan)
    {
        bool undone = HasState(directory, plan, "undone");
        bool undoing = HasState(directory, plan, "undoing");
        bool purged = HasState(directory, plan, "purged");
        bool purging = HasState(directory, plan, "purging");
        bool quarantined = HasState(directory, plan, "quarantined");
        if ((undone && !undoing) || (purged && !purging) || (purging && !quarantined) || (undoing && purging))
            throw new InvalidDataException("El diario contiene estados contradictorios.");
        return undone ? "undone" : purged ? "purged" : undoing ? "undoing" : purging ? "purging" :
            quarantined ? "quarantined" : "moving";
    }

    private static byte[] StateBytes(RetentionPlan plan, string state) =>
        SHA256.HashData([.. SHA256.HashData(Encode(plan)), .. System.Text.Encoding.UTF8.GetBytes(state)]);

    private static byte[] ReadFile(SafeFileHandle directory, string name)
    {
        using var input = WindowsProbeNative.OpenPlainFile(directory, name, FileAccess.Read, FileShare.Read);
        return ReadBytes(input);
    }

    private static byte[] ReadBytes(Stream input)
    {
        if (input.Length is < 1 or > MaximumBytes)
            throw new InvalidDataException("Tamaño de diario fuera de límites.");
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }
}
