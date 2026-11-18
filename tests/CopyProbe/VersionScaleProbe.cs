using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class VersionScaleProbe
{
    private const int FilesPerDirectory = 256;

    internal static int Run(int fileCount, int objectCount)
    {
        var workspace = LabWorkspace.Create();
        using var process = Process.GetCurrentProcess();
        try
        {
            using (var scope = workspace.OpenRegisteredRoot())
                LabWorkspace.RequireSpace(256L * 1024 * 1024 + 12L * 1024 * fileCount,
                    LabWorkspace.AvailableSpace(scope.Root));
            string source = MakeDirectory("origen");
            string target = MakeDirectory("versiones");
            string output = MakeDirectory("recuperado");
            int directoryCount = (fileCount + FilesPerDirectory - 1) / FilesPerDirectory;
            Console.WriteLine(FormattableString.Invariant(
                $"Windows {Environment.OSVersion.Version}; .NET {Environment.Version}; {fileCount} files, {directoryCount} directories, {objectCount} distinct eight-byte payloads."));
            Measure("seed", () =>
            {
                Span<byte> payload = stackalloc byte[sizeof(long)];
                for (int start = 0; start < fileCount; start += FilesPerDirectory)
                {
                    string directory = Directory.CreateDirectory(Path.Combine(source, DirectoryName(start))).FullName;
                    for (int index = start; index < Math.Min(start + FilesPerDirectory, fileCount); index++)
                    {
                        BinaryPrimitives.WriteInt64LittleEndian(payload, index % objectCount);
                        using var stream = File.Create(Path.Combine(directory, FileName(index)));
                        stream.Write(payload);
                    }
                }
            });
            Measure("inventory", () =>
            {
                var inventory = FolderInventory.Scan(source, maximumEntries: fileCount + directoryCount);
                if (!inventory.Complete || inventory.Entries.Length != fileCount + directoryCount)
                    throw new InvalidOperationException("The synthetic inventory did not match the fixture.");
            });
            var store = VersionStore.Initialize(workspace, target);
            string generation = VersionStore.NewGenerationId();
            Measure("backup", () => store.CreatePoint(source, generation));
            Directory.Move(source, Path.Combine(workspace.Root, "origen apartado"));
            store = VersionStore.Open(LabWorkspace.Open(workspace.Root), target);
            Measure("list", () =>
            {
                var point = store.ListPoints().Single();
                if (point.Id != generation || point.Problem is not null || point.FileCount != fileCount)
                    throw new InvalidOperationException("The confirmed generation did not contain the expected files.");
            });
            Measure("verify", () =>
            {
                if (store.Verify(generation))
                    throw new InvalidOperationException("The confirmed generation unexpectedly lost a replica.");
            });
            int last = fileCount - 1;
            string sample = Path.Combine(DirectoryName(last), FileName(last));
            Measure("restore_one", () => store.Restore(generation, output, sample));
            if (BinaryPrimitives.ReadInt64LittleEndian(File.ReadAllBytes(Path.Combine(output, sample))) != last % objectCount)
                throw new InvalidOperationException("The restored sample did not contain the expected bytes.");

            string buckets = Path.Combine(target, "objects", "sha256");
            var counts = Directory.EnumerateDirectories(buckets).Select(bucket => Directory.EnumerateFiles(bucket)
                .Count(path => Path.GetFileName(path).Length == 64)).Order().ToArray();
            if (counts.Sum() != objectCount)
                throw new InvalidOperationException("The object store did not deduplicate as expected.");
            long manifestBytes = new FileInfo(Path.Combine(target, "manifests", generation + ".jsonl")).Length;
            Console.WriteLine(FormattableString.Invariant(
                $"PASS manifest_bytes={manifestBytes}; objects={counts.Sum()}; occupied_buckets={counts.Length}; median_occupied={counts[counts.Length / 2]}; max_bucket={counts[^1]}."));
            return 0;
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true); // This test created the entire registered workspace.
        }

        string MakeDirectory(string name) => Directory.CreateDirectory(Path.Combine(workspace.Root, name)).FullName;

        void Measure(string phase, Action operation)
        {
            var before = ReadCounters(process);
            var timer = Stopwatch.StartNew();
            operation();
            timer.Stop();
            var after = ReadCounters(process);
            process.Refresh();
            Console.WriteLine(FormattableString.Invariant(
                $"{phase}: seconds={timer.Elapsed.TotalSeconds:F3}; read_bytes={after.ReadBytes - before.ReadBytes}; write_bytes={after.WriteBytes - before.WriteBytes}; peak_working_set_bytes={process.PeakWorkingSet64}."));
        }
    }

    private static string DirectoryName(int index) => FormattableString.Invariant($"grupo {index / FilesPerDirectory:D4}");
    private static string FileName(int index) => FormattableString.Invariant($"dato ñ {index:D6}.bin");

    internal static IoCounters ReadCounters(Process process) =>
        GetProcessIoCounters(process.Handle, out var counters) ? counters : throw new Win32Exception(Marshal.GetLastPInvokeError());

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint process, out IoCounters counters);
}
