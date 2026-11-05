using System.ComponentModel;
using System.Security.Cryptography;

internal static class VolumeProbe
{
    private const int MiB = 1024 * 1024;
    private static ReadOnlySpan<byte> Previous => "synthetic previous backup"u8;

    public static int Run(string phase)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("The volume lab requires a GitHub-hosted Windows runner.");

        string label = Environment.GetEnvironmentVariable("FOLDERTWIN_LAB_LABEL") ?? "";
        string letter = Environment.GetEnvironmentVariable("FOLDERTWIN_LAB_LETTER") ?? "";
        string runnerTemp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")!);
        string state = Environment.GetEnvironmentVariable("FOLDERTWIN_LAB_STATE") ?? "";
        if (label.Length != 11 || !label.StartsWith("FTL", StringComparison.Ordinal) ||
            label.AsSpan(3).ContainsAnyExcept("0123456789abcdef") ||
            letter.Length != 1 || letter[0] is < 'D' or > 'Z' ||
            Path.GetDirectoryName(state) != runnerTemp || Path.GetFileName(state) != $"FolderTwin-Vhd-{label}")
            throw new InvalidOperationException("The volume lab configuration is invalid.");

        string configuration = Path.Combine(state, "destination.json");
        string root = Path.Combine($"{letter}:\\", label);
        if (phase == "absent")
        {
            DestinationBinding saved = DestinationBinding.Load(configuration);
            string unavailable = saved.LastVolumePath;
            try
            {
                CopyRecovery.Recover(unavailable, Path.Combine(unavailable, "pending.tmp"),
                    Path.Combine(unavailable, "final.bin"), saved);
            }
            catch (IOException error) when (error.InnerException is Win32Exception { NativeErrorCode: 2 or 3 or 15 or 21 or 55 or 1167 })
            {
                Console.WriteLine("PASS recovery refused the unavailable volume while it was detached.");
                return 0;
            }
            throw new InvalidDataException("Recovery did not report the detached destination as unavailable.");
        }
        if (phase == "substituted")
        {
            DestinationBinding saved = DestinationBinding.Load(configuration);
            using var candidate = new DirectoryFence(root);
            Check(!WindowsProbeNative.DirectoryIdentity(candidate.Root).AsSpan().SequenceEqual(saved.Identity),
                "The substitution did not reach a different destination.");
            Check(File.ReadAllBytes(Path.Combine(root, DestinationBinding.MarkerName)).AsSpan()
                .SequenceEqual(saved.Marker.ToByteArray()), "The substitute should have an identical marker.");
            try
            {
                CopyRecovery.Recover(root, Path.Combine(root, "pending.tmp"), Path.Combine(root, "final.bin"), saved);
            }
            catch (InvalidDataException)
            {
                Check(Directory.EnumerateFileSystemEntries(root).Count() == 1, "Recovery wrote into the substitute.");
                Console.WriteLine("PASS recovery refused the reused letter with a copied marker and left the substitute unchanged.");
                return 0;
            }
            throw new InvalidDataException("Recovery accepted the substitute under the registered drive letter.");
        }

        var drive = new DriveInfo($"{letter}:\\");
        if (drive.TotalSize is < 110 * MiB or > 128 * MiB || drive.VolumeLabel != label || drive.DriveFormat != "NTFS")
            throw new InvalidOperationException("The volume does not match the disposable lab disk.");

        using var fence = new DirectoryFence(root);
        using (var gate = WindowsProbeNative.OpenPlainFile(fence.Root, "runner.txt", FileAccess.Read, FileShare.Read))
        using (var reader = new StreamReader(gate))
            if (reader.ReadLine() != label || reader.ReadLine() is not null)
                throw new InvalidDataException("The disposable volume gate is missing or incorrect.");

        using (var roots = new LocalJobRoots(state, root))
        {
            Check(!roots.Review.SameVolume, "Product roots did not distinguish the disposable NTFS volume from the runner volume.");
            using var reopened = LocalJobRoots.Reopen(roots.Review);
            Check(reopened.Review.AvailableBytes > 0, "The product review could not reopen the disposable destination.");
        }

        string source = Path.Combine(root, "source.bin");
        string pending = Path.Combine(root, "pending.tmp");
        string final = Path.Combine(root, "final.bin");
        string previous = Path.Combine(root, "previous.bin");
        string fullPending = Path.Combine(root, "full.tmp");
        string fullFinal = Path.Combine(root, "full.bin");
        byte[] content = new byte[8 * MiB];
        new Random(42).NextBytes(content);
        byte[] expected = SHA256.HashData(content);

