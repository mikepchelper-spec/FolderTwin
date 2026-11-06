using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

if (args is ["--interrupt", "before-receipt"])
    return RunInterruptedCopy(CopyStep.Flushed);
if (args is ["--interrupt", "receipt-draft"])
    return RunInterruptedCopy(CopyStep.ReceiptFlushed);
if (args is ["--interrupt", "before"])
    return RunInterruptedCopy(CopyStep.Verified);
if (args is ["--interrupt", "after"])
    return RunInterruptedCopy(CopyStep.Published);
if (args is ["--compare"])
    return TransportProbe.Run();
if (args is ["--uncached"])
    return UncachedProbe.Run();
if (args is ["--prefix-resume"])
    return PrefixResumeProbe.Run();
if (args is ["--paths"])
    return PathProbe.Run();
if (args is ["--product-roots"])
    return LocalJobRootsProbe.Run();
if (args is ["--product-jobs"])
    return ProductJobRegistryProbe.Run();
if (args is ["--review-product-pending", var pendingRegistry, var pendingJob] && Guid.TryParseExact(pendingJob, "N", out var pendingId))
    return ProductRunner.AbandonCommand(pendingRegistry, pendingId);
if (args is ["--abandon-product-version", var abandonRegistry, var abandonProductJob, var abandonReview] &&
    Guid.TryParseExact(abandonProductJob, "N", out var abandonId) && Guid.TryParseExact(abandonReview, "N", out var abandonRun))
    return ProductRunner.AbandonCommand(abandonRegistry, abandonId, abandonRun);
if (args is ["--held-product-abandon-child", var heldAbandonRoot, var heldAbandonJob, var heldAbandonReview] &&
    Guid.TryParseExact(heldAbandonJob, "N", out var heldAbandonId) && Guid.TryParseExact(heldAbandonReview, "N", out var heldAbandonRun))
    return VersionAbandonProbe.HeldProductChild(heldAbandonRoot, heldAbandonId, heldAbandonRun);
if (args is ["--product-config"])
    return ProductConfigurationProbe.Run();
if (args is ["--version-abandon"])
    return VersionAbandonProbe.Run();
if (args is ["--held-abandon-version-child", var abandonRoot, var abandonJob, var abandonGeneration] && Guid.TryParseExact(abandonJob, "N", out var abandonJobId))
    return VersionAbandonProbe.HeldChild(abandonRoot, abandonJobId, abandonGeneration);
if (args is ["--run-product-job", var productRegistry, var productId] && Guid.TryParseExact(productId, "N", out var productJobId))
    return ProductRunner.RunCommand(productRegistry, productJobId);
if (args is ["--run-product-job", var approvedRegistry, var approvedJob, var approval] &&
    Guid.TryParseExact(approvedJob, "N", out var approvedJobId) && Guid.TryParseExact(approval, "N", out var approvalId))
    return ProductRunner.RunCommand(approvedRegistry, approvedJobId, approvalId);
if (args is ["--product-runs"])
    return ProductRunProbe.Run();
if (args is ["--product-restore"])
    return ProductRestoreProbe.Run();
if (args is ["--product-mirror-review"])
    return ProductMirrorReviewProbe.Run();
if (args is ["--review-product-mirror", var mirrorRegistry, var mirrorJob] && Guid.TryParseExact(mirrorJob, "N", out var reviewJobId))
    return ProductRunner.ReviewMirrorCommand(mirrorRegistry, reviewJobId);
if (args is ["--held-mirror-review-child", var mirrorFixture, var heldMirror] && Guid.TryParseExact(heldMirror, "N", out var heldMirrorId))
    return ProductMirrorReviewProbe.HeldChild(mirrorFixture, heldMirrorId);
if (args is ["--restore-product-job", var restoreRegistry, var restoreJob, var restoreRequest] &&
    Guid.TryParseExact(restoreJob, "N", out var restoreJobId) && Guid.TryParseExact(restoreRequest, "N", out var restoreRequestId))
    return ProductRunner.RestoreCommand(restoreRegistry, restoreJobId, restoreRequestId);
