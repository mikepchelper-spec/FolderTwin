using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

internal static class StorageGrowthProbe
{
    private sealed record Sample(ProductJob Job, string Source);
    private const string Group = "group-0000";

    internal static async Task RunAsync(string executable, int files)
    {
        var sourceRoot = LabWorkspace.Create();
        var destination = LabWorkspace.CreateOnFixedVolume(@"D:\");
        Process? monitor = null;
        bool passed = false;
        try
        {
            if (Path.GetPathRoot(sourceRoot.Root) != @"C:\") throw new InvalidDataException("The fixture source must be on C:.");
            foreach (var workspace in new[] { sourceRoot, destination })
            {
                using var root = workspace.OpenRegisteredRoot();
                LabWorkspace.RequireSpace(1024L * 1024 * 1024 + 24L * 1024 * files, LabWorkspace.AvailableSpace(root.Root));
            }
            string settings = Directory.CreateDirectory(Path.Combine(sourceRoot.Root, "settings")).FullName;
            int reconcile = files == 4096 ? 60 : 180;
            File.WriteAllText(Path.Combine(settings, "monitor-timing.json"),
                FormattableString.Invariant($$"""{"QuietSeconds":2,"MaximumWaitSeconds":20,"ReconcileSeconds":{{reconcile}}}"""));
            using var registry = new ProductJobRegistry(settings);
            var modes = files == 4096 ? Enum.GetValues<ProductBackupMode>() : [ProductBackupMode.Versions];
            var samples = new List<Sample>();
            foreach (var mode in modes)
            {
                string source = Directory.CreateDirectory(Path.Combine(sourceRoot.Root, mode.ToString())).FullName;
                Console.WriteLine($"Prepare {mode}: {files} files on C:; dedicated destination on D:.");
                DailyPerformanceProbe.Seed(source, files);
                var job = registry.Create(new($"Synthetic growth {mode}", mode, null), LocalJobRoots.ReviewDedicatedDestination(source, destination.Root));
                registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching,
                    authorizeMirror: mode == ProductBackupMode.ProtectedMirror);
                if (mode != ProductBackupMode.Versions) DailyPerformanceProbe.PrepareCurrentTree(job, source);
                samples.Add(new(job, source));
            }

            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--watch-product-jobs");
            start.ArgumentList.Add(settings);
            DateTimeOffset started = DateTimeOffset.UtcNow;
            monitor = Process.Start(start) ?? throw new InvalidOperationException("The packaged monitor did not start.");
            Console.WriteLine($"Monitor PID {monitor.Id}, jobs={samples.Count}, periodic_seconds={reconcile}.");
            var sequences = samples.ToDictionary(sample => sample.Job.Id, _ => 0L);
            await Observe("initial", started, sample => sample.Job.Options.Mode == ProductBackupMode.Versions
                ? ProductRunState.Confirmed : ProductRunState.Unchanged);
            var baseline = Snapshot("initial");
            await Observe("idle", DateTimeOffset.UtcNow, _ => ProductRunState.Unchanged);
            var idle = Snapshot("idle");
            if (samples.Any(sample => idle[sample.Job.Id] != baseline[sample.Job.Id]))
                throw new InvalidDataException("Idle reconciliation added a confirmed point.");

            await Change("edit", sample =>
            {
                string path = At(sample, "file-000000.txt");
                DateTime timestamp = File.GetLastWriteTimeUtc(path);
                File.WriteAllText(path, "synthetic XX");
                File.SetLastWriteTimeUtc(path, timestamp);
            });
            await Change("temporary", sample =>
            {
                File.WriteAllText(At(sample, "saving.tmp"), "synthetic editor save");
                File.Move(At(sample, "saving.tmp"), At(sample, "file-000001.txt"), overwrite: true);
            });
            await Change("create", sample => File.WriteAllText(At(sample, "new.txt"), "synthetic addition"));
            await Change("rename", sample => File.Move(At(sample, "file-000002.txt"), At(sample, "renamed.txt")));
            await Change("delete", sample => File.Delete(At(sample, "file-000003.txt")), sample =>
                sample.Job.Options.Mode == ProductBackupMode.Accumulative ? ProductRunState.Unchanged : ProductRunState.Confirmed);
            await WaitIdle();
            var burst = DateTimeOffset.UtcNow;
            for (int save = 0; save < 8; save++)
            {
                foreach (var sample in samples)
                {
                    File.WriteAllText(At(sample, "burst.tmp"), $"burst save {save}");
                    File.Move(At(sample, "burst.tmp"), At(sample, "file-000004.txt"), overwrite: true);
                }
                await Task.Delay(180);
            }
            await Observe("burst", burst, _ => ProductRunState.Confirmed);
            Snapshot("burst");
            foreach (var sample in samples)
                registry.UpdateSettings(sample.Job.Id, sample.Job.Options.Name, null, true, false, ProductWatchMode.Paused);
            using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20))) await monitor.WaitForExitAsync(stop.Token);
            if (monitor.ExitCode != 0) throw new InvalidDataException("The packaged monitor did not stop normally.");

