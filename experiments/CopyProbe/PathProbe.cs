using System.Diagnostics;
using System.Security.Cryptography;

internal static class PathProbe
{
    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The directory handle experiment requires Windows.");

        string fixture = Directory.CreateTempSubdirectory("FolderTwin-Paths-").FullName;
        string root = Path.Combine(fixture, "root");
        string movedRoot = Path.Combine(fixture, "moved-root");
        string child = Path.Combine(root, "child");
        string movedChild = Path.Combine(root, "moved-child");
        string outside = Path.Combine(fixture, "outside");
        string sentinel = Path.Combine(outside, "sentinel.bin");
        try
        {
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(sentinel, "synthetic sentinel"u8);

            using (var rootHandle = WindowsProbeNative.OpenPlainDirectory(root))
            using (var childHandle = WindowsProbeNative.OpenPlainDirectory(child))
            {
                ExpectRenameRefusal(root, movedRoot);
                ExpectRenameRefusal(child, movedChild);
            }
            Directory.Move(root, movedRoot);
            Directory.Move(movedRoot, root);
            Directory.Move(child, movedChild);
            Console.WriteLine("PASS directory handles prevented renames of the root and child until released.");

            CreateJunction(child, outside);
            using (var rootHandle = WindowsProbeNative.OpenPlainDirectory(root))
            {
                try
                {
                    using var unexpected = WindowsProbeNative.OpenPlainDirectory(child);
                    throw new InvalidOperationException("The directory check followed the junction.");
                }
                catch (InvalidDataException)
                {
                    if (!File.ReadAllBytes(sentinel).AsSpan().SequenceEqual("synthetic sentinel"u8))
                        throw new InvalidDataException("The junction target was changed.");
                }
            }
            Console.WriteLine("PASS a junction substituted for a child directory was rejected.");
            Directory.Delete(child);
            Directory.Move(movedChild, child);
            CheckCopyAndRecovery(fixture, root, child, movedChild, outside, sentinel);
            return 0;
        }
        finally
        {
            File.Delete(Path.Combine(root, "source.bin"));
            // Nonrecursive deletion removes a junction itself, without following it.
            foreach (string directory in new[] { child, movedChild, Path.Combine(movedRoot, "child"), root, movedRoot })
                if (Directory.Exists(directory))
                {
                    if (!File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                        foreach (string name in new[] { "backup.bin", "pending.tmp", "pending.tmp.ready", "pending.tmp.lock",
                            "pending.tmp.ready.tmp", "moved-pending.tmp" })
                            File.Delete(Path.Combine(directory, name));
                    Directory.Delete(directory);
                }
            if (Directory.Exists(outside))
            {
                File.Delete(sentinel);
                Directory.Delete(outside);
            }
            Directory.Delete(fixture);
        }
    }

    private static void CheckCopyAndRecovery(string fixture, string root, string child, string movedChild,
        string outside, string sentinel)
    {
        string source = Path.Combine(root, "source.bin");
        string temporary = Path.Combine(child, "pending.tmp");
        string destination = Path.Combine(child, "backup.bin");
        string receipt = CopyRecovery.ReceiptPath(temporary);
        string lockFile = CopyRecovery.LockPath(temporary);
        File.WriteAllBytes(source, "synthetic copy"u8);
        Expect<ArgumentException>(() => VerifiedCopy.Run(sentinel, destination, temporary, recoveryRoot: root));
        Expect<ArgumentException>(() => VerifiedCopy.Run(source + ":alternate", destination, temporary, recoveryRoot: root));
        if (Directory.EnumerateFileSystemEntries(child).Any())
            throw new InvalidDataException("The rejected path left operation files.");

        Expect<InjectedFailure>(() => VerifiedCopy.Run(source, destination, temporary, (step, _) =>
        {
            if (step == CopyStep.Verified)
            {
                ExpectRenameRefusal(root, Path.Combine(fixture, "moved-root"));
                ExpectRenameRefusal(child, movedChild);
                Expect<IOException>(() => File.Move(temporary, Path.Combine(child, "moved-pending.tmp")));
                throw new InjectedFailure();
            }
        }, recoveryRoot: root));
        Console.WriteLine("PASS the recoverable copy rejects outside paths and holds directory and file handles.");

        Directory.Move(child, movedChild);
        Directory.CreateDirectory(child);
        foreach (string path in new[] { temporary, receipt, lockFile })
            File.Copy(Path.Combine(movedChild, Path.GetFileName(path)), path);

        if (CopyRecovery.Recover(root, temporary, destination) != RecoveryResult.WrongDirectory || File.Exists(destination))
            throw new InvalidDataException("A copied receipt authorized a different directory.");
        Console.WriteLine("PASS a replacement directory with a copied receipt cannot publish.");

        foreach (string path in new[] { temporary, receipt, lockFile })
            File.Delete(path);
        Directory.Delete(child);
        CreateJunction(child, outside);
        Expect<InvalidDataException>(() => CopyRecovery.Recover(root, temporary, destination));
        Expect<InvalidDataException>(() => VerifiedCopy.Run(source, destination, temporary, recoveryRoot: root));
        if (Directory.EnumerateFileSystemEntries(outside).Count() != 1 ||
            !File.ReadAllBytes(sentinel).AsSpan().SequenceEqual("synthetic sentinel"u8))
            throw new InvalidDataException("An operation followed the junction.");
        Directory.Delete(child);

        Directory.Move(movedChild, child);
        if (CopyRecovery.Recover(root, temporary, destination) != RecoveryResult.Published ||
            CopyRecovery.Recover(root, temporary, destination) != RecoveryResult.AlreadyPublished ||
            !SHA256.HashData(File.ReadAllBytes(destination)).AsSpan().SequenceEqual(SHA256.HashData("synthetic copy"u8)))
            throw new InvalidDataException("The original directory did not recover.");
        Console.WriteLine("PASS copy and recovery reject the substituted junction and recover in the original directory.");
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void ExpectRenameRefusal(string path, string replacement)
    {
        try
        {
            Directory.Move(path, replacement);
        }
        catch (IOException)
        {
            return;
        }
        throw new InvalidOperationException("A pinned directory was renamed.");
    }

    private static void CreateJunction(string path, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path $env:FOLDERTWIN_JUNCTION -Target $env:FOLDERTWIN_JUNCTION_TARGET | Out-Null" })
            start.ArgumentList.Add(arg);
        start.Environment["FOLDERTWIN_JUNCTION"] = path;
        start.Environment["FOLDERTWIN_JUNCTION_TARGET"] = target;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create the test junction.");
        if (!process.WaitForExit(15_000))
        {
            process.Kill();
            process.WaitForExit();
            throw new TimeoutException("Creating the test junction timed out.");
        }
        if (process.ExitCode != 0)
            throw new IOException($"Creating the test junction failed ({process.ExitCode}).");
    }
}
