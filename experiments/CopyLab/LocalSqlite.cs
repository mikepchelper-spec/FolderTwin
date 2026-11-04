using System.IO;
using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace CopyLab;

internal sealed class LocalSqlite : IDisposable
{
    private nint database;
    private const string Library = "winsqlite3.dll";

    internal LocalSqlite(string path, bool create)
    {
        const int readWrite = 0x2, createFile = 0x4, noFollow = 0x01000000;
        try
        {
            Check(sqlite3_open_v2(path, out database, readWrite | noFollow | (create ? createFile : 0), 0));
            Check(sqlite3_busy_timeout(database, 1000));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Execute(string sql) => Check(sqlite3_exec(database, sql, 0, 0, 0));

    internal Statement Prepare(string sql)
    {
        Check(sqlite3_prepare_v2(database, sql, -1, out nint statement, 0));
        return new(this, statement);
    }

    private void Check(int result)
    {
        if (result != 0)
            throw new IOException($"SQLite {result}. " +
                (database == 0 ? "No se pudo abrir el índice." : Marshal.PtrToStringUTF8(sqlite3_errmsg(database))));
    }

    public void Dispose()
    {
        if (database != 0)
        {
            _ = sqlite3_close_v2(database);
            database = 0;
        }
    }

    internal sealed class Statement(LocalSqlite owner, nint handle) : IDisposable
    {
        internal void Bind(int index, string value) =>
            owner.Check(sqlite3_bind_text(handle, index, value, -1, (nint)(-1))); // SQLITE_TRANSIENT.
        internal void Bind(int index, int value) => owner.Check(sqlite3_bind_int(handle, index, value));
        internal string Text(int column) => Marshal.PtrToStringUTF8(sqlite3_column_text(handle, column))
            ?? throw new InvalidDataException("El índice contiene un valor de texto nulo.");
        internal int Int(int column) => sqlite3_column_int(handle, column);

        internal bool Step()
        {
            int result = sqlite3_step(handle);
            if (result == 100) // SQLITE_ROW.
                return true;
            if (result != 101) // SQLITE_DONE.
                owner.Check(result);
            return false;
        }

        internal void Reset() => owner.Check(sqlite3_reset(handle));
        public void Dispose() => _ = sqlite3_finalize(handle);
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint database, int flags, nint vfs);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(nint database);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint sqlite3_errmsg(nint database);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(nint database, int milliseconds);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint context, nint error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int size, out nint statement, nint tail);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(nint statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int size, nint release);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_int(nint statement, int index, int value);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint sqlite3_column_text(nint statement, int column);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_int(nint statement, int column);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(nint statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_reset(nint statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(nint statement);
}