            foreach (var sample in samples)
            {
                var job = sample.Job;
                var latest = registry.RunHistory(job.Id).Runs.First(run => run.Result?.State == ProductRunState.Confirmed);
                string restored = Directory.CreateDirectory(Path.Combine(sourceRoot.Root, $"restored-{job.Options.Mode}")).FullName;
                Directory.Move(sample.Source, sample.Source + "-apart");
                string relative = Path.Combine(Group, "file-000004.txt");
                VersionStore.OpenProduct(job).Restore(latest.Generation!, restored, relative);
                byte[] actual = SHA256.HashData(File.ReadAllBytes(Path.Combine(restored, relative)));
                byte[] expected = SHA256.HashData("burst save 7"u8);
                if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("The burst did not restore its last saved bytes.");
                Console.WriteLine($"restore mode={job.Options.Mode} sha256={Convert.ToHexStringLower(actual)}");
            }
            passed = true;

            async Task Change(string phase, Action<Sample> change, Func<Sample, ProductRunState>? expected = null)
            {
                await WaitIdle();
                DateTimeOffset changed = DateTimeOffset.UtcNow;
                foreach (var sample in samples) change(sample);
                await Observe(phase, changed, expected ?? (_ => ProductRunState.Confirmed));
                Snapshot(phase);
            }

            async Task Observe(string phase, DateTimeOffset changed, Func<Sample, ProductRunState> expected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                var pending = samples.ToList();
                while (pending.Count > 0)
                {
                    if (monitor.HasExited) throw new InvalidOperationException("The packaged monitor exited before the sample.");
                    foreach (var sample in pending.ToArray())
                    {
                        var job = sample.Job;
                        var runs = registry.RunHistory(job.Id).Runs.Reverse().Where(run => run.Intent.Sequence > sequences[job.Id] && run.Result is not null).ToArray();
                        foreach (var run in runs)
                        {
                            var result = run.Result!;
                            sequences[job.Id] = run.Intent.Sequence;
                            if (result.State is not (ProductRunState.Confirmed or ProductRunState.Unchanged))
                                throw new InvalidDataException($"{phase}/{job.Options.Mode}: {result}");
                            if (run.Intent.StartedUtc < changed || result.State != expected(sample)) continue;
                            Console.WriteLine(FormattableString.Invariant($"run phase={phase} mode={job.Options.Mode} state={result.State} scoped={result.Scoped} dispatch_s={(run.Intent.StartedUtc - changed).TotalSeconds:F3} confirm_s={(result.FinishedUtc - changed).TotalSeconds:F3}"));
                            pending.Remove(sample);
                            break;
                        }
                    }
                    if (pending.Count > 0) await Task.Delay(250, timeout.Token);
                }
                await WaitIdle();
            }

