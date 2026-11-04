using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class WindowsProbeNative
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security,
        uint disposition, uint flags, nint template);

    internal static SafeFileHandle OpenPlainDirectory(string path) => OpenPlain(
        path, 0x01 | 0x80, FileShare.ReadWrite, 3, directory: true);

    internal static SafeFileHandle OpenPlainDirectory(SafeFileHandle parent, string name) =>
        OpenRelative(parent, name, 0x01 | 0x80, FileShare.ReadWrite, 1, directory: true);

    internal static FileStream OpenPlainFile(SafeFileHandle parent, string path, FileAccess access, FileShare share,
        FileMode mode = FileMode.Open,
        bool rename = false)
    {
        uint rights = access switch
        {
            FileAccess.Read => 0x80000000,
            FileAccess.Write => 0x40000000,
            _ => 0xc0000000
        };
        if (rename)
            rights |= 0x00010000; // DELETE, required for rename.
        uint disposition = mode switch
        {
            FileMode.Open => 1,
            FileMode.CreateNew => 2,
            FileMode.OpenOrCreate => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        var handle = OpenRelative(parent, Path.GetFileName(path), rights, share, disposition, directory: false);
        try
        {
            return new FileStream(handle, access);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static bool PlainFileExists(SafeFileHandle parent, string path)
    {
        try
        {
            using var file = OpenRelative(parent, Path.GetFileName(path), 0x80,
                FileShare.ReadWrite | FileShare.Delete, 1, directory: false);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static SafeFileHandle OpenPlain(string path, uint access, FileShare share, uint disposition, bool directory)
    {
        const uint openReparsePoint = 0x00200000;
        const uint backupSemantics = 0x02000000;
        var handle = CreateFile(path, access, share, 0, disposition, openReparsePoint | (directory ? backupSemantics : 0), 0);
        return ValidatePlain(handle, directory);
    }

    private static unsafe SafeFileHandle OpenRelative(SafeFileHandle parent, string name, uint access, FileShare share,
        uint disposition, bool directory)
    {
        const uint synchronize = 0x00100000;
        const uint readAttributes = 0x80;
        const uint synchronousIo = 0x20;
        const uint openReparsePoint = 0x00200000;
        const uint directoryFile = 0x01;
        const uint nonDirectoryFile = 0x40;
        const uint caseInsensitive = 0x40;
        bool acquired = false;
        try
        {
            parent.DangerousAddRef(ref acquired);
            fixed (char* buffer = name)
            {
                ushort length = checked((ushort)(name.Length * sizeof(char)));
                var unicode = new UnicodeString { Length = length, MaximumLength = length, Buffer = buffer };
                var attributes = new ObjectAttributes
                {
                    Length = sizeof(ObjectAttributes),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = &unicode,
                    Attributes = caseInsensitive
                };
                int status = NtCreateFile(out var handle, access | synchronize | readAttributes, in attributes, out _, 0, 0,
                    (uint)share, disposition, synchronousIo | openReparsePoint | (directory ? directoryFile : nonDirectoryFile),
                    0, 0);
                if (status != 0)
                {
                    handle?.Dispose();
                    int error = RtlNtStatusToDosError(status);
                    if (error == 2)
                        throw new FileNotFoundException("The operation file was not found.");
                    throw new IOException("Opening the relative operation path failed.", new Win32Exception(error));
                }
                return ValidatePlain(handle!, directory);
            }
        }
        finally
        {
            if (acquired)
                parent.DangerousRelease();
        }
    }

    private static SafeFileHandle ValidatePlain(SafeFileHandle handle, bool directory)
    {
        const int attributeTagInfo = 9;
        try
        {
            if (handle.IsInvalid || !GetAttributeTag(handle, attributeTagInfo, out AttributeTag info, Marshal.SizeOf<AttributeTag>()))
                throw new IOException("Opening the operation path failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                info.Attributes.HasFlag(FileAttributes.Directory) != directory)
                throw new InvalidDataException("Unexpected path type or reparse point.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static byte[] DirectoryIdentity(SafeFileHandle directory)
    {
        const int fileIdInfo = 18;
        byte[] identity = new byte[24]; // 64-bit volume serial and 128-bit file ID.
        if (!GetIdentity(directory, fileIdInfo, identity, identity.Length))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Reading directory identity failed.");
        return identity;
    }

    internal static void RenameNew(SafeFileHandle file, SafeFileHandle directory, string destinationName)
    {
        const int fileRenameInformation = 10;
        // Leave ReplaceIfExists zero and resolve the name against the pinned directory handle.
        int lengthOffset = 2 * IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        ReadOnlySpan<byte> name = MemoryMarshal.AsBytes(destinationName.AsSpan());
        byte[] request = new byte[nameOffset + name.Length + sizeof(char)];
        BitConverter.TryWriteBytes(request.AsSpan(lengthOffset), (uint)name.Length);
        name.CopyTo(request.AsSpan(nameOffset));
        bool acquired = false;
        try
        {
            directory.DangerousAddRef(ref acquired);
            nint directoryHandle = directory.DangerousGetHandle();
            MemoryMarshal.Write(request.AsSpan(IntPtr.Size), in directoryHandle);
            int status = NtSetInformationFile(file, out _, request, (uint)request.Length, fileRenameInformation);
            if (status != 0)
                throw new IOException("Publishing the verified file failed.", new Win32Exception(RtlNtStatusToDosError(status)));
        }
        finally
        {
            if (acquired)
                directory.DangerousRelease();
        }
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

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle file, int infoClass, [Out] byte[] data, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, in ObjectAttributes attributes,
        out IoStatusBlock status, nint allocationSize, uint fileAttributes, uint share, uint disposition, uint options,
        nint extendedAttributes, uint extendedAttributesLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock status, byte[] data,
        uint size, int infoClass);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int RtlNtStatusToDosError(int status);
}