        if (phase == "reopen")
        {
            DestinationBinding saved = DestinationBinding.Load(configuration);
            using (var guidFence = new DirectoryFence(saved.LastVolumePath))
            using (saved.OpenVerifiedMarker(guidFence.Root))
            {
                Check(WindowsProbeNative.DirectoryIdentity(guidFence.Root).AsSpan()
                    .SequenceEqual(WindowsProbeNative.DirectoryIdentity(fence.Root)), "The volume GUID resolved a different directory.");
            }

            Check(CopyRecovery.Recover(root, pending, final, saved) == RecoveryResult.Published, "Recovery after letter change failed.");
            Check(CopyRecovery.Recover(root, pending, final, saved) == RecoveryResult.AlreadyPublished,
                "Repeated recovery did not recognize the published copy.");
            CheckHash(final, expected);
            CheckHash(previous, SHA256.HashData(Previous));
            Check(File.Exists(fullPending) && !File.Exists(fullFinal), "The disk-full temporary was not preserved.");
            Console.WriteLine("PASS the reattached volume matches its saved identity; pending copy recovered.");
            return 0;
        }

        DestinationBinding binding = DestinationBinding.Register(fence.Root);
        binding.SaveNew(configuration);
        VerifyProductTransfer(state, root, binding);
        WriteNew(source, content);
        WriteNew(previous, Previous);

        string padding = Path.Combine(root, "padding.bin");
        bool paddingCreated = false;
        try
        {
            using (var filler = new FileStream(padding, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                paddingCreated = true;
                byte[] block = new byte[64 * 1024];
                for (int i = 0; i < (128 * MiB) / block.Length && drive.AvailableFreeSpace > 2 * MiB; i++)
                    filler.Write(block);
                filler.Flush(flushToDisk: true);
            }
            Check(drive.AvailableFreeSpace < content.Length, "The bounded fill did not produce a full-volume scenario.");
            bool diskFull = false;
            try
            {
                VerifiedCopy.Run(source, fullFinal, fullPending, expectedHash: expected, recoveryRoot: root, binding: binding);
            }
            catch (IOException error) when (IsDiskFull(error))
            {
                diskFull = true;
                Console.WriteLine($"Observed actual disk-full error: 0x{error.HResult:X8}");
            }
            Check(diskFull, "Copy did not return a real disk-full error.");
            Check(!File.Exists(fullFinal) && !File.Exists(CopyRecovery.ReceiptPath(fullPending)),
                "A failed copy published data or a receipt.");
            Check(new FileInfo(fullPending).Length < content.Length, "The failed copy did not leave a partial temporary.");
            CheckHash(previous, SHA256.HashData(Previous));
            CheckHash(source, expected);
            Check(CopyRecovery.Recover(root, fullPending, fullFinal, binding) == RecoveryResult.NoReceipt,
                "Recovery accepted the failed copy.");
        }
        finally
        {
            if (paddingCreated)
                File.Delete(padding);
        }

        try
        {
            VerifiedCopy.Run(source, final, pending, (step, _) =>
            {
                if (step == CopyStep.Verified)
                    throw new InjectedFailure();
            }, expected, root, binding: binding);
            throw new InvalidOperationException("Expected a verified pending copy.");
        }
        catch (InjectedFailure)
        {
            Check(File.Exists(CopyRecovery.ReceiptPath(pending)) && !File.Exists(final), "The verified operation was not pending.");
        }
        Console.WriteLine("PASS real disk full preserved previous data and refused publication.");
        return 0;
    }

    private static bool IsDiskFull(IOException error) =>
        error.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027);

    private static void VerifyProductTransfer(string sourceRoot, string destinationRoot, DestinationBinding binding)
    {
        const string name = "product-cross-volume.bin";
        const string draft = "product-cross-volume.tmp";
        string source = Path.Combine(sourceRoot, name);
        string final = Path.Combine(destinationRoot, name);
        string temporary = Path.Combine(destinationRoot, draft);
        byte[] hash = SHA256.HashData("verified across two volumes"u8);
        WriteNew(source, "verified across two volumes"u8);
        using var roots = new LocalJobRoots(sourceRoot, destinationRoot);
        try
        {
            VerifiedCopy.RunBound(roots, binding, name, name, draft, hash, (step, _) =>
            {
                if (step == CopyStep.Verified)
                    throw new InjectedFailure();
            });
            throw new InvalidOperationException("The cross-volume transfer did not stop at its verification boundary.");
        }
        catch (InjectedFailure)
        {
            Check(!File.Exists(final), "The interrupted cross-volume copy was published.");
        }
        Check(CopyRecovery.Recover(destinationRoot, temporary, final, binding, hash) == RecoveryResult.Published,
            "The cross-volume copy could not recover on the destination volume.");
        CheckHash(final, hash);
        CheckHash(source, hash);
        Console.WriteLine("PASS verified transfer across two NTFS volumes recovered at the destination without changing the source.");
    }

    private static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static void CheckHash(string path, byte[] expected)
    {
        using var file = File.OpenRead(path);
        Check(SHA256.HashData(file).AsSpan().SequenceEqual(expected), "Synthetic file hash changed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
