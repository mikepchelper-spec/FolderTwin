using System.IO;

namespace CopyLab;

internal enum VersionChange { None, Added, Modified, Removed }

internal sealed record VersionEntry(string Path, bool IsDirectory, VersionChange Change)
{
    public string Label => System.IO.Path.GetFileName(Path) + (IsDirectory ? " · Carpeta" : "") + (Change switch
    {
        VersionChange.Added => " · Añadido",
        VersionChange.Modified => " · Modificado",
        VersionChange.Removed => " · Eliminado",
        _ => ""
    });
}

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
        .Where(entry => onlyChanges ? entry.Change != VersionChange.None || entry.IsDirectory && changedFolders.Contains(entry.Path) :
            entry.Change != VersionChange.Removed)
        .OrderByDescending(entry => entry.IsDirectory)
        .ThenBy(entry => entry.Path, StringComparer.Ordinal)
        .ToArray();

    internal static string Parent(string path) => Path.GetDirectoryName(path) ?? "";
}
