using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

internal static class UncachedProbe
{
    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The unbuffered comparison requires Windows.");

        using var fixture = new Fixture();
        foreach (int size in new[] { 0, 1, 511, 512, 4095, 4096, 65_537, 8 * 1024 * 1024 + 17 })
        {
            byte[] content = new byte[size];
            new Random(42).NextBytes(content);
            fixture.Seed(content);
            byte[] expected = SHA256.HashData(content);
            VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary, expectedHash: expected);

            var timer = Stopwatch.StartNew();
            byte[] buffered;
            using (var input = File.OpenRead(fixture.Backup))
                buffered = SHA256.HashData(input);
            double bufferedMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            byte[] unbuffered = HashUnbuffered(fixture.Backup, out StorageInfo storage);
            double unbufferedMs = timer.Elapsed.TotalMilliseconds;

            if (!buffered.AsSpan().SequenceEqual(expected) || !unbuffered.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("Buffered or unbuffered SHA-256 mismatch.");
            Console.WriteLine(FormattableString.Invariant(
                $"PASS {size} bytes, sectors {storage.Logical}/{storage.PhysicalAtomicity}/{storage.PhysicalPerformance}, buffered {bufferedMs:F2} ms, unbuffered {unbufferedMs:F2} ms"));
        }
        return 0;
    }

    private static unsafe byte[] HashUnbuffered(string path, out StorageInfo storage)
    {
        const uint readAccess = 0x80000000;
        const uint openExisting = 3;
        const uint noBuffering = 0x20000000;
        const int fileStorageInfo = 16;
        const int bufferSize = 64 * 1024;
        using var file = WindowsProbeNative.CreateFile(path, readAccess, FileShare.Read, 0, openExisting, noBuffering, 0);
        if (file.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Opening the unbuffered read failed.");
        if (!GetStorageInfo(file, fileStorageInfo, out storage, sizeof(StorageInfo)))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Reading storage alignment failed.");

        uint alignment = Math.Max(storage.Logical, Math.Max(storage.PhysicalAtomicity, storage.PhysicalPerformance));
        if (!BitOperations.IsPow2(storage.Logical) || !BitOperations.IsPow2(alignment) || alignment > bufferSize)
            throw new NotSupportedException("Unsupported storage alignment for the unbuffered experiment.");

        void* buffer = NativeMemory.AlignedAlloc(bufferSize, alignment);
        try
        {
            long remaining = RandomAccess.GetLength(file);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (remaining > 0)
            {
                if (!ReadFile(file, buffer, bufferSize, out int count, 0))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "The unbuffered read failed.");
                if (count != Math.Min(remaining, bufferSize))
                    throw new IOException("Unexpected length returned by the unbuffered read.");
                hash.AppendData(new ReadOnlySpan<byte>(buffer, count));
                remaining -= count;
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageInfo
    {
        public uint Logical;
        public uint PhysicalAtomicity;
        public uint PhysicalPerformance;
        public uint EffectiveAtomicity;
        public uint Flags;
        public uint SectorOffset;
        public uint PartitionOffset;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStorageInfo(SafeFileHandle file, int infoClass, out StorageInfo storage, int size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadFile(SafeFileHandle file, void* buffer, int size, out int read, nint overlapped);
}
