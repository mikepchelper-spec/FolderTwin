using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FolderTwin.Core;


// Callers supply a verified synchronous plain-file handle. The backup context
// operates on that handle without resolving the path again or enabling privileges.
internal static class ZoneIdentifierStream
{
    internal const int MaximumBytes = 16 * 1024;
    private const string StreamName = ":Zone.Identifier:$DATA";
    private const int HeaderBytes = 20;
    private static readonly Encoding Names = new UnicodeEncoding(false, false, true);

    internal static byte[]? Read(SafeFileHandle file)
    {
        long position = Position(file);
        nint context = 0;
        try
        {
            byte[] buffer = new byte[64 * 1024];
            int offset = 0, available = 0;
            byte[] header = new byte[HeaderBytes];
            byte[]? zone = null;
            int streams = 0;
            while (ReadInto(header, allowEnd: true))
            {
                uint kind = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (kind != 9 && ++streams > 32) throw new InvalidDataException("El archivo contiene demasiados flujos de respaldo.");
                long length = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
                uint nameLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
                if (length < 0 || nameLength > 1024 || (nameLength & 1) != 0)
                    throw new InvalidDataException("Un flujo del archivo tiene un encabezado inválido.");
                byte[] name = new byte[(int)nameLength];
                ReadInto(name);
                if (kind == 4)
                {
                    if (!Names.GetString(name).Equals(StreamName, StringComparison.OrdinalIgnoreCase) || zone is not null)
                        throw new InvalidDataException("El archivo contiene otro flujo alternativo o una marca duplicada.");
                    if (length > MaximumBytes) throw new InvalidDataException("Zone.Identifier supera el límite de 16 KiB.");
                    zone = new byte[(int)length];
                    ReadInto(zone);
                }
                else
                {
                    long buffered = Math.Min(length, available - offset);
                    offset += (int)buffered;
                    long remaining = length - buffered;
                    if (remaining != 0 && (!BackupSeek(file, (uint)remaining, (uint)((ulong)remaining >> 32),
                        out uint low, out uint high, ref context) || ((ulong)high << 32 | low) != (ulong)remaining))
                        throw NativeError("No se pudo recorrer el flujo principal del archivo.");
                }
            }
            return zone;

            bool ReadInto(byte[] target, bool allowEnd = false)
            {
                int copied = 0;
                while (copied != target.Length)
                {
                    if (offset == available)
                    {
                        if (!BackupRead(file, buffer, (uint)buffer.Length, out uint received, false, false, ref context))
                            throw NativeError("No se pudieron leer los metadatos del archivo.");
                        offset = 0;
                        available = checked((int)received);
                        if (available == 0)
                        {
                            if (allowEnd && copied == 0) return false;
                            throw new InvalidDataException("El flujo de metadatos quedó incompleto.");
                        }
                    }
                    int take = Math.Min(available - offset, target.Length - copied);
                    buffer.AsSpan(offset, take).CopyTo(target.AsSpan(copied));
                    offset += take;
                    copied += take;
                }
                return true;
            }
        }
        finally
        {
            try
            {
                if (context != 0 && !BackupRead(file, null, 0, out _, true, false, ref context))
                    throw NativeError("No se pudo cerrar la lectura de metadatos.");
            }
            finally { RestorePosition(file, position); }
        }
    }

    internal static void Write(SafeFileHandle file, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Zone.Identifier supera el límite de 16 KiB.");
        // BackupWrite can open a named stream even when the supplied base handle
        // has read-only data access. Enforce our capability before entering it.
        WindowsProbeNative.RequireDataWriteAccess(file);
        long position = Position(file);
        nint context = 0;
        try
        {
            byte[] name = Names.GetBytes(StreamName);
            byte[] record = new byte[HeaderBytes + name.Length + bytes.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 4);
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), (uint)name.Length);
            name.CopyTo(record, HeaderBytes);
            bytes.CopyTo(record, HeaderBytes + name.Length);
            if (!BackupWrite(file, record, (uint)record.Length, out uint written, false, false, ref context) || written != record.Length)
                throw NativeError("No se pudo conservar Zone.Identifier en el archivo temporal.");
        }
        finally
        {
            try
            {
                if (context != 0 && !BackupWrite(file, null, 0, out _, true, false, ref context))
                    throw NativeError("No se pudo cerrar la escritura de metadatos.");
            }
            finally { RestorePosition(file, position); }
        }
    }

    private static long Position(SafeFileHandle file) => SetFilePointerEx(file, 0, out long position, 1) ? position :
        throw NativeError("No se pudo consultar la posición del archivo.");

    private static void RestorePosition(SafeFileHandle file, long position)
    {
        if (!SetFilePointerEx(file, position, out _, 0)) throw NativeError("No se pudo conservar la posición del archivo.");
    }

    private static IOException NativeError(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BackupRead(SafeFileHandle file, [Out] byte[]? buffer, uint length, out uint read,
        [MarshalAs(UnmanagedType.Bool)] bool abort, [MarshalAs(UnmanagedType.Bool)] bool security, ref nint context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BackupWrite(SafeFileHandle file, byte[]? buffer, uint length, out uint written,
        [MarshalAs(UnmanagedType.Bool)] bool abort, [MarshalAs(UnmanagedType.Bool)] bool security, ref nint context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BackupSeek(SafeFileHandle file, uint low, uint high, out uint skippedLow, out uint skippedHigh, ref nint context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long position, uint origin);
}
