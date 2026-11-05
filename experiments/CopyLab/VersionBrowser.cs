using System.IO;

namespace CopyLab;

internal enum VersionChange { None, Added, Modified, Removed }

internal sealed record VersionEntry(string Path, bool IsDirectory, VersionChange Change)
{
    public string Label => System.IO.Path.GetFileName(Path) + Suffix;
    public string FullLabel => Path + Suffix;
    private string Suffix => (IsDirectory ? " · Carpeta" : "") + (Change switch
    {
        VersionChange.Added => " · Añadido",
        VersionChange.Modified => " · Modificado",
        VersionChange.Removed => " · Eliminado",
        _ => ""
    });
}

internal sealed record VersionPage(VersionEntry[] Entries, bool HasMore);

internal sealed class VersionBrowser
{
    private readonly ILookup<string, VersionEntry> children;
    private readonly HashSet<string> changedFolders = new(StringComparer.Ordinal);

    internal VersionBrowser(CatalogSnapshot current, CatalogSnapshot? previous)
    {
        var beforeFiles = previous?.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal) ?? [];
        var beforeDirectories = previous?.Directories.ToHashSet(StringComparer.Ordinal) ?? [];
        var entries = new List<VersionEntry>();
        foreach (var file in current.Files)
        {
            VersionChange change = VersionChange.None;
            if (previous is not null)
                change = !beforeFiles.Remove(file.RelativePath, out var prior) ? VersionChange.Added :
                    prior.Length == file.Length && prior.Hash.AsSpan().SequenceEqual(file.Hash) && prior.Metadata == file.Metadata
                        ? VersionChange.None : VersionChange.Modified;
            entries.Add(new(file.RelativePath, false, change));
        }
        foreach (string directory in current.Directories)
            entries.Add(new(directory, true, previous is not null && !beforeDirectories.Remove(directory) ?
                VersionChange.Added : VersionChange.None));
        entries.AddRange(beforeFiles.Keys.Select(path => new VersionEntry(path, false, VersionChange.Removed)));
        entries.AddRange(beforeDirectories.Select(path => new VersionEntry(path, true, VersionChange.Removed)));
        children = entries.ToLookup(entry => Parent(entry.Path), StringComparer.Ordinal);
        foreach (var entry in entries.Where(entry => entry.Change != VersionChange.None))
            for (string folder = Parent(entry.Path); folder.Length != 0 && changedFolders.Add(folder); folder = Parent(folder)) { }
    }

    internal VersionEntry[] Children(string folder, bool onlyChanges) => children[folder]
        .Where(entry => onlyChanges ? ShowChange(entry) : entry.Change != VersionChange.Removed)
        .OrderByDescending(entry => entry.IsDirectory)
        .ThenBy(entry => entry.Path, StringComparer.Ordinal)
        .ToArray();

    internal IEnumerable<(VersionEntry Entry, bool ShowChange)> CacheRows() =>
        children.SelectMany(folder => folder).Select(entry => (entry, ShowChange(entry)));

    internal VersionPage Page(string folder, bool onlyChanges, string query, int offset, int count)
    {
        VersionIndex.CheckPage(offset, count);
        var entries = (query.Length == 0 ? children[folder] : children.SelectMany(group => group))
            .Where(entry => (onlyChanges ? ShowChange(entry) : query.Length != 0 || entry.Change != VersionChange.Removed) &&
                entry.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .Skip(offset).Take(count + 1).ToArray();
        return new(entries.Take(count).ToArray(), entries.Length > count);
    }

    private bool ShowChange(VersionEntry entry) =>
        entry.Change != VersionChange.None || entry.IsDirectory && changedFolders.Contains(entry.Path);

    internal static string Parent(string path) => Path.GetDirectoryName(path) ?? "";
}
