using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class TransportProbe
{
    private enum Transport { DotNet, CopyFile2, Robocopy }

    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The transport comparison requires Windows.");

        string root = Directory.CreateTempSubdirectory("FolderTwin-Transport-").FullName;
        string source = Path.Combine(root, "source");
        string staging = Path.Combine(root, "staging");
        string destination = Path.Combine(root, "destination");
        string temporary = Path.Combine(staging, "pending.tmp");
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var random = new Random(42);
        int totalBytes = 0;
        try
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(destination);
            for (int i = 0; i < 24; i++)
                AddFile($"archivo ñ {i:D2}.bin", 4_096 + i);
            AddFile("vacío.bin", 0);
            AddFile("grande.bin", 8 * 1024 * 1024 + 17);
            Console.WriteLine($"{hashes.Count} synthetic files, {totalBytes} bytes; three rounds with rotated order.");

            Transport[] transports = Enum.GetValues<Transport>();
            var times = transports.ToDictionary(transport => transport, _ => new List<double>());
            for (int round = 0; round < transports.Length; round++)
            {
                for (int offset = 0; offset < transports.Length; offset++)
                {
                    Transport transport = transports[(round + offset) % transports.Length];
                    var timer = Stopwatch.StartNew();
                    Copy(transport, source, staging, destination, temporary, hashes);
                    timer.Stop();
                    VerifyDirectory(destination, hashes);
                    VerifyDirectory(source, hashes);
                    times[transport].Add(timer.Elapsed.TotalMilliseconds);
                    Console.WriteLine(FormattableString.Invariant(
                        $"PASS round {round + 1}: {transport}, {timer.Elapsed.TotalMilliseconds:F1} ms"));
                    ClearOutputs();
                }
            }

            string sample = "archivo ñ 00.bin";
            var single = new Dictionary<string, byte[]> { [sample] = hashes[sample] };
            foreach (Transport transport in transports)
            {
                double median = times[transport].Order().ElementAt(1);
                Console.WriteLine(FormattableString.Invariant($"{transport} median: {median:F1} ms"));
                IOException? rejection = null;
                using (var writer = new FileStream(
                    Path.Combine(source, sample), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                    try
                    {
                        Copy(transport, source, staging, destination, temporary, single);
                    }
                    catch (IOException error)
                    {
                        rejection = error;
                    }
                }

                if (rejection is not null)
                {
                    if (Directory.EnumerateFileSystemEntries(destination).Any())
                        throw new InvalidDataException($"{transport} published after a failed copy.");
                }
                else
                {
                    VerifyDirectory(destination, single);
                }
                if (transport == Transport.DotNet && rejection?.HResult != unchecked((int)0x80070020))
                    throw new InvalidDataException(".NET did not report the expected sharing violation.", rejection);
                Console.WriteLine(rejection is null
                    ? $"{transport} with an open writer: copied."
                    : $"{transport} with an open writer: rejected (0x{rejection.HResult:X8}).");
                ClearOutputs();
            }
            return 0;
        }
        finally
        {
            ClearOutputs();
            ClearFiles(source);
            foreach (string directory in new[] { destination, staging, source })
                if (Directory.Exists(directory))
                    Directory.Delete(directory);
            Directory.Delete(root);
        }

        void AddFile(string name, int size)
        {
            byte[] bytes = new byte[size];
            random.NextBytes(bytes);
            hashes.Add(name, SHA256.HashData(bytes));
            File.WriteAllBytes(Path.Combine(source, name), bytes);
            totalBytes += size;
        }

        void ClearOutputs()
        {
            if (Directory.Exists(staging))
                File.Delete(temporary);
            ClearFiles(staging);
            ClearFiles(destination);
        }

        void ClearFiles(string directory)
        {
            if (!Directory.Exists(directory))
                return;
            foreach (string name in hashes.Keys)
                File.Delete(Path.Combine(directory, name));
        }
    }

    private static void Copy(
        Transport transport, string source, string staging, string destination, string temporary,
        IReadOnlyDictionary<string, byte[]> hashes)
    {
        if (transport == Transport.Robocopy)
            RunRobocopy(source, staging, hashes.Keys);

        foreach ((string name, byte[] hash) in hashes)
        {
            string input = Path.Combine(source, name);
            string final = Path.Combine(destination, name);
            if (transport == Transport.DotNet)
            {
                VerifiedCopy.Run(input, final, temporary, expectedHash: hash);
                continue;
            }

            string pending = Path.Combine(staging, name);
            if (transport == Transport.CopyFile2)
            {
                var options = new CopyOptions
                {
                    Size = (uint)Marshal.SizeOf<CopyOptions>(),
                    Flags = 1 // COPY_FILE_FAIL_IF_EXISTS
                };
                int result = CopyFile2(input, pending, in options);
                if (result < 0)
                    throw new IOException($"CopyFile2 failed (HRESULT 0x{result:X8}).", result);
            }

            using (var file = new FileStream(pending, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                file.Flush(flushToDisk: true);
                if (!SHA256.HashData(file).AsSpan().SequenceEqual(hash))
                    throw new InvalidDataException($"{transport} produced a different SHA-256.");
            }
            File.Move(pending, final);
        }
    }

    private static void VerifyDirectory(string directory, IReadOnlyDictionary<string, byte[]> hashes)
    {
        if (Directory.EnumerateFileSystemEntries(directory).Count() != hashes.Count)
            throw new InvalidDataException("Unexpected file count.");

        foreach ((string name, byte[] hash) in hashes)
        {
            using var file = File.OpenRead(Path.Combine(directory, name));
            if (!SHA256.HashData(file).AsSpan().SequenceEqual(hash))
                throw new InvalidDataException("Unexpected file content.");
        }
    }

    private static void RunRobocopy(string source, string staging, IEnumerable<string> names)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "Robocopy.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(staging);
        foreach (string name in names)
            start.ArgumentList.Add(name);
        foreach (string option in new[] { "/R:0", "/W:0", "/COPY:DAT", "/NP", "/NFL", "/NDL" })
            start.ArgumentList.Add(option);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Robocopy did not start.");
        Task output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            process.WaitForExit();
            throw new TimeoutException("Robocopy exceeded 30 seconds.");
        }
        output.GetAwaiter().GetResult();
        if (process.ExitCode >= 8)
            throw new IOException($"Robocopy failed (exit code {process.ExitCode}).");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyOptions
    {
        public uint Size;
        public uint Flags;
        public nint Cancel;
        public nint ProgressRoutine;
        public nint CallbackContext;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CopyFile2(string existingFile, string newFile, in CopyOptions options);
}
