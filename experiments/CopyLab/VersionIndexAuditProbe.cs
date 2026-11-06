using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CopyLab;

// Synthetic, deterministic substitution after the managed preflight and before the SQLite VFS opens files.
// The sentinel is another tiny index owned by this probe, never the user's profile or backup.
internal static class VersionIndexAuditProbe
{
    internal static void Run()
    {
        if (new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace < 256L * 1024 * 1024)
            throw new IOException("The SQLite boundary audit needs a 256 MiB free-space reserve.");
        var failures = new List<string>();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            Check(suffix, afterPreflight: false);
            Check(suffix, afterPreflight: true);
        }
        CheckNormalUse();
        CheckForeignDatabases();
        CheckForeignPendingWal();
        foreach (string failure in failures) Console.Error.WriteLine("FAIL SQLite boundary audit: " + failure);
        if (failures.Count != 0) throw new InvalidOperationException($"SQLite boundary audit: {failures.Count} unsafe results in six bounded cases.");
        Console.Error.WriteLine("PASS six deterministic SQLite DB/WAL/SHM substitutions rejected or preserved the independent synthetic sentinel.");

        void Check(string suffix, bool afterPreflight)
        {
            var workspace = LabWorkspace.Create();
            string? alias = null;
            try
            {
                string primary = Directory.CreateDirectory(Path.Combine(workspace.Root, "primary")).FullName;
                string sentinelScope = Directory.CreateDirectory(Path.Combine(workspace.Root, "sentinel")).FullName;
                var before = Catalog("before");
                var after = Catalog("after");
                var originalIndex = new VersionIndex(() => new DirectoryFence(primary));
                originalIndex.Ensure(VersionIndex.Key(before, null), () => new(before, null));
                var sentinelIndex = new VersionIndex(() => new DirectoryFence(sentinelScope));
                sentinelIndex.Ensure("independent-sentinel-view", () => new(before, null));
                string sentinel = Path.Combine(sentinelScope, VersionIndex.CacheDirectory, VersionIndex.DatabaseName);
                using (var db = new LocalSqlite(sentinel, create: false))
                    db.Execute("CREATE TABLE sentinel_guard (value TEXT NOT NULL); INSERT INTO sentinel_guard VALUES ('independent synthetic data');");
                string[] beforeTables = Tables(sentinel);
                byte[] beforeBytes = File.ReadAllBytes(sentinel);
                string candidatePath = Path.Combine(primary, VersionIndex.CacheDirectory, VersionIndex.DatabaseName + suffix);
                alias = candidatePath;
                bool injected = false, blocked = false, attempted = false, reachedBoundary = false, rejected = false;
                string label = (suffix.Length == 0 ? "DB" : suffix[1..].ToUpperInvariant()) +
                    (afterPreflight ? "-after-preflight" : "-preflight-control");
                void Substitute()
                {
                    attempted = true;
                    try
                    {
                        if (File.Exists(candidatePath)) File.Move(candidatePath, candidatePath + ".owned-original");
                    }
                    catch (IOException error) when (VersionJob.SharingViolation(error) || (error.HResult & 0xffff) == 32)
                    { blocked = true; throw; }
                    if (!CreateHardLink(candidatePath, sentinel, 0))
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a synthetic SQLite boundary hardlink.");
                    injected = true;
                }
                if (!afterPreflight) Substitute();
                var candidate = new VersionIndex(() => new DirectoryFence(primary), () =>
                {
                    reachedBoundary = true;
                    if (afterPreflight) Substitute();
                });
                try { candidate.Ensure(VersionIndex.Key(after, before), () => new(after, before)); }
                catch (Exception error) when (VersionIndex.IsUnavailable(error)) { rejected = true; }
                if (!attempted || !injected && !blocked || blocked && !rejected || afterPreflight && !reachedBoundary || !afterPreflight && reachedBoundary)
                    throw new InvalidOperationException("The synthetic substitution did not execute at its intended boundary: " + label);
                // All SQLite handles have closed. Remove only the alias we created before inspecting the independent name.
                if (injected) File.Delete(candidatePath);
                alias = null;
                byte[] afterBytes = File.ReadAllBytes(sentinel);
                string[] afterTables;
                try { afterTables = Tables(sentinel); }
                catch (IOException) { afterTables = ["<unreadable after SQLite operation>"]; }
                bool sameBytes = beforeBytes.AsSpan().SequenceEqual(afterBytes);
                bool sameTables = beforeTables.SequenceEqual(afterTables, StringComparer.Ordinal);
                Console.Error.WriteLine($"SQLite boundary case={label}; rejected={rejected}; swap_blocked={blocked}; sentinel_bytes_same={sameBytes}; sentinel_tables_same={sameTables}.");
                if (!afterPreflight && !rejected) failures.Add(label + " did not reject the pre-existing hardlink.");
                if (!sameBytes || !sameTables) failures.Add(label + " changed the independent synthetic sentinel" + (rejected ? " before reporting an error." : "."));
                string cache = Path.Combine(primary, VersionIndex.CacheDirectory);
                Directory.Move(cache, cache + ".released"); // A rejected/open/closed VFS operation must release every pinned handle.
            }
            finally
            {
                if (alias is not null) File.Delete(alias);
                Clean(workspace);
            }
        }
    }

    private static void CheckNormalUse()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            var catalog = Catalog("normal synthetic index");
            string key = VersionIndex.Key(catalog, null);
            var index = new VersionIndex(workspace);
            index.Ensure(key, () => new(catalog, null));
            CheckContents(index);
            var reopened = new VersionIndex(workspace);
            reopened.Ensure(key, () => throw new InvalidOperationException("The existing verified cache was rebuilt."));
            CheckContents(reopened);
            string cache = Path.Combine(workspace.Root, VersionIndex.CacheDirectory);
            Directory.Move(cache, cache + ".owned-old");
            reopened.Ensure(key, () => new(catalog, null));
            CheckContents(reopened);
            Directory.Move(cache, cache + ".released");
            Console.Error.WriteLine("PASS SQLite pinning preserves Ensure/Children/Page, cache reuse, reopening, rebuilding and handle release.");

            void CheckContents(VersionIndex candidate)
            {
                var children = candidate.Children(key, "", false);
                var page = candidate.Page(key, "", false, "synthetic", 0, 10);
                if (children is not [{ Path: "synthetic.txt", IsDirectory: false }] ||
                    !page.Entries.SequenceEqual(children) || page.HasMore)
                    throw new InvalidOperationException("The pinned index changed its normal query results.");
            }
        }
        finally { Clean(workspace); }
    }

    private static void CheckForeignDatabases()
    {
        foreach (int applicationId in new[] { 0, 1234 })
        {
            var workspace = LabWorkspace.Create();
            try
            {
                var catalog = Catalog("foreign database control");
                string key = VersionIndex.Key(catalog, null);
                var index = new VersionIndex(workspace);
                index.Ensure(key, () => new(catalog, null));
                string cache = Path.Combine(workspace.Root, VersionIndex.CacheDirectory);
                foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
                    File.Delete(Path.Combine(cache, VersionIndex.DatabaseName + suffix)); // Only this probe's settled index files.
                string database = Path.Combine(cache, VersionIndex.DatabaseName);
                using (var foreign = new LocalSqlite(database, create: true))
                    foreign.Execute($"PRAGMA application_id={applicationId}; CREATE TABLE foreign_guard (value TEXT NOT NULL); INSERT INTO foreign_guard VALUES ('preserve foreign schema');");
                byte[] original = File.ReadAllBytes(database);
                string[] snapshot = Image(cache);
                bool rejected = false;
                try { index.Ensure(key, () => new(catalog, null)); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected || !original.AsSpan().SequenceEqual(File.ReadAllBytes(database)) || !snapshot.SequenceEqual(Image(cache), StringComparer.Ordinal))
                    throw new InvalidOperationException("The index accepted or changed a foreign database before ownership validation.");
                using (var foreign = new LocalSqlite(database, create: false))
                using (var query = foreign.Prepare("SELECT value FROM foreign_guard"))
                    if (!query.Step() || query.Text(0) != "preserve foreign schema" || query.Step())
                        throw new InvalidOperationException("The foreign database guard changed.");
                Directory.Move(cache, cache + ".released");
            }
            finally { Clean(workspace); }
        }
        Console.Error.WriteLine("PASS foreign nonempty schema/application ID are rejected before changing database bytes, with handles released.");
    }

    private static void Clean(LabWorkspace workspace)
    {
        string root = Path.GetFullPath(workspace.Root);
        if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
            !Path.GetFileName(root).StartsWith(LabWorkspace.Prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Refusing cleanup outside the owned SQLite audit workspace.");
        using (workspace.OpenRegisteredRoot()) { }
        Directory.Delete(root, recursive: true);
    }

    private static void CheckForeignPendingWal()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            var catalog = Catalog("foreign WAL control");
            string key = VersionIndex.Key(catalog, null);
            var index = new VersionIndex(workspace);
            index.Ensure(key, () => new(catalog, null));
            string cache = Path.Combine(workspace.Root, VersionIndex.CacheDirectory);
            foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
                File.Delete(Path.Combine(cache, VersionIndex.DatabaseName + suffix));
            string source = Path.Combine(workspace.Root, "independent-foreign.db");
            using (var writer = new LocalSqlite(source, create: true, persistWal: true))
            {
                writer.Execute("CREATE TABLE foreign_guard (value TEXT NOT NULL); INSERT INTO foreign_guard VALUES ('baseline'); PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
                using var reader = new LocalSqlite(source, create: false, persistWal: true);
                reader.Execute("BEGIN; SELECT value FROM foreign_guard;");
                writer.Execute("INSERT INTO foreign_guard VALUES ('committed only in WAL');");
                string database = Path.Combine(cache, VersionIndex.DatabaseName);
                Copy(source, database);
                Copy(source + "-wal", database + "-wal");
                byte[] beforeDatabase = File.ReadAllBytes(database), beforeWal = File.ReadAllBytes(database + "-wal");
                string[] snapshot = Image(cache);
                if (beforeWal.Length <= 32) throw new InvalidOperationException("The foreign WAL fixture has no committed frames.");
                bool rejected = false;
                try { index.Ensure(key, () => new(catalog, null)); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected || !beforeDatabase.AsSpan().SequenceEqual(File.ReadAllBytes(database)) ||
                    !beforeWal.AsSpan().SequenceEqual(File.ReadAllBytes(database + "-wal")) || !snapshot.SequenceEqual(Image(cache), StringComparer.Ordinal))
                    throw new InvalidOperationException("A foreign database/WAL was opened or checkpointed before ownership validation.");
                Directory.Move(cache, cache + ".released");
            }
            Console.Error.WriteLine("PASS a foreign DB with committed WAL frames is rejected before VFS recovery/checkpoint; both byte images remain intact.");
        }
        finally { Clean(workspace); }

        static void Copy(string source, string destination)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }
    }

    private static string[] Image(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))
        .Concat(Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path) + "/"))
        .Order(StringComparer.Ordinal).ToArray();

    private static CatalogSnapshot Catalog(string contents)
    {
        var binding = new DestinationBinding { Version = 1, Marker = Guid.NewGuid(), Identity = new byte[24], LastVolumePath = "" };
        var plan = new BatchPlan("", "", "", [], binding, [], [new("synthetic.txt", contents.Length, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents)))]);
        string generation = VersionStore.NewGenerationId();
        using var stream = new MemoryStream();
        _ = BatchCatalog.WriteMetadata(stream, plan, generation);
        stream.Position = 0;
        return BatchCatalog.Parse(stream, generation);
    }

    private static string[] Tables(string path)
    {
        using var db = new LocalSqlite(path, create: false);
        var rows = new List<string>();
        using (var integrity = db.Prepare("PRAGMA quick_check"))
            while (integrity.Step()) rows.Add("integrity=" + integrity.Text(0));
        using (var views = db.Prepare("SELECT key FROM views ORDER BY key"))
            while (views.Step()) rows.Add("view=" + views.Text(0));
        using (var guard = db.Prepare("SELECT value FROM sentinel_guard ORDER BY value"))
            while (guard.Step()) rows.Add("guard=" + guard.Text(0));
        return rows.ToArray();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string alias, string target, nint security);
}
