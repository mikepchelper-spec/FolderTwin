using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

// Registration is explicit and limited to the synthetic fixture. The saved path
// helps locate the directory; authorization uses the handle and the marker.
internal sealed class DestinationBinding
{
    internal const string MarkerName = ".foldertwin-destination";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public required int Version { get; init; }
    public required Guid Marker { get; init; }
    public required byte[] Identity { get; init; }
    public required string LastVolumePath { get; init; }

    public static DestinationBinding Register(SafeFileHandle directory)
    {
        var binding = Capture(directory);
        binding.WriteNewMarker(directory);
        return binding;
    }

    internal static DestinationBinding Capture(SafeFileHandle directory) =>
        new()
        {
            Version = 1,
            Marker = Guid.NewGuid(),
            Identity = WindowsProbeNative.DirectoryIdentity(directory),
            LastVolumePath = WindowsProbeNative.VolumePath(directory)
        };

    internal void WriteNewMarker(SafeFileHandle directory)
    {
        RequireDirectoryIdentity(directory);
        using var marker = WindowsProbeNative.OpenPlainFile(directory, MarkerName, FileAccess.Write, FileShare.None,
            FileMode.CreateNew);
        marker.Write(Marker.ToByteArray());
        marker.Flush(flushToDisk: true);
    }

    public void SaveNew(string configurationPath)
    {
        using var config = new FileStream(configurationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(config, this, JsonOptions);
        config.Flush(flushToDisk: true);
    }

    public static DestinationBinding Load(string configurationPath)
    {
        using var config = File.OpenRead(configurationPath);
        if (config.Length is 0 or > 65_536)
            throw new InvalidDataException("Unexpected destination configuration size.");
        DestinationBinding? binding = JsonSerializer.Deserialize<DestinationBinding>(config, JsonOptions);
        if (binding is null || binding.Version != 1 || binding.Marker == Guid.Empty ||
            binding.Identity is not { Length: 24 } ||
            binding.LastVolumePath?.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidDataException("Invalid destination configuration.");
        return binding;
    }

    public FileStream OpenVerifiedMarker(SafeFileHandle directory)
    {
        RequireDirectoryIdentity(directory);

        FileStream marker;
        try
        {
            marker = WindowsProbeNative.OpenPlainFile(directory, MarkerName, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException error)
        {
            throw new InvalidDataException("The configured destination marker is missing.", error);
        }
        try
        {
            Span<byte> markerBytes = stackalloc byte[16];
            if (marker.ReadAtLeast(markerBytes, markerBytes.Length, throwOnEndOfStream: false) != markerBytes.Length ||
                marker.ReadByte() != -1 || new Guid(markerBytes) != Marker)
                throw new InvalidDataException("The destination marker differs from the saved configuration.");
            return marker;
        }
        catch
        {
            marker.Dispose();
            throw;
        }
    }

    internal void RequireDirectoryIdentity(SafeFileHandle directory)
    {
        if (!WindowsProbeNative.DirectoryIdentity(directory).AsSpan().SequenceEqual(Identity))
            throw new InvalidDataException("La identidad de la carpeta o el volumen de destino cambió.");
    }
}
