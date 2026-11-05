using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CopyLab;

internal sealed class VersionIndex(Func<DirectoryFence> openScope)
{
    internal VersionIndex(LabWorkspace workspace) : this(workspace.OpenRegisteredRoot) { }
    internal const string CacheDirectory = ".foldertwin-index";
    internal const string DatabaseName = "index.db";
    private const string OwnerName = "owner";
    private const int ApplicationId = 0x46544931;

    internal static string Key(CatalogSnapshot current, CatalogSnapshot? previous) =>
        Convert.ToHexString(current.DocumentHash) + (previous is null ? "" : ":" + Convert.ToHexString(previous.DocumentHash));

    internal void Ensure(string key, Func<VersionBrowser> build) => Use(create: true, db =>
    {
        db.Execute("PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
        using (var mode = db.Prepare("PRAGMA journal_mode=WAL"))
            if (!mode.Step() || mode.Text(0) != "wal")
                throw new IOException("SQLite no pudo activar WAL.");
        using (var sync = db.Prepare("PRAGMA synchronous"))
            if (!sync.Step() || sync.Int(0) != 2)
                throw new IOException("SQLite no pudo activar synchronous=FULL.");
        db.Execute("BEGIN IMMEDIATE");
        db.Execute("""
            CREATE TABLE IF NOT EXISTS views (key TEXT PRIMARY KEY NOT NULL);
            CREATE TABLE IF NOT EXISTS entries (
                view_key TEXT NOT NULL REFERENCES views(key),
                parent TEXT NOT NULL, path TEXT NOT NULL, directory INTEGER NOT NULL,
                change INTEGER NOT NULL, visible_change INTEGER NOT NULL,
                PRIMARY KEY (view_key, parent, path, directory)
            ) WITHOUT ROWID;
            """);
        if (!HasView(db, key))
        {
            using (var view = db.Prepare("INSERT INTO views VALUES (?1)"))
            {
                view.Bind(1, key);
                _ = view.Step();
            }
            using var insert = db.Prepare("INSERT INTO entries VALUES (?1,?2,?3,?4,?5,?6)");
            insert.Bind(1, key);
            foreach (var (entry, showChange) in build().CacheRows())
            {
                insert.Bind(2, VersionBrowser.Parent(entry.Path));
                insert.Bind(3, entry.Path);
                insert.Bind(4, entry.IsDirectory ? 1 : 0);
                insert.Bind(5, (int)entry.Change);
                insert.Bind(6, showChange ? 1 : 0);
                _ = insert.Step();
                insert.Reset();
            }
        }
        db.Execute("COMMIT");
        return true;
    });

    internal VersionEntry[] Children(string key, string folder, bool onlyChanges) => Use(create: false, db =>
    {
        db.Execute("BEGIN");
        if (!HasView(db, key))
            throw new InvalidDataException("La vista del catálogo falta en el índice.");
        using var query = db.Prepare("""
            SELECT path, directory, change FROM entries
            WHERE view_key=?1 AND parent=?2 AND
                ((?3=1 AND visible_change=1) OR (?3=0 AND change<>?4))
            """);
        query.Bind(1, key);
        query.Bind(2, folder);
        query.Bind(3, onlyChanges ? 1 : 0);
        query.Bind(4, (int)VersionChange.Removed);
        var results = new List<VersionEntry>();
        while (query.Step())
        {
            string path = query.Text(0);
            int directory = query.Int(1);
            var change = (VersionChange)query.Int(2);
            if (VersionBrowser.Parent(path) != folder || directory is not (0 or 1) || !Enum.IsDefined(change))
                throw new InvalidDataException("El índice contiene una ruta o un estado inválido.");
            results.Add(new(path, directory == 1, change));
        }
        return results.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
    });

    internal VersionPage Page(string key, string folder, bool onlyChanges, string search, int offset, int count)
    {
        CheckPage(offset, count);
        return Use(create: false, db =>
        {
            db.Execute("BEGIN");
            if (!HasView(db, key)) throw new InvalidDataException("La vista del catálogo falta en el índice.");
            using var query = db.Prepare("""
                SELECT path, directory, change FROM entries
                WHERE view_key=?1 AND (?2=1 OR parent=?3) AND
                    ((?4=1 AND visible_change=1) OR (?4=0 AND (?2=1 OR change<>?5)))
                ORDER BY directory DESC, path COLLATE BINARY
                """);
            query.Bind(1, key);
            query.Bind(2, search.Length == 0 ? 0 : 1);
            query.Bind(3, folder);
            query.Bind(4, onlyChanges ? 1 : 0);
            query.Bind(5, (int)VersionChange.Removed);
            var entries = new List<VersionEntry>();
            int matched = 0;
            while (query.Step())
            {
                string path = query.Text(0);
                int directory = query.Int(1);
                var change = (VersionChange)query.Int(2);
                if (directory is not (0 or 1) || !Enum.IsDefined(change) ||
                    search.Length == 0 && VersionBrowser.Parent(path) != folder)
                    throw new InvalidDataException("El índice contiene una ruta o un estado inválido.");
                if (!path.Contains(search, StringComparison.OrdinalIgnoreCase) || matched++ < offset) continue;
                if (entries.Count == count) return new VersionPage(entries.ToArray(), true);
                entries.Add(new(path, directory == 1, change));
            }
            return new VersionPage(entries.ToArray(), false);
        });
    }

    internal static void CheckPage(int offset, int count)
    {
        if (offset < 0 || count is < 1 or > 250) throw new ArgumentOutOfRangeException(nameof(count));
    }

    private static bool HasView(LocalSqlite db, string key)
    {
        using var query = db.Prepare("SELECT 1 FROM views WHERE key=?1");
        query.Bind(1, key);
        return query.Step();
    }

    private T Use<T>(bool create, Func<LocalSqlite, T> action)
    {
        using var scope = openScope();
        string volume = Path.GetPathRoot(WindowsProbeNative.VolumePath(scope.Root))!;
        if (GetDriveType(volume) != 3) // DRIVE_FIXED.
            throw new IOException("El índice requiere un volumen fijo local.");
        using var cache = create ? WindowsProbeNative.OpenOrCreatePlainDirectory(scope.Root, CacheDirectory) :
            WindowsProbeNative.OpenPlainDirectory(scope.Root, CacheDirectory);
        using var owner = OpenOwner(cache, create);
        foreach (string name in new[] { DatabaseName, DatabaseName + "-wal", DatabaseName + "-shm", DatabaseName + "-journal" })
            _ = WindowsProbeNative.PlainFileExists(cache, name);
        string path = Path.Combine(WindowsProbeNative.VolumePath(cache), DatabaseName);
        using var db = new LocalSqlite(path, create);
        db.Execute("PRAGMA trusted_schema=OFF;");
        int id;
        using (var application = db.Prepare("PRAGMA application_id"))
            id = application.Step() ? application.Int(0) : -1;
        if (id != ApplicationId)
        {
            if (!create || id != 0)
                throw new InvalidDataException("La base no pertenece al índice de FolderTwin.");
            using (var existing = db.Prepare("SELECT 1 FROM sqlite_master LIMIT 1"))
                if (existing.Step())
                    throw new InvalidDataException("La base ya contiene un esquema ajeno al índice.");
            db.Execute($"PRAGMA application_id={ApplicationId}");
        }
        return action(db);
    }

    private static FileStream OpenOwner(SafeFileHandle cache, bool create)
    {
        byte[] identity = WindowsProbeNative.DirectoryIdentity(cache);
        if (create && !WindowsProbeNative.PlainFileExists(cache, OwnerName))
        {
            if (WindowsProbeNative.ReadDirectory(cache).Any(entry => entry.Name is not ("." or "..")))
                throw new InvalidDataException("La carpeta del índice contiene datos sin registrar.");
            using var created = WindowsProbeNative.OpenPlainFile(cache, OwnerName, FileAccess.Write, FileShare.None, FileMode.CreateNew);
            created.Write(identity);
            created.Flush(flushToDisk: true);
        }
        var owner = WindowsProbeNative.OpenPlainFile(cache, OwnerName, FileAccess.Read, FileShare.Read);
        try
        {
            Span<byte> saved = stackalloc byte[24];
            if (owner.ReadAtLeast(saved, saved.Length, false) != saved.Length || owner.ReadByte() != -1 ||
                !saved.SequenceEqual(identity))
                throw new InvalidDataException("La carpeta del índice cambió de identidad.");
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    internal static bool IsUnavailable(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or ArgumentException or Win32Exception or DllNotFoundException or EntryPointNotFoundException;

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string root);
}
