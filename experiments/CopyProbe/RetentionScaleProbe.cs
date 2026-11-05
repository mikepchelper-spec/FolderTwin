using System.Diagnostics;
using System.Globalization;

internal static class RetentionScaleProbe
{
    internal static int Run(bool large = false)
    {
        if (large && (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted"))
            throw new InvalidOperationException("The large retention probe requires a disposable hosted Windows runner.");
        int fileCount = large ? 329_000 : 4096, generations = large ? 3 : 8, kept = large ? 2 : 3;
        const int filesPerDirectory = 256;
        var workspace = LabWorkspace.Create();
        using var process = Process.GetCurrentProcess();
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "source")).FullName;
            string destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "store")).FullName;
            using (var scope = workspace.OpenRegisteredRoot())
                LabWorkspace.RequireSpace(256L * 1024 * 1024 + 12L * 1024 * fileCount, LabWorkspace.AvailableSpace(scope.Root));
            Console.WriteLine($"Windows {Environment.OSVersion.Version}; .NET {Environment.Version}; {generations} generations with {fileCount} files each.");
            string firstFile = Path.Combine(source, "group 0000", "file 000000.txt");
            Measure("retention_seed", () =>
            {
                for (int start = 0; start < fileCount; start += filesPerDirectory)
                {
                    string directory = Directory.CreateDirectory(Path.Combine(source, $"group {start / filesPerDirectory:D4}")).FullName;
                    for (int index = start; index < Math.Min(fileCount, start + filesPerDirectory); index++)
                        File.WriteAllText(Path.Combine(directory, $"file {index:D6}.txt"), (index % 32).ToString(CultureInfo.InvariantCulture));
                }
            });
            var store = VersionStore.Initialize(workspace, destination);
            DateTimeOffset reference = DateTimeOffset.UtcNow;
            for (int index = 0; index < generations; index++)
            {
                File.WriteAllText(firstFile, "generation " + index);
                string point = reference.AddDays(index - generations + 1)
                    .ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                Measure("retention_backup_" + (index + 1), () => store.CreatePoint(source, point));
            }
            Directory.Move(source, Path.Combine(workspace.Root, "source absent"));
            RetentionPreview review = null!;
            Measure("retention_preview", () => review = store.PreviewRetention(new(kept, 0, 0, 1), clock: reference));
            int expired = generations - kept;
            if (review.Keep.Length != kept || review.Expire.Length != expired || review.Objects.Length != expired)
                throw new InvalidOperationException("The scale reference union is incorrect.");
            string operation = "";
            Measure("retention_quarantine", () => operation = store.BeginRetention(review));
            Measure("retention_purge", () => store.PurgeRetention(operation, clock: DateTimeOffset.UtcNow.AddDays(2)));
            foreach (string retained in review.Keep)
                if (store.Verify(retained)) throw new InvalidOperationException("A retained scale point lost a replica.");
            Console.WriteLine($"PASS retention across {generations} generations, {fileCount} files each; {kept} kept, {expired} expired, {expired} unshared objects purged; source absent.");
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
