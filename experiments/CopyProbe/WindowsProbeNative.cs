using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class WindowsProbeNative
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, nint security,
        uint disposition, uint flags, nint template);
}
