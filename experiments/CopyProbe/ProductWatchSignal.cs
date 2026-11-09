internal sealed class ProductWatchSignal(TimeProvider clock, Action? wake = null)
{
    internal const int MaximumEditHints = 16;
    private readonly object gate = new();
    private readonly Dictionary<string, bool> edits = new(StringComparer.OrdinalIgnoreCase);
    private long? first;
    private long last;
    private bool reconcile = true;
    private bool wholeBatch;
    private bool watcherFailed;

    internal bool Pending
    {
        get { lock (gate) return reconcile || first is not null; }
    }

    internal void Changed(string? path = null, bool metadataOnly = false)
    {
        lock (gate)
        {
            last = clock.GetTimestamp();
            first ??= last;
            if (!reconcile && !wholeBatch)
            {
                bool valid = path is not null && ValidEditHint(path);
                if (valid)
                {
                    // A name event takes precedence over Changed for the same path.
                    if (!edits.TryGetValue(path!, out bool nameEvent) || !nameEvent) edits[path!] = !metadataOnly;
                }
                if (!valid || edits.Count > MaximumEditHints)
                {
                    wholeBatch = true;
                    edits.Clear();
                }
            }
        }
        wake?.Invoke();
    }

    internal void RequireReconciliation(bool failedWatcher = false)
    {
        lock (gate)
        {
            reconcile = true;
            edits.Clear();
            watcherFailed |= failedWatcher;
        }
        wake?.Invoke();
    }

    internal bool TakeWatcherFailure()
    {
        lock (gate)
        {
            bool failed = watcherFailed;
            watcherFailed = false;
            return failed;
        }
    }

    // Consume before starting work. Callbacks that arrive during that work start
    // another batch, even if the current attempt ends successfully.
    internal bool TakeDue(TimeSpan quiet, TimeSpan maximum) => TakeDue(quiet, maximum, out _);

    internal bool TakeDue(TimeSpan quiet, TimeSpan maximum, out string[]? editHints)
    {
        lock (gate)
        {
            long now = clock.GetTimestamp();
            editHints = null;
            if (!reconcile && (first is null ||
                clock.GetElapsedTime(last, now) < quiet && clock.GetElapsedTime(first.Value, now) < maximum))
                return false;
            if (!reconcile && !wholeBatch && edits.Count != 0)
                editHints = edits.Select(edit => edit.Value ? edit.Key : "?" + edit.Key).ToArray();
            edits.Clear();
            wholeBatch = false;
            first = null;
            reconcile = false;
            return true;
        }
    }

    internal static bool MetadataHint(string hint) => hint.StartsWith('?');

    internal static string HintPath(string hint) => MetadataHint(hint) ? hint[1..] : hint;

    internal static bool ValidEditHints(string[] hints) => hints.Length is > 0 and <= MaximumEditHints &&
        hints.Select(HintPath).All(ValidEditHint) && hints.Select(HintPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == hints.Length;

    private static bool ValidEditHint(string path)
    {
        if (path.Length is 0 or > 240 || Path.IsPathRooted(path) || path.Contains('/') || path.Contains(':')) return false;
        foreach (var name in path.Split(Path.DirectorySeparatorChar))
        {
            if (name is "." or ".." || name.StartsWith(FolderBatch.ControlPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            try { DirectoryFence.CheckName(name); }
            catch (ArgumentException) { return false; }
        }
        return true;
    }

    internal FileSystemWatcher Observe(LocalJobRoot source)
    {
        using var root = new DirectoryFence(source.VolumePath);
        if (!WindowsProbeNative.DirectoryIdentity(root.Root).AsSpan().SequenceEqual(source.Identity))
            throw new InvalidDataException("La carpeta de origen fue sustituida.");
        var watcher = new FileSystemWatcher(source.VolumePath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                NotifyFilters.Size | NotifyFilters.Attributes
        };
        try
        {
            watcher.Created += (_, args) => Changed(args.Name);
            watcher.Changed += (_, args) => Changed(args.Name, metadataOnly: true);
            watcher.Deleted += (_, args) => Changed(args.Name);
            watcher.Renamed += (_, args) => { Changed(args.OldName); Changed(args.Name); };
            watcher.Error += (_, _) => RequireReconciliation(failedWatcher: true);
            watcher.EnableRaisingEvents = true;
            RequireReconciliation();
            return watcher;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }
}
