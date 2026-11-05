using Microsoft.Win32.SafeHandles;

internal sealed record LocalJobRoot(string VolumePath, byte[] Identity);
internal sealed record LocalRootReview(LocalJobRoot Source, LocalJobRoot Destination, ulong AvailableBytes, bool SameVolume);

// This check is read-only. A data operation must keep the handles open and
// separately verify the destination's job marker before writing.
internal sealed class LocalJobRoots : IDisposable
{
    private readonly DirectoryFence source;
    private readonly DirectoryFence destination;
    internal SafeFileHandle SourceHandle => source.Root;
    internal SafeFileHandle DestinationHandle => destination.Root;
    internal LocalRootReview Review { get; }

    internal LocalJobRoots(string sourcePath, string destinationPath)
    {
        source = new DirectoryFence(sourcePath);
        try
        {
            destination = new DirectoryFence(destinationPath);
            try
            {
                var origin = Inspect(SourceHandle, writable: false);
                var target = Inspect(DestinationHandle, writable: true);
                if (origin.Identity.AsSpan().SequenceEqual(target.Identity) ||
                    Contains(origin.VolumePath, target.VolumePath) || Contains(target.VolumePath, origin.VolumePath))
                    throw new InvalidDataException("El origen y el destino deben ser carpetas separadas.");
                Review = new(origin, target, LabWorkspace.AvailableSpace(DestinationHandle),
                    string.Equals(Path.GetPathRoot(origin.VolumePath), Path.GetPathRoot(target.VolumePath), StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                destination.Dispose();
                throw;
            }
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    internal static LocalJobRoots Reopen(LocalRootReview expected)
    {
        var roots = new LocalJobRoots(expected.Source.VolumePath, expected.Destination.VolumePath);
        if (roots.Review.Source.Identity.AsSpan().SequenceEqual(expected.Source.Identity) &&
            roots.Review.Destination.Identity.AsSpan().SequenceEqual(expected.Destination.Identity))
            return roots;
        roots.Dispose();
        throw new InvalidDataException("Una de las carpetas cambió desde la revisión. Vuelve a elegirla y comprobarla.");
    }

    private static LocalJobRoot Inspect(SafeFileHandle handle, bool writable)
    {
        WindowsProbeNative.RequireNtfsVolume(handle, writable);
        string path = Path.TrimEndingDirectorySeparator(WindowsProbeNative.VolumePath(handle));
        if (path.Length == Path.GetPathRoot(path)?.Length)
            throw new InvalidDataException("Elige una carpeta dedicada dentro del volumen.");
        WindowsProbeNative.RequireSupportedData(handle, "carpeta del trabajo");
        return new(path, WindowsProbeNative.DirectoryIdentity(handle));
    }

    private static bool Contains(string parent, string child) =>
        child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        destination.Dispose();
        source.Dispose();
    }
}