if (args is ["--held-restore-child", var restoreFixture, var heldRestoreJob, var heldRestoreRequest] &&
    Guid.TryParseExact(heldRestoreJob, "N", out var heldRestoreId) && Guid.TryParseExact(heldRestoreRequest, "N", out var heldRequestId))
    return ProductRestoreProbe.HeldChild(restoreFixture, heldRestoreId, heldRequestId);
if (args is ["--held-product-child", var productFixture, var heldId] && Guid.TryParseExact(heldId, "N", out var heldJobId))
    return ProductRunProbe.HeldChild(productFixture, heldJobId);
if (args is ["--product-store"])
    return ProductStoreProbe.Run();
if (args is ["--reader-audit"])
    return ReaderAuditProbe.Run();
if (args is ["--differential-audit"])
    return DifferentialAuditProbe.Run();
if (args is ["--recovery-audit"])
    return RecoveryAuditProbe.Run();
if (args is ["--concurrency-audit"])
    return ProductConcurrencyAuditProbe.Run();
if (args is ["--import-concurrency-audit"])
    return ImportedSourceConcurrencyAuditProbe.Run();
if (args is ["--accumulative"])
    return AccumulativeProbe.Run();
if (args is ["--interrupt-accumulative-child", var cumulativeRoot])
    return AccumulativeProbe.InterruptChild(cumulativeRoot);
if (args is ["--inventory"])
    return InventoryProbe.Run();
if (args is ["--batch"])
    return BatchProbe.Run();
if (args is ["--workspace"])
    return WorkspaceProbe.Run();
if (args is ["--versions"])
    return VersionProbe.Run();
if (args is ["--registry"])
    return RegistryProbe.Run();
if (args is ["--retention"])
    return RetentionProbe.Run();
if (args is ["--mirror"])
    return MirrorProbe.Run();
if (args is ["--interrupt-mirror-child", var mirrorRoot])
    return MirrorProbe.InterruptChild(mirrorRoot);
if (args is ["--retention-scale"])
    return RetentionScaleProbe.Run();
if (args is ["--retention-scale", "329000"])
    return RetentionScaleProbe.Run(large: true);
if (args is ["--interrupt-retention-child", var retentionRoot])
    return RetentionProbe.InterruptChild(retentionRoot);
if (args is ["--version-scale", var fileCount, var objectCount] &&
    int.TryParse(fileCount, out int files) && int.TryParse(objectCount, out int objects) &&
    files is >= 1 and <= 329_000 && objects >= 1 && objects <= files)
    return VersionScaleProbe.Run(files, objects);
if (args is ["--object-layout", var layoutCount] && int.TryParse(layoutCount, out int layoutObjects) &&
    layoutObjects is >= 1 and <= 1_000_000)
    return ObjectLayoutProbe.Run(layoutObjects);
if (args is ["--object-layout-volume", var volumeCount, var volumeLevel] &&
    int.TryParse(volumeCount, out int volumeObjects) && volumeObjects is >= 1 and <= 1_000_000 &&
    int.TryParse(volumeLevel, out int selectedLevel) && selectedLevel is 1 or 2)
    return ObjectLayoutProbe.Run(volumeObjects, selectedLevel, disposableVolume: true);
if (args is ["--recover-version", var recoveryWorkspace, var recoveryStore, var recoveryGeneration, var recoveryOutput])
    return VersionRecovery.Run(recoveryWorkspace, recoveryStore, recoveryGeneration, recoveryOutput);
if (args is ["--run-version-job", var jobWorkspace, var jobId] && Guid.TryParseExact(jobId, "N", out var parsedJobId))
    return VersionJob.RunCommand(jobWorkspace, parsedJobId);
if (args is ["--interrupt-version-job", var interruptedWorkspace, var interruptedId] &&
    Guid.TryParseExact(interruptedId, "N", out var interruptedJob))
    return JobProbe.InterruptChild(interruptedWorkspace, interruptedJob);
if (args is ["--jobs"])
    return JobProbe.Run();
if (args is ["--catalog"])
    return CatalogProbe.Run();
if (args is ["--resume"])
    return ResumeProbe.Run();
