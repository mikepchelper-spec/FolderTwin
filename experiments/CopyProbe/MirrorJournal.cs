using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal enum MirrorStep { Planned, PointConfirmed, Staged, Ready, Retired, DirectoryRemoved, Promoted, CurrentVerified, Registered, Released, Applied }
internal sealed record MirrorPlan(int Version, string Generation, byte[] DocumentHash, string? Previous, byte[] PreviousHash,
    string Source, byte[] SourceIdentity, byte[] CurrentIdentity, Guid Marker, byte[] StoreIdentity,
    int MaximumChangePercent, bool EmptyReviewed, bool MassReviewed);
internal sealed record MirrorOperation(string Generation, string? Previous, bool Ready, bool Applied);

internal static class MirrorJournal
{
    internal const string DirectoryName = "mirror";
    internal const string CurrentName = "current";
    internal const string OperationsName = "operations";
    private const string HeaderName = ".foldertwin-mirror";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        MaxDepth = 8
    };

    internal static void Initialize(SafeFileHandle store, DestinationBinding binding)
    {
        using var mirror = WindowsProbeNative.OpenOrCreatePlainDirectory(store, DirectoryName);
        LabWorkspace.RequireEmpty(mirror);
        using var current = WindowsProbeNative.OpenOrCreatePlainDirectory(mirror, CurrentName);
        using var operations = WindowsProbeNative.OpenOrCreatePlainDirectory(mirror, OperationsName);
        byte[] header = [.. "FTMIRR01"u8, .. binding.Identity, .. binding.Marker.ToByteArray(), .. WindowsProbeNative.DirectoryIdentity(current)];
        RetentionJournal.WriteFile(mirror, HeaderName, header);
    }

    internal static SafeFileHandle Open(SafeFileHandle store, DestinationBinding binding, out byte[] currentIdentity)
    {
        var mirror = WindowsProbeNative.OpenPlainDirectory(store, DirectoryName);
        try
        {
            using var header = WindowsProbeNative.OpenPlainFile(mirror, HeaderName, FileAccess.Read, FileShare.Read);
            byte[] bytes = new byte[72];
            header.ReadExactly(bytes);
            if (header.ReadByte() != -1 || !bytes.AsSpan(0, 8).SequenceEqual("FTMIRR01"u8) ||
                !bytes.AsSpan(8, 24).SequenceEqual(binding.Identity) || new Guid(bytes.AsSpan(32, 16)) != binding.Marker)
                throw new InvalidDataException("El registro del espejo está dañado o pertenece a otro almacén.");
            currentIdentity = bytes[48..];
            using var current = WindowsProbeNative.OpenPlainDirectory(mirror, CurrentName);
            if (!WindowsProbeNative.DirectoryIdentity(current).AsSpan().SequenceEqual(currentIdentity))
                throw new InvalidDataException("La carpeta actual del espejo cambió de identidad.");
            return mirror;
        }
        catch { mirror.Dispose(); throw; }
    }

    internal static byte[] Encode(MirrorPlan plan)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(plan, Json);
        if (body.Length > 8192)
            throw new InvalidDataException("El plan del espejo supera su límite.");
        return [.. SHA256.HashData(body), .. body];
    }

    internal static MirrorPlan Read(SafeFileHandle directory, string generation, DestinationBinding binding, byte[] currentIdentity)
    {
        _ = VersionStore.GenerationTime(generation);
        using var input = WindowsProbeNative.OpenPlainFile(directory, "plan", FileAccess.Read, FileShare.Read);
        if (input.Length is < 33 or > 8224)
            throw new InvalidDataException("El plan del espejo está incompleto o es excesivo.");
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        if (!SHA256.HashData(bytes.AsSpan(32)).AsSpan().SequenceEqual(bytes.AsSpan(0, 32)))
            throw new InvalidDataException("El plan del espejo está dañado.");
        var plan = JsonSerializer.Deserialize<MirrorPlan>(bytes.AsSpan(32), Json)
            ?? throw new InvalidDataException("Falta el plan del espejo.");
        if (plan.Version != 1 || plan.Generation != generation || plan.Marker != binding.Marker ||
            plan.StoreIdentity is not { Length: 24 } || !plan.StoreIdentity.AsSpan().SequenceEqual(binding.Identity) ||
            plan.CurrentIdentity is not { Length: 24 } || !plan.CurrentIdentity.AsSpan().SequenceEqual(currentIdentity) ||
            plan.SourceIdentity is not { Length: 24 } || plan.DocumentHash is not { Length: 32 } || plan.PreviousHash is not { Length: 32 } ||
            plan.MaximumChangePercent is < 1 or > 100)
            throw new InvalidDataException("Identidades o referencias inválidas en el plan del espejo.");
        DirectoryFence.CheckName(plan.Source);
        if (plan.Previous is { } previous)
            _ = VersionStore.GenerationTime(previous);
        else if (plan.PreviousHash.Any(value => value != 0))
            throw new InvalidDataException("Un espejo inicial no puede declarar un catálogo anterior.");
        return plan;
    }

    internal static bool HasState(SafeFileHandle directory, MirrorPlan plan, string state)
    {
        if (!WindowsProbeNative.PlainFileExists(directory, state))
            return false;
        using var input = WindowsProbeNative.OpenPlainFile(directory, state, FileAccess.Read, FileShare.Read);
        byte[] hash = StateBytes(plan, state);
        byte[] bytes = new byte[hash.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || !bytes.AsSpan().SequenceEqual(hash))
            throw new InvalidDataException("Una transición del espejo está dañada.");
        return true;
    }

    internal static void SetState(SafeFileHandle directory, MirrorPlan plan, string state) =>
        RetentionJournal.WriteFile(directory, state, StateBytes(plan, state));

    private static byte[] StateBytes(MirrorPlan plan, string state) => SHA256.HashData(
        [.. SHA256.HashData(Encode(plan)), .. System.Text.Encoding.UTF8.GetBytes(state)]);
}
