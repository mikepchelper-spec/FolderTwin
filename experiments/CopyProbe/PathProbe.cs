using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        string sentinel = Path.Combine(outside, "sentinel.bin");
        try
        {
            Directory.CreateDirectory(child);
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(sentinel, "synthetic sentinel"u8);

            using (var rootHandle = OpenPlainDirectory(root))
            using (var childHandle = OpenPlainDirectory(child))
            {
                ExpectRenameRefusal(root, movedRoot);
                ExpectRenameRefusal(child, movedChild);
            }
            Directory.Move(root, movedRoot);
            Directory.Move(movedRoot, root);
            Directory.Move(child, movedChild);
            Console.WriteLine("PASS directory handles prevented renames of the root and child until released.");

            CreateJunction(child, outside);
            using (var rootHandle = OpenPlainDirectory(root))
            {
                try
                {
                    using var unexpected = OpenPlainDirectory(child);
                    throw new InvalidOperationException("The directory check followed the junction.");
                }
                catch (InvalidDataException)
                {
                    if (!File.ReadAllBytes(sentinel).AsSpan().SequenceEqual("synthetic sentinel"u8))
                        throw new InvalidDataException("The junction target was changed.");
                }
            }
            Console.WriteLine("PASS a junction substituted for a child directory was rejected.");
            return 0;
        }
        finally
        {
            // The child may be a junction. Nonrecursive deletion removes the link itself.
            foreach (string directory in new[] { child, movedChild, Path.Combine(movedRoot, "child"), root, movedRoot })
                if (Directory.Exists(directory))
                    Directory.Delete(directory);
            if (Directory.Exists(outside))
            {
                File.Delete(sentinel);
                Directory.Delete(outside);
            }
            Directory.Delete(fixture);
        }
    }

    private static SafeFileHandle OpenPlainDirectory(string path)
    {
        const uint listDirectory = 0x01;
        const uint readAttributes = 0x80;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        const uint openReparsePoint = 0x00200000;
        const int attributeTagInfo = 9;
        var handle = WindowsProbeNative.CreateFile(path, listDirectory | readAttributes, FileShare.ReadWrite, 0, openExisting,
            backupSemantics | openReparsePoint, 0);
        try
        {
            if (handle.IsInvalid || !GetAttributeTag(handle, attributeTagInfo, out AttributeTag info, Marshal.SizeOf<AttributeTag>()))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Opening the directory or reading its attributes failed.");
            if ((info.Attributes & FileAttributes.Directory) == 0 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Expected a directory without a reparse point.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag
    {
        public FileAttributes Attributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributeTag(SafeFileHandle file, int infoClass, out AttributeTag info, int size);
}
