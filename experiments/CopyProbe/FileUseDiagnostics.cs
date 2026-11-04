using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

// Advisory information only. No shutdown, restart, or privilege elevation APIs.
internal static class FileUseDiagnostics
{
    internal readonly record struct FileUser(uint ProcessId, string Application);

    internal static bool IsSharingViolation(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current.HResult == unchecked((int)0x80070020) ||
                current is Win32Exception { NativeErrorCode: 32 })
                return true;
        return false;
    }

    internal static FileUser[] TryGetUsers(string file)
    {
        try
        {
            return GetUsers(file);
        }
        catch (Exception error) when (error is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    internal static FileUser[] GetUsers(string file)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Restart Manager requires Windows.");
        Check(RmStartSession(out uint session, 0, new StringBuilder(33)));
        try
        {
            Check(RmRegisterResources(session, 1, [file], 0, 0, 0, 0));
            ProcessInfo[] processes = new ProcessInfo[8];
            for (int attempt = 0; attempt < 3; attempt++)
            {
                uint count = (uint)processes.Length;
                int result = RmGetList(session, out uint needed, ref count, processes, out _);
                if (result == 234 && needed > processes.Length && needed <= 256) // ERROR_MORE_DATA
                {
                    processes = new ProcessInfo[needed];
                    continue;
                }
                Check(result);
                return processes.Take(checked((int)count))
                    .Select(process => new FileUser(process.Process.Id, process.Application ?? ""))
                    .ToArray();
            }
            throw new Win32Exception(234, "The file users changed during diagnosis.");
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    private static void Check(int result)
    {
        if (result != 0)
            throw new Win32Exception(result, "Restart Manager could not identify file users.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess
    {
        public uint Id;
        public FILETIME Started;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string? Application;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string? Service;
        public uint ApplicationType;
        public uint Status;
        public uint Session;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmRegisterResources(uint session, uint fileCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] files,
        uint applicationCount, nint applications, uint serviceCount, nint services);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] ProcessInfo[] processes, out uint rebootReasons);

    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern int RmEndSession(uint session);
}