            async Task WaitIdle()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
                while (true)
                {
                    if (monitor.HasExited) throw new InvalidOperationException("The packaged monitor exited before returning to idle.");
                    bool idle = true;
                    foreach (var sample in samples)
                    {
                        registry.ReadMonitorProcess(sample.Job.Id, out var state);
                        idle &= state == ProductWatchActivity.Watching;
                    }
                    if (idle) return;
                    await Task.Delay(200, timeout.Token);
                }
            }

            Dictionary<Guid, int> Snapshot(string phase)
            {
                var counts = new Dictionary<Guid, int>();
                foreach (var sample in samples)
                {
                    var job = sample.Job;
                    var footprint = ReadFootprint(job.Destination.LastVolumePath);
                    string history = Path.Combine(settings, job.Id.ToString("N"), "runs");
                    footprint["history"] = ReadFiles(history);
                    int points = Directory.EnumerateFiles(Path.Combine(job.Destination.LastVolumePath, "manifests"), "*.jsonl").Count();
                    counts.Add(job.Id, points);
                    Console.WriteLine($"footprint phase={phase} mode={job.Options.Mode} points={points} attempts={registry.RunHistory(job.Id).Runs.Length}");
                    foreach (var (category, bytes) in footprint.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                        Console.WriteLine($"bytes category={category} files={bytes.Files} logical={bytes.Logical} allocated={bytes.Allocated}");
                }
                return counts;
            }
        }
        finally
        {
            if (monitor is not null)
            {
                if (!monitor.HasExited) { monitor.Kill(entireProcessTree: true); await monitor.WaitForExitAsync(); }
                monitor.Dispose();
            }
            if (passed)
            {
                RemoveWorkspace(destination, @"D:\");
                RemoveWorkspace(sourceRoot, Path.TrimEndingDirectorySeparator(Path.GetTempPath()));
            }
            else Console.Error.WriteLine($"Synthetic fixtures preserved: {sourceRoot.Root}, {destination.Root}.");
        }
    }

    private static string At(Sample sample, string name) => Path.Combine(sample.Source, Group, name);

    internal static void RemoveWorkspace(LabWorkspace workspace, string parent)
    {
        string root = Path.GetFullPath(workspace.Root);
        if (!string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(root).StartsWith(LabWorkspace.Prefix, StringComparison.Ordinal))
            throw new IOException("The cleanup target left the owned workspace parent.");
        using (workspace.OpenRegisteredRoot()) { }
        Directory.Delete(root, recursive: true);
    }

    internal static Dictionary<string, (int Files, long Logical, long Allocated)> ReadFootprint(string root)
    {
        var totals = new Dictionary<string, (int Files, long Logical, long Allocated)>(StringComparer.Ordinal);
        foreach (string category in new[] { "current", "objects", "catalogs", "replicas", "journals", "quarantine" }) totals[category] = default;
        foreach (var file in Files(root))
        {
            string relative = Path.GetRelativePath(root, file);
            string category = relative.Split(Path.DirectorySeparatorChar, 2)[0] switch
            {
                "manifests" => "catalogs",
                "manifests_backup" => "replicas",
                "quarantine" => "quarantine",
                "objects" when Path.GetFileName(file).Length == 64 => "objects",
                "mirror" when relative.StartsWith(@"mirror\current\", StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(file).StartsWith(".foldertwin-", StringComparison.OrdinalIgnoreCase) => "current",
                _ => "journals"
            };
            totals[category] = Add(totals[category], FileSizes(file));
        }
        return totals;
    }

    private static (int Files, long Logical, long Allocated) ReadFiles(string root) => Files(root)
        .Aggregate(default((int Files, long Logical, long Allocated)), (total, path) => Add(total, FileSizes(path)));

    private static IEnumerable<string> Files(string root) => Directory.EnumerateFiles(root, "*", new EnumerationOptions
    {
        RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint
    });

    private static (int Files, long Logical, long Allocated) Add((int Files, long Logical, long Allocated) total, FileStandardInfo file) =>
        (total.Files + 1, total.Logical + file.EndOfFile, total.Allocated + file.AllocationSize);

    private static FileStandardInfo FileSizes(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandleEx(handle, 1, out var info, (uint)Marshal.SizeOf<FileStandardInfo>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (info.NumberOfLinks != 1) throw new InvalidDataException("The growth fixture contains a multiply linked file.");
        return info;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileStandardInfo info, uint size);
}
