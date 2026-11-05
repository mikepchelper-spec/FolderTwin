using Microsoft.Win32.SafeHandles;

internal sealed record LocalJobRoot(string VolumePath, byte[] Identity);
internal sealed record LocalRootReview(LocalJobRoot Source, LocalJobRoot Destination, ulong AvailableBytes, bool SameVolume);
internal sealed record DedicatedDestinationReview(LocalJobRoot Source, LocalJobRoot Parent, Guid FolderId, ulong AvailableBytes)
{
    internal string FolderName => $"FolderTwin-{FolderId:N}";
}

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
                if (origin.Identity.AsSpan().SequenceEqual(target.Identity) || Overlap(origin.VolumePath, target.VolumePath))
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

    internal static DedicatedDestinationReview ReviewDedicatedDestination(string sourcePath, string parentPath)
    {
        using var origin = new DirectoryFence(sourcePath);
        using var parent = new DirectoryFence(parentPath);
        var source = Inspect(origin.Root, writable: false);
        var destinationParent = Inspect(parent.Root, writable: true, allowVolumeRoot: true);
        var review = new DedicatedDestinationReview(source, destinationParent, Guid.NewGuid(), LabWorkspace.AvailableSpace(parent.Root));
        CheckNewDestination(review);
        return review;
    }

    internal static LocalJobRoots CreateDedicatedDestination(DedicatedDestinationReview review)
    {
        using var origin = new DirectoryFence(review.Source.VolumePath);
        using var parent = new DirectoryFence(review.Parent.VolumePath);
        var source = Inspect(origin.Root, writable: false);
        var destinationParent = Inspect(parent.Root, writable: true, allowVolumeRoot: true);
        if (!source.Identity.AsSpan().SequenceEqual(review.Source.Identity) ||
            !destinationParent.Identity.AsSpan().SequenceEqual(review.Parent.Identity))
            throw new InvalidDataException("La carpeta elegida cambió desde la revisión.");
        string destinationPath = CheckNewDestination(review with { Source = source, Parent = destinationParent });
        using var created = WindowsProbeNative.CreatePlainDirectory(parent.Root, review.FolderName);
        LabWorkspace.RequireEmpty(created);
        var result = new LocalJobRoots(source.VolumePath, destinationPath);
        if (result.Review.Destination.Identity.AsSpan().SequenceEqual(WindowsProbeNative.DirectoryIdentity(created)))
            return result;
        result.Dispose();
        throw new InvalidDataException("El destino nuevo cambió de identidad.");
    }

    private static string CheckNewDestination(DedicatedDestinationReview review)
    {
        if (review.FolderId == Guid.Empty)
            throw new InvalidDataException("Falta la identidad de la carpeta dedicada.");
        string path = Path.Combine(review.Parent.VolumePath, review.FolderName);
        if (Overlap(path, review.Source.VolumePath))
            throw new InvalidDataException("El destino dedicado debe quedar fuera del origen.");
        return path;
    }

    internal static LocalJobRoot InspectSelected(string path, bool writable, bool allowVolumeRoot = false)
    {
        using var root = new DirectoryFence(path);
        return Inspect(root.Root, writable, allowVolumeRoot);
    }

    private static LocalJobRoot Inspect(SafeFileHandle handle, bool writable, bool allowVolumeRoot = false)
    {
        WindowsProbeNative.RequireNtfsVolume(handle, writable);
        string path = Path.TrimEndingDirectorySeparator(WindowsProbeNative.VolumePath(handle));
        if (!allowVolumeRoot && path.Length == Path.GetPathRoot(path)?.Length)
            throw new InvalidDataException("Elige una carpeta dedicada dentro del volumen.");
        WindowsProbeNative.RequireSupportedData(handle, "carpeta del trabajo");
        return new(path, WindowsProbeNative.DirectoryIdentity(handle));
    }

    internal static bool Overlap(string first, string second)
    {
        first = Path.TrimEndingDirectorySeparator(first);
        second = Path.TrimEndingDirectorySeparator(second);
        return first.Equals(second, StringComparison.OrdinalIgnoreCase) || Contains(first, second) || Contains(second, first);

        static bool Contains(string parent, string child) =>
            child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        destination.Dispose();
        source.Dispose();
    }
}
