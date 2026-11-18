using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

internal static class KopiaOutputProbe
{
    private const int HistoryCount = 10_000;

    internal static async Task RunHistoryAsync()
    {
        var fixture = LabWorkspace.Create();
        bool passed = false;
        try
        {
            string executable = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
            var repository = new KopiaRepository(executable, Path.Combine(fixture.Root, "output-long-history"), "synthetic-history");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            long before = GC.GetTotalAllocatedBytes(precise: true);
            var clock = Stopwatch.StartNew();
            var history = await repository.ListAsync(timeout.Token);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            if (history.Length != HistoryCount || history[0].Point.ManifestId != (HistoryCount - 1).ToString("x32") ||
                history[^1].Point.ManifestId != new string('0', 32))
                throw new InvalidDataException("The remote history did not preserve all distinct dates in order.");
            Console.WriteLine($"PASS remote history dates={history.Length}; seconds={clock.Elapsed.TotalSeconds:F3}; managed_allocations={allocated} bytes.");
            passed = true;
        }
        finally
        {
            if (passed)
            {
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
        }
    }

    internal static int EmitHistory(string[] arguments)
    {
        int configIndex = Array.IndexOf(arguments, "--config-file");
        if (configIndex < 0 || configIndex + 1 >= arguments.Length) return 2;
        string config = Path.GetFullPath(arguments[configIndex + 1]);
        if (Path.GetFileName(config) != "output-long-history") return 2;
        _ = LabWorkspace.Open(Path.GetDirectoryName(config)!);
        string prefix = VersionStore.NewGenerationId()[..24];
        string job = Guid.NewGuid().ToString("N");
        Console.Write('[');
        for (int index = 0; index < HistoryCount; index++)
        {
            if (index != 0) Console.Write(',');
            string id = index.ToString("x32");
            Console.Write(JsonSerializer.Serialize(new
            {
                id,
                endTime = DateTimeOffset.UnixEpoch,
                tags = new Dictionary<string, string> { ["tag:ft-job"] = job, ["tag:ft-generation"] = prefix + id },
                rootEntry = new { type = "d", obj = "k" + id, summ = new { numFailed = 0, symlinks = 0, size = 1, files = 1, dirs = 1 } },
                stats = new { errorCount = 0, ignoredErrorCount = 0, excludedFileCount = 0, excludedDirCount = 0 }
            }));
        }
        Console.Write(']');
        return 0;
    }

    internal static async Task RunAsync(string root)
    {
        string executable = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
        foreach (string mode in new[] { "json", "overflow", "diagnostic", "stderr-flood" })
        {
            string config = Path.Combine(root, "output-" + mode);
            var repository = new KopiaRepository(executable, config, "synthetic-output-password");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(mode == "stderr-flood" ? 2 : 15));
            var timer = Stopwatch.StartNew();
            try
            {
                string output = await repository.RunAsync(timeout.Token, "--cloud-output-child", mode);
                if (mode != "json") throw new InvalidOperationException("Oversized or failed output was accepted.");
                using var document = JsonDocument.Parse(output);
                if (document.RootElement.GetProperty("synthetic").GetInt32() != 42)
                    throw new InvalidDataException("Normal JSON output was changed.");
            }
            catch (InvalidDataException error) when (mode == "overflow" &&
                error.Message.Contains("límite de lectura", StringComparison.Ordinal)) { }
            catch (CloudProviderException error) when (mode == "diagnostic" && error.Issue == CloudTransferIssue.Quota) { }
            catch (OperationCanceledException) when (mode == "stderr-flood" && timeout.IsCancellationRequested) { }
            if (mode != "stderr-flood" && timeout.IsCancellationRequested)
                throw new InvalidDataException("A bounded reader waited for its deadline instead of stopping the child.");
            string marker = config + ".pid";
            int childId = int.Parse(File.ReadAllText(marker), System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                using var child = Process.GetProcessById(childId);
                if (!child.HasExited) throw new InvalidDataException("The synthetic output child survived completion.");
            }
            catch (ArgumentException) { }
            Console.WriteLine($"PASS bounded Kopia output {mode}, child stopped in {timer.Elapsed.TotalSeconds:F3}s.");
        }
    }

    internal static int Emit(string[] arguments)
    {
        int configIndex = Array.IndexOf(arguments, "--config-file");
        if (configIndex < 0 || configIndex + 1 >= arguments.Length)
            throw new ArgumentException("The synthetic child requires its fixture path.");
        string config = Path.GetFullPath(arguments[configIndex + 1]);
        string root = Path.GetDirectoryName(config)!;
        _ = LabWorkspace.Open(root);
        File.WriteAllText(config + ".pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string block = new('x', 4096);
        switch (arguments[^1])
        {
            case "json":
                Console.Write("{\"synthetic\":42}");
                return 0;
            case "overflow":
                while (true) Console.Write(block);
            case "diagnostic":
                // Exercise a large unbroken stderr response and a diagnostic
                // token crossing the reader's 4096-character boundary.
                for (int index = 0; index < 1024; index++) Console.Error.Write(block);
                Console.Error.Write(new string('x', 4090));
                Console.Error.Write("InvalidAccessKeyId");
                Console.Error.Write(new string('x', 4090));
                Console.Error.Write("QuotaExceeded");
                return 1;
            case "stderr-flood":
                while (true) Console.Error.Write(block);
            default:
                throw new ArgumentException("Unknown synthetic output mode.");
        }
    }
}
