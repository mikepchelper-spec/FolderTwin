using System.Diagnostics;
using System.Globalization;

internal static class RetentionScaleProbe
{
    internal static int Run()
    {
        const int fileCount = 4096, generations = 8;
        var workspace = LabWorkspace.Create();
        using var process = Process.GetCurrentProcess();
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "source")).FullName;
            string destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "store")).FullName;
            for (int index = 0; index < fileCount; index++)
                File.WriteAllText(Path.Combine(source, $"file {index:D5}.txt"), (index % 32).ToString(CultureInfo.InvariantCulture));
            var store = VersionStore.Initialize(workspace, destination);
            for (int index = 0; index < generations; index++)
            {
                File.WriteAllText(Path.Combine(source, "file 00000.txt"), "generation " + index);
                string point = DateTimeOffset.UtcNow.AddDays(index - generations + 1)
                    .ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                store.CreatePoint(source, point);
            }
            Directory.Move(source, Path.Combine(workspace.Root, "source absent"));
            RetentionPreview review = null!;
            Measure("retention_preview", () => review = store.PreviewRetention(new(3, 0, 0, 1)));
            if (review.Keep.Length != 3 || review.Expire.Length != 5 || review.Objects.Length != 5)
                throw new InvalidOperationException("The scale reference union is incorrect.");
            string operation = "";
            Measure("retention_quarantine", () => operation = store.BeginRetention(review));
            Measure("retention_purge", () => store.PurgeRetention(operation, clock: DateTimeOffset.UtcNow.AddDays(2)));
            foreach (string retained in review.Keep)
                if (store.Verify(retained)) throw new InvalidOperationException("A retained scale point lost a replica.");
            Console.WriteLine($"PASS retention across {generations} generations, {fileCount} files each; 3 kept, 5 expired, 5 unshared objects purged; source absent.");
            return 0;
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }

        void Measure(string phase, Action action)
        {
            var before = VersionScaleProbe.ReadCounters(process);
            TimeSpan cpu = process.TotalProcessorTime;
            var timer = Stopwatch.StartNew();
            action();
            timer.Stop();
            var after = VersionScaleProbe.ReadCounters(process);
            process.Refresh();
            Console.WriteLine(FormattableString.Invariant($"{phase}: seconds={timer.Elapsed.TotalSeconds:F3}; cpu_seconds={(process.TotalProcessorTime - cpu).TotalSeconds:F3}; read_bytes={after.ReadBytes - before.ReadBytes}; write_bytes={after.WriteBytes - before.WriteBytes}; peak_working_set_bytes={process.PeakWorkingSet64}."));
        }
    }
}
