using Microsoft.Win32.SafeHandles;

// The caller supplies synthetic candidates. Resolve the candidate from an open
// volume root, checking and retaining each component before opening files.
internal sealed class DirectoryFence : IDisposable
{
    private readonly Dictionary<string, SafeFileHandle> handles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SafeFileHandle> parents = new(StringComparer.Ordinal);

    public SafeFileHandle Root { get; }

    public DirectoryFence(string root, params string[] files)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The directory fence requires Windows.");
        if (!Path.IsPathFullyQualified(root) || Path.GetFullPath(root) != root)
            throw new ArgumentException("Expected a canonical absolute root.");

        string volume = Path.GetPathRoot(root)!;
        if (!IsLocalVolumeRoot(volume))
            throw new ArgumentException("Expected a local drive or a volume GUID path.");

        try
        {
            handles.Add(volume, WindowsProbeNative.OpenPlainDirectory(volume));
            Root = root.Length == volume.Length ? handles[volume] : PinPath(volume, root[volume.Length..]);

            foreach (string file in files)
            {
                if (!Path.IsPathFullyQualified(file) || Path.GetFullPath(file) != file)
                    throw new ArgumentException("Expected a canonical absolute path.");
                string relative = Path.GetRelativePath(root, file);
                if (relative is "." or ".." || Path.IsPathRooted(relative) ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new ArgumentException("Operation path is outside its synthetic root.");

                CheckName(Path.GetFileName(relative));
                string? parent = Path.GetDirectoryName(relative);
                parents[file] = string.IsNullOrEmpty(parent) ? Root : PinPath(root, parent);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public SafeFileHandle Parent(string file) => parents[file];

    private SafeFileHandle PinPath(string ancestor, string relative)
    {
        string current = ancestor;
        foreach (string name in relative.Split(Path.DirectorySeparatorChar))
        {
            CheckName(name);
            var parent = handles[current];
            current = Path.Combine(current, name);
            if (!handles.ContainsKey(current))
                handles.Add(current, WindowsProbeNative.OpenPlainDirectory(parent, name));
        }
        return handles[current];
    }

    private static bool IsLocalVolumeRoot(string path)
    {
        if (path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
            return true;

        const string prefix = @"\\?\Volume";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.EndsWith('\\') &&
            Guid.TryParseExact(path.AsSpan(prefix.Length, path.Length - prefix.Length - 1), "B", out _);
    }

    internal static void CheckName(string name)
    {
        if (name.Length is 0 or > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("Unsupported name in the synthetic operation path.");
    }

    public void Dispose()
    {
        foreach (var handle in handles.Values.Reverse())
            handle.Dispose();
    }
}