if (args is ["--interrupt-pending-child", var pendingInterruptedRoot, var pendingBoundary])
    return ResumeProbe.InterruptChild(pendingInterruptedRoot, pendingBoundary);
if (args is ["--resume-pending-child", var pendingResumeRoot, var pendingOutcome, var pendingCount])
    return ResumeProbe.ResumeChild(pendingResumeRoot, pendingOutcome, pendingCount);
if (args is ["--restore-catalog-child", var catalogFixture])
    return CatalogProbe.RestoreChild(catalogFixture);
if (args is ["--volume-lab", "fill"])
    return VolumeProbe.Run("fill");
if (args is ["--volume-lab", "absent"])
    return VolumeProbe.Run("absent");
if (args is ["--volume-lab", "substituted"])
    return VolumeProbe.Run("substituted");
if (args is ["--volume-lab", "reopen"])
    return VolumeProbe.Run("reopen");
if (args.Length != 0)
{
    Console.Error.WriteLine("Unknown scenario. Use a documented synthetic probe or a registered workspace command.");
    return 2;
}

using var fixture = new Fixture();
int passed = 0;

foreach (int size in new[] { 0, 17, 65_537 })
{
    byte[] content = new byte[size];
    new Random(42).NextBytes(content);
    fixture.Seed(content);
    byte[] expected = SHA256.HashData(content);
    VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary);
    VerifiedCopy.Run(fixture.Backup, fixture.Restored, fixture.RestoreTemporary, expectedHash: expected);
    CheckFile(fixture.Source, expected);
    CheckFile(fixture.Backup, expected);
    CheckFile(fixture.Restored, expected);
    Check(!File.Exists(fixture.Temporary) && !File.Exists(fixture.RestoreTemporary), "Temporary file remains");
    Pass($"Copy and restore ({size} bytes)");
}

foreach (CopyStep failure in new[] { CopyStep.Written, CopyStep.Flushed, CopyStep.Verified })
{
    fixture.Seed("new backup"u8);
    File.WriteAllBytes(fixture.Backup, "previous backup"u8);
    Expect<InjectedFailure>(() => VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary, (step, _) =>
    {
        if (step == failure)
            throw new InjectedFailure();
    }));
    CheckFile(fixture.Source, SHA256.HashData("new backup"u8));
    CheckFile(fixture.Backup, SHA256.HashData("previous backup"u8));
    Check(File.Exists(fixture.Temporary), "Pending copy was removed");
    Pass($"Failure at {failure} preserves previous backup");
}

fixture.Seed("original"u8);
Expect<InvalidDataException>(() => VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary, (step, path) =>
{
    if (step == CopyStep.Flushed)
        File.WriteAllBytes(path, "corrupt!"u8);
}));
Check(!File.Exists(fixture.Backup), "Corrupt copy was published");
Pass("Corruption rejected before publication");

fixture.Seed("original"u8);
File.WriteAllBytes(fixture.Backup, "corrupt!"u8);
Expect<InvalidDataException>(() => VerifiedCopy.Run(
    fixture.Backup, fixture.Restored, fixture.RestoreTemporary, expectedHash: SHA256.HashData("original"u8)));
Check(!File.Exists(fixture.Restored), "Corrupt backup was restored");
Pass("Restore rejects a corrupt backup");

fixture.Seed("new backup"u8);
File.WriteAllBytes(fixture.Backup, "previous backup"u8);
Expect<IOException>(() => VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary));
CheckFile(fixture.Backup, SHA256.HashData("previous backup"u8));
Pass("Publication refuses an existing file");

fixture.Seed("new backup"u8);
File.WriteAllBytes(fixture.Temporary, "pending data"u8);
Expect<IOException>(() => VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary));
CheckFile(fixture.Temporary, SHA256.HashData("pending data"u8));
Check(!File.Exists(fixture.Backup), "Copy published over a pending operation");
Pass("Existing temporary file is preserved");

