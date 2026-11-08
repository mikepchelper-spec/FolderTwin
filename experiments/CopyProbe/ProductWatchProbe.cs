internal static class ProductWatchProbe
{
    internal static async Task<int> RunAsync()
    {
        var clock = new ProbeClock();
        var signal = new ProductWatchSignal(clock);
        var quiet = TimeSpan.FromSeconds(2);
        var maximum = TimeSpan.FromSeconds(10);
        Check(signal.TakeDue(quiet, maximum), "Startup did not reconcile.");
        for (int second = 0; second <= 10; second++)
        {
            clock.Tick = second;
            signal.Changed();
            Check(signal.TakeDue(quiet, maximum) == (second == 10), "A sustained burst starved or started early.");
        }
        signal.Changed(); // a notification during the preceding work
        clock.Tick = 12;
        Check(signal.TakeDue(quiet, maximum) && !signal.TakeDue(quiet, maximum), "A trailing change was lost or repeated.");
        signal.RequireReconciliation(failedWatcher: true);
        Check(signal.TakeWatcherFailure() && !signal.TakeWatcherFailure() && signal.TakeDue(quiet, maximum),
            "Watcher failure did not require both recreation and reconciliation.");

        var workspace = LabWorkspace.Create();
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "source")).FullName;
            string sibling = Directory.CreateDirectory(Path.Combine(workspace.Root, "prepared")).FullName;
            File.WriteAllText(Path.Combine(sibling, "file.txt"), "synthetic");
            var binding = LocalJobRoots.InspectSelected(source, writable: false);
            using (signal.Observe(binding))
            {
                Check(signal.TakeDue(quiet, maximum), "Attaching the watcher did not require a fresh reconciliation.");
                Directory.Move(sibling, Path.Combine(source, "arrived"));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!signal.TakeDue(TimeSpan.Zero, maximum))
                    await Task.Delay(20, timeout.Token);
            }
            Directory.Move(source, Path.Combine(workspace.Root, "original"));
            Directory.CreateDirectory(source);
            try
            {
                using var incorrect = signal.Observe(binding);
                throw new InvalidOperationException("The watcher followed a substituted source.");
            }
            catch (InvalidDataException) { }
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }
        Console.WriteLine("PASS continuous signal groups bursts, preserves trailing work, recovers after watcher failure and sees a real subtree move.");
        return 0;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ProbeClock : TimeProvider
    {
        internal long Tick;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Tick;
    }
}
