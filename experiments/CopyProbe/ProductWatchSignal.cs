internal sealed class ProductWatchSignal(TimeProvider clock, Action? wake = null)
{
    internal const int MaximumEditHints = 16;
    private readonly object gate = new();
    private readonly HashSet<string> edits = new(StringComparer.OrdinalIgnoreCase);
    private long? first;
    private long last;
    private bool reconcile = true;
    private bool wholeBatch;
    private bool watcherFailed;

    internal bool Pending
    {
        get { lock (gate) return reconcile || first is not null; }
    }

    internal void Changed(string? path = null)
    {
        lock (gate)
        {
            last = clock.GetTimestamp();
            first ??= last;
            if (!reconcile && !wholeBatch)
            {
                if (path is null || !ValidEditHint(path) || edits.Add(path) && edits.Count > MaximumEditHints)
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
            if (!reconcile && !wholeBatch && edits.Count != 0) editHints = edits.ToArray();
            edits.Clear();
            wholeBatch = false;
            first = null;
            reconcile = false;
            return true;
        }
    }

    internal static bool ValidEditHints(string[] paths) => paths.Length is > 0 and <= MaximumEditHints &&
        paths.All(ValidEditHint) && paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Length;

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
            watcher.Changed += (_, args) => Changed(args.Name);
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