if (OperatingSystem.IsWindows())
{
    fixture.Seed("locked source"u8);
    using (var writer = new FileStream(fixture.Source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
    {
        Expect<IOException>(() => VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary));
        Check(FileUseDiagnostics.GetUsers(fixture.Source).Any(user => user.ProcessId == Environment.ProcessId),
            "Restart Manager did not identify the synthetic writer.");
        Check(writer.CanWrite, "Diagnosis closed the writer.");
    }
    Check(!File.Exists(fixture.Backup) && !File.Exists(fixture.Temporary), "Locked source started a copy");
    Pass("Windows rejects a source with an open writer");
    Check(FileUseDiagnostics.TryGetUsers(fixture.Root).Length == 0,
        "An unsupported diagnostic resource should leave the generic fallback.");
    Check(FileUseDiagnostics.IsSharingViolation(new IOException("native failure", new System.ComponentModel.Win32Exception(32))) &&
        FileUseDiagnostics.IsSharingViolation(new IOException("sharing violation", unchecked((int)0x80070020))) &&
        !FileUseDiagnostics.IsSharingViolation(new IOException("unrelated failure")), "Sharing violation classification failed");
    Pass("Restart Manager identifies the synthetic writer and tolerates an unsupported diagnostic resource");
}

foreach (string boundary in new[] { "before-receipt", "receipt-draft", "before", "after" })
{
    CheckInterruptedCopy(boundary);
    Pass($"Disk recovery after process termination at {boundary}");
}

foreach (RecoveryResult expected in new[]
{
    RecoveryResult.InvalidReceipt, RecoveryResult.HashMismatch, RecoveryResult.Conflict, RecoveryResult.MissingData
})
{
    PreparePending(fixture);
    switch (expected)
    {
        case RecoveryResult.InvalidReceipt:
            using (var receipt = File.OpenWrite(CopyRecovery.ReceiptPath(fixture.Temporary)))
                receipt.SetLength(12);
            break;
        case RecoveryResult.HashMismatch:
            File.WriteAllBytes(fixture.Temporary, "corrupted copy"u8);
            break;
        case RecoveryResult.Conflict:
            File.WriteAllBytes(fixture.Backup, "previous backup"u8);
            break;
        case RecoveryResult.MissingData:
            File.Delete(fixture.Temporary);
            break;
    }
    CheckRecoveryLeavesFiles(fixture, expected);
    Pass($"Recovery preserves files on {expected}");
}

PreparePending(fixture);
using (var receipt = File.Open(CopyRecovery.ReceiptPath(fixture.Temporary), FileMode.Open, FileAccess.ReadWrite))
{
    receipt.Position = 8;
    int original = receipt.ReadByte();
    receipt.Position = 8;
    receipt.WriteByte((byte)(original ^ 0xff));
}
CheckRecoveryLeavesFiles(fixture, RecoveryResult.InvalidReceipt);
Pass("Recovery rejects an altered receipt of the correct length");

PreparePending(fixture);
Check(CopyRecovery.Recover(fixture.Root, fixture.Temporary, fixture.Restored) == RecoveryResult.InvalidReceipt,
    "Receipt was accepted for a different destination");
Check(File.Exists(fixture.Temporary) && !File.Exists(fixture.Restored), "Wrong destination was published");
Pass("Recovery binds the receipt to its original destination name");

PreparePending(fixture);
using (var operation = new FileStream(CopyRecovery.LockPath(fixture.Temporary), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Expect<IOException>(() => CopyRecovery.Recover(fixture.Root, fixture.Temporary, fixture.Backup));
Expect<IOException>(() => VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary, recoveryRoot: fixture.Root));
Check(CopyRecovery.Recover(fixture.Root, fixture.Temporary, fixture.Backup) == RecoveryResult.Published, "Retry did not publish");
File.WriteAllBytes(fixture.Backup, "corrupted copy"u8);
CheckRecoveryLeavesFiles(fixture, RecoveryResult.HashMismatch);
Pass("Recovery excludes concurrent access, retries, and rejects an altered final file");

foreach (CopyStep fault in new[] { CopyStep.Writing, CopyStep.Flushing, CopyStep.WritingReceipt, CopyStep.FlushingReceipt })
{
    byte[] content = new byte[65_537];
    new Random(12).NextBytes(content);
    fixture.Seed(content);
    int writes = 0;
    var error = new IOException("Injected I/O failure", unchecked((int)(fault == CopyStep.Writing ? 0x80070070 : 0x8007001D)));
    try
    {
        VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary, (step, _) =>
        {
            if (step == fault && (step != CopyStep.Writing || ++writes == 2))
                throw error;
        }, recoveryRoot: fixture.Root);
        throw new InvalidOperationException("A failed operation completed.");
    }
    catch (IOException caught) when (ReferenceEquals(caught, error))
    {
        CheckFile(fixture.Source, SHA256.HashData(content));
        Check(new FileInfo(fixture.Temporary).Length == (fault == CopyStep.Writing ? 65_536 : content.Length),
            "Unexpected amount of pending data");
        Check(!File.Exists(fixture.Backup) && !File.Exists(CopyRecovery.ReceiptPath(fixture.Temporary)),
            "An I/O failure published a file or a receipt");
        CheckRecoveryLeavesFiles(fixture, RecoveryResult.NoReceipt);
        Pass($"Injected I/O error at {fault} leaves the operation pending");
    }
}

fixture.Seed(new byte[65_537]);
using (var cancellation = new CancellationTokenSource())
{
    int writes = 0;
    Expect<OperationCanceledException>(() => VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary, (step, _) =>
    {
        if (step == CopyStep.Writing && ++writes == 2)
            cancellation.Cancel();
    }, recoveryRoot: fixture.Root, cancellationToken: cancellation.Token));
}
Check(new FileInfo(fixture.Temporary).Length == 65_536 && !File.Exists(fixture.Backup), "Cancellation published data");
CheckRecoveryLeavesFiles(fixture, RecoveryResult.NoReceipt);
Pass("Cancellation during copy preserves the partial temporary without publishing");

