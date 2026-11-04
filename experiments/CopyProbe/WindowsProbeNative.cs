using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class WindowsProbeNative
{
    private static readonly Encoding DirectoryNameEncoding = new UnicodeEncoding(false, false, true);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security,
        uint disposition, uint flags, nint template);

    internal static SafeFileHandle OpenPlainDirectory(string path) => OpenPlain(
        path, 0x01 | 0x80, FileShare.ReadWrite, 3, directory: true);

    internal static SafeFileHandle OpenPlainDirectory(SafeFileHandle parent, string name) =>
        OpenRelative(parent, name, 0x01 | 0x80, FileShare.ReadWrite, 1, directory: true);

    internal static SafeFileHandle OpenOrCreatePlainDirectory(SafeFileHandle parent, string name)
    {
        DirectoryFence.CheckName(name);
        return OpenRelative(parent, name, 0x01 | 0x80, FileShare.ReadWrite, 3, directory: true);
    }

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
            RequirePlain(parent, directory: true);
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
        try
        {
            RequirePlain(handle, directory);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void RequirePlain(SafeFileHandle handle, bool? directory = null)
    {
        const int attributeTagInfo = 9;
        if (handle.IsInvalid || !GetAttributeTag(handle, attributeTagInfo, out AttributeTag info, Marshal.SizeOf<AttributeTag>()))
            throw new IOException("Opening the operation path failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
        if (directory is { } expected && info.Attributes.HasFlag(FileAttributes.Directory) != expected)
            throw new InvalidDataException("Tipo de ruta inesperado.");
        switch (FolderInventory.Omission(info.Attributes))
        {
            case InventoryIssueKind.Unavailable:
                throw new InvalidDataException("La ruta tiene datos no disponibles localmente. Requiere revisión.");
            case InventoryIssueKind.ReparsePoint:
                throw new InvalidDataException("La ruta contiene un punto de reanálisis. Requiere revisión.");
            case InventoryIssueKind.Encrypted:
                throw new InvalidDataException("La ruta está cifrada con EFS. Requiere revisión.");
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

    internal static void RequireSupportedData(SafeFileHandle handle, string label)
    {
        RequirePlain(handle);
        const int fileStreamInfo = 7;
        const int headerSize = 24;
        // Only the unnamed data stream is supported. A larger response is not needed to authorize a copy.
        byte[] buffer = new byte[4096];
        if (!GetIdentity(handle, fileStreamInfo, buffer, buffer.Length))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == 38) // ERROR_HANDLE_EOF: no streams, as on a plain directory.
                return;
            throw new IOException($"No se pudieron comprobar los flujos NTFS de «{label}».", new Win32Exception(error));
        }

        uint nameBytes = BitConverter.ToUInt32(buffer, 4);
        if (BitConverter.ToUInt32(buffer, 0) != 0 ||
            nameBytes != "::$DATA".Length * sizeof(char) ||
            !buffer.AsSpan(headerSize, (int)nameBytes).SequenceEqual(MemoryMarshal.AsBytes("::$DATA".AsSpan())))
            throw new InvalidDataException($"«{label}» tiene flujos de datos alternativos. Requiere revisión.");
    }

    internal readonly record struct DirectoryEntry(string Name, FileAttributes Attributes, long Length);

    internal static IEnumerable<DirectoryEntry> ReadDirectory(SafeFileHandle directory)
    {
        // FILE_FULL_DIR_INFO has a 68-byte header followed by a UTF-16 name.
        const int headerSize = 68;
        byte[] buffer = new byte[64 * 1024];
        bool restart = true;
        while (true)
        {
            RequirePlain(directory, directory: true);
            Array.Clear(buffer);
            if (!GetIdentity(directory, restart ? 15 : 14, buffer, buffer.Length))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == 18) // ERROR_NO_MORE_FILES
                    yield break;
                throw new IOException("Reading directory entries failed.", new Win32Exception(error));
            }
            restart = false;
            int offset = 0;
            while (true)
            {
                if (offset > buffer.Length - headerSize)
                    throw new InvalidDataException("Invalid directory entry header.");
                uint next = BitConverter.ToUInt32(buffer, offset);
                uint nameLength = BitConverter.ToUInt32(buffer, offset + 60);
                if (nameLength == 0 || nameLength % 2 != 0 || nameLength > buffer.Length - offset - headerSize ||
                    (next != 0 && (next % 8 != 0 || next < headerSize + nameLength || next > buffer.Length - offset - headerSize)))
                    throw new InvalidDataException("Invalid directory entry length.");
                string name;
                try
                {
                    name = DirectoryNameEncoding.GetString(buffer, offset + headerSize, checked((int)nameLength));
                }
                catch (DecoderFallbackException error)
                {
                    throw new InvalidDataException("Unsupported UTF-16 directory name.", error);
                }
                long length = BitConverter.ToInt64(buffer, offset + 40);
                if (length < 0)
                    throw new InvalidDataException("Invalid directory entry size.");
                yield return new DirectoryEntry(name, (FileAttributes)BitConverter.ToUInt32(buffer, offset + 56), length);
                if (next == 0)
                    break;
                offset += checked((int)next);
            }
        }
    }

    internal static unsafe string VolumePath(SafeFileHandle directory)
    {
        const uint volumeNameGuid = 1;
        Span<char> path = stackalloc char[512];
        fixed (char* buffer = path)
        {
            uint size = GetFinalPathNameByHandle(directory, buffer, (uint)path.Length, volumeNameGuid);
            if (size == 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Reading the volume path failed.");
            if (size < path.Length)
                return new string(path[..(int)size]);

            var longerPath = new char[checked((int)size)];
            fixed (char* longerBuffer = longerPath)
            {
                size = GetFinalPathNameByHandle(directory, longerBuffer, (uint)longerPath.Length, volumeNameGuid);
                if (size == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Reading the volume path failed.");
                if (size >= longerPath.Length)
                    throw new IOException("The volume path changed while reading it.");
                return new string(longerPath, 0, (int)size);
            }
        }
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
            RequirePlain(directory, directory: true);
            RequirePlain(file, directory: false);
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

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static extern unsafe uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint size, uint flags);

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
