using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;

internal sealed record FileMetadata(DateTime LastWriteUtc, DateTime? CreationUtc = null, bool? Hidden = null,
    string? ZoneData = null, string? ZoneHash = null)
{
    internal static FileMetadata Read(SafeFileHandle file)
    {
        var metadata = WindowsProbeNative.ReadFileMetadata(file);
        if (!WindowsProbeNative.HasZoneIdentifier(file)) return metadata;
        byte[] zone = ZoneIdentifierStream.Read(file) ?? throw new InvalidDataException("La marca Zone.Identifier cambió durante la lectura.");
        return metadata with { ZoneData = Convert.ToBase64String(zone), ZoneHash = Convert.ToHexStringLower(SHA256.HashData(zone)) };
    }

    internal bool IsValid() => IsSupportedTime(LastWriteUtc) &&
        (CreationUtc is { } creation ? IsSupportedTime(creation) && Hidden.HasValue : !Hidden.HasValue) && ValidZone();

    private bool ValidZone()
    {
        if (ZoneData is null) return ZoneHash is null;
        if (CreationUtc is null || Hidden is null || ZoneHash is not { Length: 64 } || ZoneData.Length > ((ZoneIdentifierStream.MaximumBytes + 2) / 3) * 4)
            return false;
        byte[] bytes = new byte[ZoneIdentifierStream.MaximumBytes];
        return Convert.TryFromBase64String(ZoneData, bytes, out int count) &&
            Convert.ToBase64String(bytes, 0, count) == ZoneData &&
            Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, count))) == ZoneHash;
    }

    internal void Apply(SafeFileHandle file)
    {
        if (!IsValid()) throw new InvalidDataException("Los metadatos del archivo no son válidos.");
        if (ZoneData is not null) ZoneIdentifierStream.Write(file, Convert.FromBase64String(ZoneData));
        if (Hidden is { } hidden)
        {
            FileAttributes current = File.GetAttributes(file);
            if (current.HasFlag(FileAttributes.Hidden) != hidden)
            {
                FileAttributes attributes = hidden
                    ? (current & ~FileAttributes.Normal) | FileAttributes.Hidden
                    : current & ~FileAttributes.Hidden;
                File.SetAttributes(file, attributes == 0 ? FileAttributes.Normal : attributes);
            }
        }
        if (CreationUtc is { } creation)
            File.SetCreationTimeUtc(file, creation);
        File.SetLastWriteTimeUtc(file, LastWriteUtc);
    }

    internal void RequireMatch(SafeFileHandle file)
    {
        var observed = Read(file);
        if (observed.LastWriteUtc != LastWriteUtc)
            throw new InvalidDataException("La fecha de modificación del archivo no coincide con la registrada.");
        if (CreationUtc is { } creation && observed.CreationUtc != creation)
            throw new InvalidDataException("La fecha de creación del archivo no coincide con la registrada.");
        if (Hidden is { } hidden && observed.Hidden != hidden)
            throw new InvalidDataException("El atributo Oculto del archivo no coincide con el registrado.");
        if (observed.ZoneData != ZoneData || observed.ZoneHash != ZoneHash)
            throw new InvalidDataException("La marca de procedencia Zone.Identifier del archivo no coincide con la registrada.");
    }

    private static bool IsSupportedTime(DateTime value) =>
        value.Kind == DateTimeKind.Utc && value > DateTime.FromFileTimeUtc(0);
}
