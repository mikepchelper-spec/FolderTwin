using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

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
        string selected = Path.Combine(outside, "selected");
        string empty = Path.Combine(fixture, "empty");
        string sentinel = Path.Combine(outside, "sentinel.bin");
        try
        {
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(selected);
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

            string redirected = Path.Combine(child, "selected");
            Expect<InvalidDataException>(() =>
            {
                using var rejected = new DirectoryFence(redirected);
            });
            string redirectedPending = Path.Combine(redirected, "pending.tmp");
            string redirectedFinal = Path.Combine(redirected, "backup.bin");
            Expect<InvalidDataException>(() => CopyRecovery.Recover(redirected, redirectedPending, redirectedFinal));
            if (Directory.EnumerateFileSystemEntries(selected).Any())
                throw new InvalidDataException("A junction ancestor redirected a selected root.");
            Console.WriteLine("PASS a junction above the selected root cannot redirect recovery.");

            Directory.CreateDirectory(empty);
            using (var fence = new DirectoryFence(empty))
            {
                CopyJunctionData(child, empty);
                if (!File.GetAttributes(empty).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The open test directory did not become a junction.");
                Expect<InvalidDataException>(() =>
                {
                    using var rejected = WindowsProbeNative.OpenPlainFile(fence.Root, "new.bin", FileAccess.Write,
                        FileShare.None, FileMode.CreateNew);
                });
                if (File.Exists(Path.Combine(outside, "new.bin")))
                    throw new InvalidDataException("An open directory redirected the write after becoming a junction.");
            }
            Directory.Delete(empty);
            Console.WriteLine("PASS a directory converted to a junction after opening is rejected before a relative create.");

            foreach (string unsupported in new[] { @"\\server\share\backup", @"\\.\C:\backup", "C:backup" })
                Expect<ArgumentException>(() =>
                {
                    using var rejected = new DirectoryFence(unsupported);
                });
            Console.WriteLine("PASS UNC, device namespaces, and drive-relative roots are rejected before opening.");

            Directory.Delete(selected);
            Directory.Delete(child);
            Directory.Move(movedChild, child);
            CheckCopyAndRecovery(fixture, root, child, movedChild, outside, sentinel);
            BindingProbe.Run();
            CheckLongPaths();
            return 0;
        }
        finally
        {
            File.Delete(Path.Combine(root, "source.bin"));
            // Nonrecursive deletion removes a junction itself, without following it.
            foreach (string directory in new[] { empty, child, movedChild, Path.Combine(movedRoot, "child"), root, movedRoot })
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
                if (Directory.Exists(selected))
                    Directory.Delete(selected);
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

    private static void CheckLongPaths()
    {
        string fixture = Directory.CreateTempSubdirectory("FolderTwin-LongPaths-").FullName;
        var directories = new List<string> { fixture };
        string root = fixture;
        string[] files = [];
        try
        {
            while (root.Length < 360)
            {
                root = Path.Combine(root, "carpeta con espacios á漢字-0123456789");
                Directory.CreateDirectory(root);
                directories.Add(root);
            }

            string source = Path.Combine(root, "origen á漢字🧪.bin");
            string temporary = Path.Combine(root, "copia á漢字🧪.tmp");
            string destination = Path.Combine(root, "copia á漢字🧪.bin");
            string restored = Path.Combine(root, "restaurado á漢字🧪.bin");
            string restoreTemporary = Path.Combine(root, "restaurado á漢字🧪.tmp");
            files = [source, temporary, destination, restored, restoreTemporary,
                CopyRecovery.ReceiptPath(temporary), CopyRecovery.LockPath(temporary),
                CopyRecovery.ReceiptPath(temporary) + ".tmp", CopyRecovery.ReceiptPath(restoreTemporary),
                CopyRecovery.LockPath(restoreTemporary), CopyRecovery.ReceiptPath(restoreTemporary) + ".tmp"];
            byte[] content = new byte[65_537];
            new Random(42).NextBytes(content);
            byte[] hash = SHA256.HashData(content);
            File.WriteAllBytes(source, content);

            // These names are deliberately outside the prototype's supported set.
            foreach (string name in new[] { "trailing.", "trailing ", "file.bin:stream", "invalid?.bin" })
                Expect<ArgumentException>(() => VerifiedCopy.Run(source, Path.Combine(root, name), temporary,
                    recoveryRoot: root));
            if (Directory.EnumerateFileSystemEntries(root).Single() != source)
                throw new InvalidDataException("An unsupported name left operation files.");
            Console.WriteLine("PASS unsupported names are rejected before creating operation files.");

            Expect<InjectedFailure>(() => VerifiedCopy.Run(source, destination, temporary, (step, _) =>
            {
                if (step == CopyStep.Verified)
                    throw new InjectedFailure();
            }, recoveryRoot: root));
            if (CopyRecovery.Recover(root, temporary, destination) != RecoveryResult.Published ||
                CopyRecovery.Recover(root, temporary, destination) != RecoveryResult.AlreadyPublished)
                throw new InvalidDataException("The long-path operation did not recover idempotently.");
            VerifiedCopy.Run(destination, restored, restoreTemporary, expectedHash: hash, recoveryRoot: root);
            foreach (string path in new[] { source, destination, restored })
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                    throw new InvalidDataException("Long-path copy or restoration changed the content.");
            Console.WriteLine("PASS Unicode paths beyond 360 characters preserve content through copy, recovery, and restoration.");
        }
        finally
        {
            foreach (string path in files)
                File.Delete(path);
            foreach (string directory in directories.AsEnumerable().Reverse())
                Directory.Delete(directory);
        }
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

    private static void CopyJunctionData(string junction, string emptyDirectory)
    {
        const uint openExisting = 3;
        const uint readAccess = 0x80000000;
        const uint writeAccess = 0x40000000;
        const uint openDirectoryAndReparsePoint = 0x02200000;
        const uint getReparsePoint = 0x000900a8;
        const uint setReparsePoint = 0x000900a4;

        using var original = WindowsProbeNative.CreateFile(junction, readAccess, FileShare.ReadWrite, 0, openExisting,
            openDirectoryAndReparsePoint, 0);
        if (original.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Opening the synthetic junction failed.");
        byte[] data = new byte[16 * 1024];
        if (!DeviceIoControl(original, getReparsePoint, null, 0, data, data.Length, out int size, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Reading the synthetic junction failed.");

        using var target = WindowsProbeNative.CreateFile(emptyDirectory, writeAccess, FileShare.ReadWrite, 0, openExisting,
            openDirectoryAndReparsePoint, 0);
        if (target.IsInvalid ||
            !DeviceIoControl(target, setReparsePoint, data, size, null, 0, out _, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Setting the synthetic junction in place failed.");
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[]? input, int inputSize,
        [Out] byte[]? output, int outputSize, out int bytes, nint overlapped);
}
