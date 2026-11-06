using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace CopyLab;

internal static class ProductHistoryAuditProbe
{
    private sealed record Sample(double Milliseconds, double CpuMilliseconds, ulong Reads, ulong ReadBytes,
        ulong WriteBytes, long AllocatedBytes, int HandleDelta);

    internal static void Run()
    {
        // Two bounded, new polling cases; no catalog-scale repetition or real backups.
        const ulong reserve = 2UL * 1024 * 1024 * 1024;
        const long maxFixtureBytes = 32L * 1024 * 1024;
        var fixture = LabWorkspace.Create();
        using var process = Process.GetCurrentProcess();
        var budget = Stopwatch.StartNew();
        try
        {
            using var held = fixture.OpenRegisteredRoot();
            if (LabWorkspace.AvailableSpace(held.Root) < reserve)
                throw new IOException("History audit requires a 2 GiB free-space reserve.");
            string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
            string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
            string parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "backups")).FullName;
            using var registry = new ProductJobRegistry(settings);
            var job = registry.Create(new("Synthetic polling audit", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, parent));
            var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            ProductRunIntent template;
            using (var first = registry.StartRun(job.Id, () => epoch))
            {
                template = first.Intent;
                _ = first.Finish(ProductRunState.Review, "synthetic.txt", clock: () => epoch,
                    detail: "Synthetic failed-attempt metadata; no backup was created.");
            }
            string runs = Path.Combine(settings, job.Id.ToString("N"), "runs");
            int created = 1;
            long written = 0;
            foreach (int count in new[] { 100, 1000 })
            {
                // Populate valid, checksummed failed attempts in O(n); do not benchmark
                // StartRun's own full-history read while preparing a polling experiment.
                while (created < count)
                {
                    CheckBudget();
                    int sequence = ++created;
                    var intent = template with { Id = Guid.NewGuid(), Sequence = sequence, StartedUtc = epoch.AddSeconds(sequence) };
                    byte[] encodedIntent = Encode(intent);
                    var result = new ProductRunResult(1, SHA256.HashData(encodedIntent), ProductRunState.Review,
                        intent.StartedUtc, null, "synthetic.txt", null, "Synthetic failed-attempt metadata; no backup was created.");
                    byte[] encodedResult = Encode(result);
                    written += encodedIntent.Length + encodedResult.Length;
                    if (written > maxFixtureBytes) throw new IOException("History fixture exceeded its 32 MiB content budget.");
                    string directory = Directory.CreateDirectory(Path.Combine(runs,
                        sequence.ToString("D20", CultureInfo.InvariantCulture) + "-" + intent.Id.ToString("N"))).FullName;
                    File.WriteAllBytes(Path.Combine(directory, "intent"), encodedIntent);
                    File.WriteAllBytes(Path.Combine(directory, "result"), encodedResult);
                }
                CheckHistory(registry.RunHistory(job.Id), count); // Warm metadata/cache before steady samples.
                var samples = new List<Sample>();
                for (int iteration = 0; iteration < 5; iteration++)
                {
                    CheckBudget();
                    process.Refresh();
                    int handles = process.HandleCount;
                    TimeSpan cpu = process.TotalProcessorTime;
                    var before = VersionScaleProbe.ReadCounters(process);
                    long allocated = GC.GetTotalAllocatedBytes(precise: true);
                    var timer = Stopwatch.StartNew();
                    var history = registry.RunHistory(job.Id);
                    timer.Stop();
                    allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
                    var after = VersionScaleProbe.ReadCounters(process);
                    process.Refresh();
                    samples.Add(new(timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpu).TotalMilliseconds,
                        after.ReadOperations - before.ReadOperations, after.ReadBytes - before.ReadBytes,
                        after.WriteBytes - before.WriteBytes, allocated, process.HandleCount - handles));
                    CheckHistory(history, count);
                    if (timer.Elapsed > TimeSpan.FromSeconds(10))
                        throw new IOException("A history query exceeded the ten-second observation budget; no further samples run.");
                }
                process.Refresh();
                Console.Error.WriteLine(JsonSerializer.Serialize(new
                {
                    experiment = "product_history_polling", runs = count, samples = samples.Count,
                    medianMilliseconds = Median(samples.Select(sample => sample.Milliseconds)),
                    medianCpuMilliseconds = Median(samples.Select(sample => sample.CpuMilliseconds)),
                    medianReadOperations = Median(samples.Select(sample => (double)sample.Reads)),
                    medianReadBytes = Median(samples.Select(sample => (double)sample.ReadBytes)),
                    medianWriteBytes = Median(samples.Select(sample => (double)sample.WriteBytes)),
                    medianAllocatedBytes = Median(samples.Select(sample => (double)sample.AllocatedBytes)),
                    maximumSampleHandleDelta = samples.Max(sample => sample.HandleDelta),
                    workingSetBytes = process.WorkingSet64, fixtureContentBytes = written,
                    scope = "warm process counters and metadata reads; not physical-device IO or UI latency"
                }));
            }
            Console.Error.WriteLine("PASS bounded history polling audit preserves all 100/1000 failed-attempt records. No source files were copied, no worker or native schedule started.");

            void CheckBudget()
            {
                if (budget.Elapsed > TimeSpan.FromSeconds(120))
                    throw new IOException("History audit exceeded its two-minute cooperative budget.");
            }
        }
        finally
        {
            string root = Path.GetFullPath(fixture.Root);
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith(LabWorkspace.Prefix, StringComparison.Ordinal))
                throw new IOException("History fixture cleanup target left the owned temporary directory.");
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Encode<T>(T record)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(record);
        return [.. SHA256.HashData(json), .. json];
    }

    private static void CheckHistory(ProductRunHistory history, int count)
    {
        if (history.Unreadable != 0 || history.Runs.Length != count || history.Runs[0].Intent.Sequence != count ||
            history.Runs.Any(run => run.Running || run.Generation is not null || run.Result?.State != ProductRunState.Review))
            throw new InvalidOperationException("History polling lost, misordered or misclassified synthetic attempts.");
    }

    private static double Median(IEnumerable<double> values) => values.Order().ElementAt(2);
}
