using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

if (args is ["--interrupt", "before-receipt"])
    return RunInterruptedCopy(CopyStep.Flushed);
if (args is ["--interrupt", "before"])
    return RunInterruptedCopy(CopyStep.Verified);
if (args is ["--interrupt", "after"])
    return RunInterruptedCopy(CopyStep.Published);
if (args is ["--compare"])
    return TransportProbe.Run();
if (args.Length != 0)
{
    Console.Error.WriteLine("Unknown scenario. This experiment does not accept paths.");
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
        Expect<IOException>(() => VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary));
    Check(!File.Exists(fixture.Backup) && !File.Exists(fixture.Temporary), "Locked source started a copy");
    Pass("Windows rejects a source with an open writer");
}

foreach (string boundary in new[] { "before-receipt", "before", "after" })
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
Check(CopyRecovery.Recover(fixture.Temporary, fixture.Restored) == RecoveryResult.InvalidReceipt,
    "Receipt was accepted for a different destination");
Check(File.Exists(fixture.Temporary) && !File.Exists(fixture.Restored), "Wrong destination was published");
Pass("Recovery binds the receipt to its original destination name");

PreparePending(fixture);
using (var operation = new FileStream(CopyRecovery.LockPath(fixture.Temporary), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Expect<IOException>(() => CopyRecovery.Recover(fixture.Temporary, fixture.Backup));
Expect<IOException>(() => VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary, recoverable: true));
Check(CopyRecovery.Recover(fixture.Temporary, fixture.Backup) == RecoveryResult.Published, "Retry did not publish");
File.WriteAllBytes(fixture.Backup, "corrupted copy"u8);
CheckRecoveryLeavesFiles(fixture, RecoveryResult.HashMismatch);
Pass("Recovery excludes concurrent access, retries, and rejects an altered final file");

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
        if (boundary == "before-receipt")
        {
            CheckRecoveryLeavesFiles(interrupted, RecoveryResult.NoReceipt);
            return;
        }

        RecoveryResult expected = boundary == "after" ? RecoveryResult.AlreadyPublished : RecoveryResult.Published;
        Check(CopyRecovery.Recover(interrupted.Temporary, interrupted.Backup) == expected, "Unexpected recovery outcome");
        Check(!File.Exists(interrupted.Temporary), "Published copy still has a temporary name");
        Check(CopyRecovery.Recover(interrupted.Temporary, interrupted.Backup) == RecoveryResult.AlreadyPublished,
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
    }, recoverable: true);
    return 0;
}

static void PreparePending(Fixture fixture)
{
    fixture.Seed("interrupted copy"u8);
    Expect<InjectedFailure>(() => VerifiedCopy.Run(fixture.Source, fixture.Backup, fixture.Temporary, (step, _) =>
    {
        if (step == CopyStep.Verified)
            throw new InjectedFailure();
    }, recoverable: true));
}

static void CheckRecoveryLeavesFiles(Fixture fixture, RecoveryResult expected)
{
    string[] paths = [fixture.Source, fixture.Temporary, fixture.Backup, CopyRecovery.ReceiptPath(fixture.Temporary)];
    byte[]?[] before = paths.Select(path => File.Exists(path) ? File.ReadAllBytes(path) : null).ToArray();
    Check(CopyRecovery.Recover(fixture.Temporary, fixture.Backup) == expected, $"Expected recovery result {expected}");
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
            CopyRecovery.ReceiptPath(Temporary), CopyRecovery.LockPath(Temporary)
        })
            File.Delete(path);
    }

    public void Dispose()
    {
        ClearFiles();
        Directory.Delete(Root);
    }
}
