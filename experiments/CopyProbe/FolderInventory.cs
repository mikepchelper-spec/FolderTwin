using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

internal enum InventoryIssueKind { Unavailable, ReparsePoint, Encrypted, UnsupportedName, ReadFailure, EntryLimit, DepthLimit, Cancelled }
internal sealed record InventoryEntry(string RelativePath, bool IsDirectory, long Length);
internal sealed record InventoryIssue(string RelativePath, InventoryIssueKind Kind);
internal sealed record InventoryResult(InventoryEntry[] Entries, InventoryIssue[] Issues)
{
    public bool Complete => Issues.Length == 0;
    public int FileCount => Entries.Count(entry => !entry.IsDirectory);
    public long Bytes => Entries.Where(entry => !entry.IsDirectory).Sum(entry => entry.Length);
}

// Metadata observations, not a snapshot or a promise that file contents can be read.
internal static class FolderInventory
{
    internal static InventoryResult Scan(string root, int maximumEntries = 10_000, int maximumDepth = 64,
        CancellationToken cancellationToken = default, Action<string>? beforeDirectory = null)
    {
        if (maximumEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumDepth < 0 || maximumDepth > 64)
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        // ponytail: retain a bounded list for the lab preview; stream into the local index at production scale.
        var entries = new List<InventoryEntry>();
        var issues = new List<InventoryIssue>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int observedEntries = 0;
        bool stopped = false;
        if (cancellationToken.IsCancellationRequested)
            issues.Add(new("", InventoryIssueKind.Cancelled));
        else
            try
            {
                using var fence = new DirectoryFence(root);
                Visit(fence.Root, "", 0);
            }
            catch (Exception error) when (IsReadFailure(error))
            {
                issues.Add(new("", InventoryIssueKind.ReadFailure));
            }
        return new(entries.ToArray(), issues.ToArray());

        void Visit(SafeFileHandle directory, string relativeDirectory, int depth)
        {
            try
            {
                beforeDirectory?.Invoke(relativeDirectory);
                if (Cancelled(relativeDirectory))
                    return;
                foreach (var entry in WindowsProbeNative.ReadDirectory(directory))
                {
                    if (Cancelled(relativeDirectory) || stopped)
                        return;
                    if (entry.Name is "." or "..")
                        continue;
                    if (observedEntries == maximumEntries)
                    {
                        issues.Add(new(relativeDirectory, InventoryIssueKind.EntryLimit));
                        stopped = true;
                        return;
                    }
                    observedEntries++;
                    string relative = Path.Combine(relativeDirectory, entry.Name);
                    try
                    {
                        DirectoryFence.CheckName(entry.Name);
                    }
                    catch (ArgumentException)
                    {
                        issues.Add(new(relative, InventoryIssueKind.UnsupportedName));
                        continue;
                    }
                    if (!names.Add(relative))
                    {
                        issues.Add(new(relative, InventoryIssueKind.UnsupportedName));
                        stopped = true;
                        return;
                    }
                    if (Omission(entry.Attributes) is { } omission)
                    {
                        issues.Add(new(relative, omission));
                        continue;
                    }
                    bool isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                    entries.Add(new(relative, isDirectory, isDirectory ? 0 : entry.Length));
                    if (!isDirectory)
                        continue;
                    if (depth == maximumDepth)
                    {
                        issues.Add(new(relative, InventoryIssueKind.DepthLimit));
                        continue;
                    }
                    try
                    {
                        using var child = WindowsProbeNative.OpenPlainDirectory(directory, entry.Name);
                        Visit(child, relative, depth + 1);
                    }
                    catch (Exception error) when (IsReadFailure(error))
                    {
                        issues.Add(new(relative, InventoryIssueKind.ReadFailure));
                    }
                }
            }
            catch (Exception error) when (IsReadFailure(error))
            {
                issues.Add(new(relativeDirectory, InventoryIssueKind.ReadFailure));
            }
        }

        bool Cancelled(string relative)
        {
            if (!cancellationToken.IsCancellationRequested)
                return false;
            if (!stopped)
                issues.Add(new(relative, InventoryIssueKind.Cancelled));
            stopped = true;
            return true;
        }
    }

    internal static InventoryIssueKind? Omission(FileAttributes attributes)
    {
        const FileAttributes recall = (FileAttributes)(0x00040000 | 0x00400000);
        if ((attributes & (FileAttributes.Offline | recall)) != 0)
            return InventoryIssueKind.Unavailable;
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            return InventoryIssueKind.ReparsePoint;
        if (attributes.HasFlag(FileAttributes.Encrypted))
            return InventoryIssueKind.Encrypted;
        return null;
    }

    private static bool IsReadFailure(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception;
}
