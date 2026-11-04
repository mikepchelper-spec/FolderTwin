using Microsoft.Win32.SafeHandles;

// The caller creates and trusts the synthetic root. Pin each directory below it
// before opening files. This experiment does not establish a root from user input.
internal sealed class DirectoryFence : IDisposable
{
    private readonly Dictionary<string, SafeFileHandle> handles = new(StringComparer.Ordinal);

    public DirectoryFence(string root, params string[] files)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The directory fence requires Windows.");
        if (!Path.IsPathFullyQualified(root) || Path.GetFullPath(root) != root)
            throw new ArgumentException("Expected a canonical absolute root.");
        try
        {
            foreach (string file in files)
            {
                if (!Path.IsPathFullyQualified(file) || Path.GetFullPath(file) != file)
                    throw new ArgumentException("Expected a canonical absolute path.");
                string relative = Path.GetRelativePath(root, file);
                if (relative is "." or ".." || Path.IsPathRooted(relative) ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new ArgumentException("Operation path is outside its synthetic root.");

                string[] names = relative.Split(Path.DirectorySeparatorChar);
                foreach (string name in names)
                    if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
                        throw new ArgumentException("Unsupported name in the synthetic operation path.");

                string current = root;
                Pin(current);
                foreach (string name in names.AsSpan(0, names.Length - 1))
                {
                    var parent = handles[current];
                    current = Path.Combine(current, name);
                    if (!handles.ContainsKey(current))
                        handles.Add(current, WindowsProbeNative.OpenPlainDirectory(parent, name));
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public SafeFileHandle Parent(string file) => handles[Path.GetDirectoryName(file)!];

    private void Pin(string path)
    {
        if (!handles.ContainsKey(path))
            handles.Add(path, WindowsProbeNative.OpenPlainDirectory(path));
    }

    public void Dispose()
    {
        foreach (var handle in handles.Values.Reverse())
            handle.Dispose();
    }
}
