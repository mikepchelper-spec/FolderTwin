using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class CloudProcessLifetime
{
    internal static SafeFileHandle Attach(Process process)
    {
        var job = new SafeFileHandle(CreateJobObject(0, null), ownsHandle: true);
        try
        {
            if (job.IsInvalid || !SetInformationJobObject(job, 9, new ExtendedLimits { Flags = 0x2000 },
                (uint)Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!process.HasExited && !AssignProcessToJobObject(job, process.SafeHandle) && !process.HasExited)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            return job;
        }
        catch { job.Dispose(); throw; }
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION on Windows x64. Windows closes
    // the job and terminates its children even when the worker is killed.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct ExtendedLimits
    {
        [FieldOffset(16)] internal uint Flags;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, in ExtendedLimits limits, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