fixture.Seed("new backup"u8);
Expect<IOException>(() => VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary, (step, _) =>
{
    if (step == CopyStep.Verified)
        File.WriteAllBytes(fixture.Backup, "late destination"u8);
}, recoveryRoot: fixture.Root));
CheckFile(fixture.Backup, SHA256.HashData("late destination"u8));
CheckFile(fixture.Temporary, SHA256.HashData("new backup"u8));
CheckRecoveryLeavesFiles(fixture, RecoveryResult.Conflict);
Pass("Handle-based publication preserves a destination created after verification");

Console.WriteLine($"{passed} synthetic checks passed.");
return 0;

void Pass(string name)
{
    passed++;
    Console.WriteLine($"PASS {name}");
}

static void CheckInterruptedCopy(string boundary)
{
    string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable");
    var start = new ProcessStartInfo(executable)
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        StandardOutputEncoding = Encoding.UTF8,
        UseShellExecute = false
    };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--interrupt");
    start.ArgumentList.Add(boundary);

    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start child process");
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    Fixture? interrupted = null;
    try
    {
        string? root = process.StandardOutput.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
        Check(root is not null, "Child exited before the checkpoint");
        interrupted = Fixture.OpenChildDirectory(root!);
        process.Kill();
        Check(process.WaitForExit(15_000), "Child did not exit after termination");

        byte[] hash = SHA256.HashData("interrupted copy"u8);
        CheckFile(interrupted.Source, hash);
        if (boundary != "after")
        {
            Check(!File.Exists(interrupted.Backup), "Unpublished copy appeared under its final name");
            CheckFile(interrupted.Temporary, hash);
        }
        if (boundary is "before-receipt" or "receipt-draft")
        {
            Check(File.Exists(CopyRecovery.ReceiptDraftPath(interrupted.Temporary)) == (boundary == "receipt-draft"),
                "Unexpected receipt draft state");
            CheckRecoveryLeavesFiles(interrupted, RecoveryResult.NoReceipt);
            return;
        }

        RecoveryResult expected = boundary == "after" ? RecoveryResult.AlreadyPublished : RecoveryResult.Published;
        Check(CopyRecovery.Recover(interrupted.Root, interrupted.Temporary, interrupted.Backup) == expected, "Unexpected recovery outcome");
        Check(!File.Exists(interrupted.Temporary), "Published copy still has a temporary name");
        Check(CopyRecovery.Recover(interrupted.Root, interrupted.Temporary, interrupted.Backup) == RecoveryResult.AlreadyPublished,
            "Repeating recovery did not recognize the published file");
        VerifiedCopy.Run(interrupted.Backup, interrupted.Restored, interrupted.RestoreTemporary, expectedHash: hash);
        CheckFile(interrupted.Restored, hash);
    }
    finally
    {
        if (!process.HasExited)
        {
            // EOF releases a child that reached the checkpoint. It removes its own fixture.
            process.StandardInput.Close();
            Check(process.WaitForExit(15_000), "Child did not exit after closing stdin");
        }
        interrupted?.Dispose();
    }
}

