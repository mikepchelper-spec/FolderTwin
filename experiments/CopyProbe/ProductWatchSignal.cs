internal sealed class ProductWatchSignal(TimeProvider clock)
{
    private readonly object gate = new();
    private long? first;
    private long last;
    private bool reconcile = true;
    private bool watcherFailed;

    internal void Changed()
    {
        lock (gate)
        {
            last = clock.GetTimestamp();
            first ??= last;
        }
    }

    internal void RequireReconciliation(bool failedWatcher = false)
    {
        lock (gate)
        {
            reconcile = true;
            watcherFailed |= failedWatcher;
        }
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
    internal bool TakeDue(TimeSpan quiet, TimeSpan maximum)
    {
        lock (gate)
        {
            long now = clock.GetTimestamp();
            if (!reconcile && (first is null ||
                clock.GetElapsedTime(last, now) < quiet && clock.GetElapsedTime(first.Value, now) < maximum))
                return false;
            first = null;
            reconcile = false;
            return true;
        }
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
            watcher.Created += (_, _) => Changed();
            watcher.Changed += (_, _) => Changed();
            watcher.Deleted += (_, _) => Changed();
            watcher.Renamed += (_, _) => Changed();
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
