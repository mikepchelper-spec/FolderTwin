using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

internal static class KopiaOutputProbe
{
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