static int RunInterruptedCopy(CopyStep checkpoint)
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    using var fixture = new Fixture();
    fixture.Seed("interrupted copy"u8);
    VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary, (step, _) =>
    {
        if (step == checkpoint)
        {
            Console.WriteLine(fixture.Root);
            Console.ReadLine();
        }
    }, recoveryRoot: fixture.Root);
    return 0;
}

static void PreparePending(Fixture fixture)
{
    fixture.Seed("interrupted copy"u8);
    Expect<InjectedFailure>(() => VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary, (step, _) =>
    {
        if (step == CopyStep.Verified)
            throw new InjectedFailure();
    }, recoveryRoot: fixture.Root));
}

static void CheckRecoveryLeavesFiles(Fixture fixture, RecoveryResult expected)
{
    string[] paths = [fixture.Source, fixture.Temporary, fixture.Backup,
        CopyRecovery.ReceiptPath(fixture.Temporary), CopyRecovery.ReceiptDraftPath(fixture.Temporary)];
    byte[]?[] before = paths.Select(path => File.Exists(path) ? File.ReadAllBytes(path) : null).ToArray();
    Check(CopyRecovery.Recover(fixture.Root, fixture.Temporary, fixture.Backup) == expected, $"Expected recovery result {expected}");
    for (int i = 0; i < paths.Length; i++)
    {
        if (before[i] is byte[] bytes)
            Check(File.ReadAllBytes(paths[i]).AsSpan().SequenceEqual(bytes), "Recovery changed an existing file");
        else
            Check(!File.Exists(paths[i]), "Recovery created an unexpected file");
    }
}

static void CheckFile(string path, byte[] expectedHash)
{
    using var input = File.OpenRead(path);
    Check(SHA256.HashData(input).AsSpan().SequenceEqual(expectedHash), "Unexpected file content");
}

static void Expect<T>(Action action) where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class InjectedFailure : IOException;

sealed class Fixture : IDisposable
{
    private const string Prefix = "FolderTwin-Probe-";
    public string Root { get; }
    public string Source => Path.Combine(Root, "origen ñ.bin");
    public string Temporary => Path.Combine(Root, "backup.tmp");
    public string Backup => Path.Combine(Root, "backup.bin");
    public string RestoreTemporary => Path.Combine(Root, "restore.tmp");
    public string Restored => Path.Combine(Root, "restored.bin");

    public Fixture() : this(Directory.CreateTempSubdirectory(Prefix).FullName) { }

    private Fixture(string root) => Root = root;

    public static Fixture OpenChildDirectory(string path)
    {
        string name = Path.GetFileName(path);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) ||
            Path.GetRelativePath(Path.GetTempPath(), path) != name ||
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Unexpected child fixture path");
        return new Fixture(path);
    }

    public void Seed(ReadOnlySpan<byte> content)
    {
        ClearFiles();
        using var file = new FileStream(Source, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(content);
    }

    private void ClearFiles()
    {
        foreach (string path in new[]
        {
            Source, Temporary, Backup, RestoreTemporary, Restored,
            CopyRecovery.ReceiptPath(Temporary), CopyRecovery.ReceiptDraftPath(Temporary), CopyRecovery.LockPath(Temporary)
        })
            File.Delete(path);
    }

    public void Dispose()
    {
        ClearFiles();
        Directory.Delete(Root);
    }
}
