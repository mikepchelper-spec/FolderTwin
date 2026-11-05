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
                VerifyProductUnavailable(state);
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
                VerifyProductUnavailable(state);
                Check(Directory.EnumerateFileSystemEntries(root).Count() == 1, "A product job wrote into the reused letter.");
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
            VerifyProductReturn(state);
            Console.WriteLine("PASS the reattached volume matches its saved identity; pending copy recovered.");
            return 0;
        }

        DestinationBinding binding = DestinationBinding.Register(fence.Root);
        binding.SaveNew(configuration);
        VerifyProductTransfer(state, root, binding);
        VerifyProductModes(state, root);
        VerifyProductDiskFull(state, root, drive, content);
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
        try
        {
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
        finally { File.Delete(source); }
    }

    private static void VerifyProductModes(string state, string parent)
    {
        string source = Path.Combine(state, "product-source");
        string settings = Path.Combine(state, "product-settings");
        string recovery = Path.Combine(state, "product-recovery");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(settings);
        Directory.CreateDirectory(recovery);
        byte[] expected = SHA256.HashData("product backup between NTFS volumes"u8);
        WriteNew(Path.Combine(source, "datos.txt"), "product backup between NTFS volumes"u8);
        using var registry = new ProductJobRegistry(settings);
        foreach (var mode in Enum.GetValues<ProductBackupMode>())
        {
            var job = registry.Create(new(mode.ToString(), mode, null), LocalJobRoots.ReviewDedicatedDestination(source, parent));
            using (var roots = LocalJobRoots.Reopen(new(job.Source,
                new(job.Destination.LastVolumePath, job.Destination.Identity), 0, false)))
                Check(!roots.Review.SameVolume, "The product-mode test did not use two NTFS volumes.");
            var copied = ProductRunner.Run(registry, job.Id);
            Check(copied.State == ProductRunState.Confirmed && copied.Generation is not null,
                copied.Detail ?? "The product runner did not confirm its cross-volume backup.");
            Check(ProductRunner.Verify(registry, job.Id, copied.Generation!).State == ProductRunState.Confirmed,
                "The product runner could not verify the cross-volume backup.");
            string output = Directory.CreateDirectory(Path.Combine(recovery, mode.ToString())).FullName;
            VersionStore.OpenProduct(job).Restore(copied.Generation!, output);
            CheckHash(Path.Combine(output, "datos.txt"), expected);
            CheckHash(Path.Combine(source, "datos.txt"), expected);
        }
        Console.WriteLine("PASS all three product runners copied to another NTFS volume, verified and restored back with matching hashes; their registered jobs remain for detach and return.");
    }

    private static void VerifyProductUnavailable(string state)
    {
        using var registry = new ProductJobRegistry(Path.Combine(state, "product-settings"));
        var jobs = registry.List().Jobs;
        Check(jobs.Length == 3, "The detached-volume fixture lost its three product jobs.");
        foreach (var job in jobs)
        {
            var prior = registry.RunHistory(job.Id).Runs.First(run => run.Intent.Operation == ProductRunOperation.Backup &&
                run.Result?.State == ProductRunState.Confirmed).Result!;
            var attempt = ProductRunner.Run(registry, job.Id);
            Check(attempt.State == ProductRunState.WaitingForDestination,
                attempt.Detail ?? "A product runner accepted the detached or substituted destination.");
            var history = registry.RunHistory(job.Id);
            Check(history.Runs[0].Result is { } saved && saved.State == attempt.State &&
                saved.IntentHash.AsSpan().SequenceEqual(attempt.IntentHash) && history.Runs.Any(run =>
                    run.Result is { } preserved && preserved.IntentHash.AsSpan().SequenceEqual(prior.IntentHash)),
                "The unavailable product destination lost its incident or prior confirmed result.");
        }
        Console.WriteLine("PASS all three product runners report the absent registered volume and preserve their prior confirmed result and incidents.");
    }

    private static void VerifyProductDiskFull(string state, string parent, DriveInfo drive, byte[] content)
    {
        WriteNew(Path.Combine(state, "product-source", "large.bin"), content);
        using var registry = new ProductJobRegistry(Path.Combine(state, "product-settings"));
        foreach (var job in registry.List().Jobs)
        {
            var store = VersionStore.OpenProduct(job);
            var prior = registry.RunHistory(job.Id).Runs.First(run => run.Intent.Operation == ProductRunOperation.Backup &&
                run.Result?.State == ProductRunState.Confirmed).Result!;
            int points = store.ListPoints().Length;
            string padding = Path.Combine(parent, "product-padding.bin");
            bool created = false;
            ProductRunResult failed;
            try
            {
                failed = ProductRunner.Run(registry, job.Id, checkpoint: work =>
                {
                    // Fill only after the ordinary space checks and durable intent,
                    // simulating another writer consuming capacity during the copy.
                    if (created || work.Phase != WorkPhase.Copying) return;
                    using var filler = new FileStream(padding, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
                    created = true;
                    byte[] block = new byte[64 * 1024];
                    for (int i = 0; i < (128 * MiB) / block.Length && drive.AvailableFreeSpace > 2 * MiB; i++)
                        filler.Write(block);
                    filler.Flush(flushToDisk: true);
                });
                Check(created && failed.State == ProductRunState.Review && failed.ErrorCode is
                    (112 or 39 or unchecked((int)0x80070070) or unchecked((int)0x80070027)),
                    failed.Detail ?? "The product runner did not report a real disk-full error.");
                Check(store.ListPoints().Length == points && !store.Verify(prior.Generation!),
                    "Disk full published a partial product point or damaged the prior confirmed backup.");
                Console.WriteLine($"PASS {job.Options.Mode} reports actual disk full ({failed.ErrorCode}), preserves its pending generation and verifies the prior point.");
            }
            finally { if (created) File.Delete(padding); }
            var resumed = ProductRunner.Run(registry, job.Id);
            Check(resumed.State == ProductRunState.Confirmed && resumed.Generation == failed.Generation &&
                ProductRunner.Verify(registry, job.Id, resumed.Generation!).State == ProductRunState.Confirmed,
                resumed.Detail ?? "The product runner did not resume its exact generation after capacity returned.");
            var review = LocalJobRoots.ReviewDedicatedDestination(job.Destination.LastVolumePath, Path.Combine(state, "product-recovery"));
            var request = registry.ApproveRestore(job.Id, resumed.Generation!, review, "large.bin");
            Check(ProductRunner.Restore(registry, job.Id, request).State == ProductRunState.Confirmed,
                "The disk-full product retry could not restore the large file.");
            CheckHash(Path.Combine(review.Parent.VolumePath, review.FolderName, "large.bin"), SHA256.HashData(content));
            Check(registry.RunHistory(job.Id).Runs.Any(run => run.Result is { } saved &&
                saved.IntentHash.AsSpan().SequenceEqual(failed.IntentHash) && saved.ErrorCode == failed.ErrorCode),
                "Successful retry erased the product disk-full incident.");
        }
        Console.WriteLine("PASS all three packaged product modes preserve prior data during real capacity exhaustion, resume the same pending generation, verify, restore and retain the incident.");
    }

    private static void VerifyProductReturn(string state)
    {
        using var registry = new ProductJobRegistry(Path.Combine(state, "product-settings"));
        var jobs = registry.List().Jobs;
        Check(jobs.Length == 3, "The returned-volume fixture lost its product jobs.");
        var generations = new Dictionary<Guid, string>();
        foreach (var job in jobs)
        {
            var result = ProductRunner.Run(registry, job.Id);
            Check(result.State == ProductRunState.Confirmed && result.Generation is not null,
                result.Detail ?? "A product runner did not resume after the volume returned with a new letter.");
            generations.Add(job.Id, result.Generation!);
            Check(ProductRunner.Verify(registry, job.Id, result.Generation!).State == ProductRunState.Confirmed &&
                registry.RunHistory(job.Id).Runs.Count(run => run.Result?.State == ProductRunState.WaitingForDestination) == 2,
                "The product return failed verification or lost an absence/substitution incident.");
        }
        string source = Path.Combine(state, "product-source"), absent = Path.Combine(state, "product-source-absent");
        Directory.Move(source, absent);
        try
        {
            foreach (var job in jobs)
            {
                var destination = LocalJobRoots.ReviewDedicatedDestination(job.Destination.LastVolumePath, Path.Combine(state, "product-recovery"));
                Guid request = registry.ApproveRestore(job.Id, generations[job.Id], destination);
                Check(ProductRunner.Restore(registry, job.Id, request).State == ProductRunState.Confirmed,
                    "The returned product backup could not restore without its source.");
                CheckHash(Path.Combine(destination.Parent.VolumePath, destination.FolderName, "datos.txt"),
                    SHA256.HashData("product backup between NTFS volumes"u8));
            }
        }
        finally { Directory.Move(absent, source); }
        foreach (var job in jobs)
        {
            using (var fence = new DirectoryFence(job.Destination.LastVolumePath))
            using (job.Destination.OpenVerifiedMarker(fence.Root)) { }
            Directory.Delete(job.Destination.LastVolumePath, recursive: true);
        }
        Console.WriteLine("PASS packaged product runners reopen all three modes after VHDX detach, letter substitution and return, retain both incidents, verify and restore without the source.");
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
