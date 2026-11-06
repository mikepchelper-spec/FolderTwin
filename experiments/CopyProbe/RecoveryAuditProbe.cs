using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

// Independent, bounded expectations for calendar selection and shared-object recovery.
// Does not use the store's reference calculation or catalog reader as its oracle.
internal static class RecoveryAuditProbe
{
    private sealed record Point(string Id, DateTimeOffset Date);
    private sealed record Payload(byte[] Bytes, DateTime LastWriteUtc);
    private sealed record Snapshot(Dictionary<string, Payload> Files, HashSet<string> Directories);
    private const int Seed = 58203;

    internal static int Run()
    {
        var timer = Stopwatch.StartNew();
        int calendars = CheckCalendars();
        var workspace = LabWorkspace.Create();
        try
        {
            using (var root = workspace.OpenRegisteredRoot())
                Check(LabWorkspace.AvailableSpace(root.Root) >= 256UL * 1024 * 1024,
                    "Recovery audit requires at least 256 MiB free; no fixture data was generated.");

            string source = Child("source");
            string destination = Child("store");
            var store = VersionStore.Initialize(workspace, destination);
            var now = new DateTimeOffset(2028, 3, 1, 12, 0, 0, TimeSpan.Zero);
            var points = new List<Point>();
            var snapshots = new List<Snapshot>();
            int[] ages = [400, 32, 8, 2, 1, 0];
            for (int index = 0; index < ages.Length; index++)
            {
                Budget();
                DateTimeOffset date = now.AddDays(-ages[index]);
                int content = Math.Min(index, 4); // Last two points share bytes but not timestamps.
                Directory.CreateDirectory(Path.Combine(source, "Carpeta vacía"));
                Directory.CreateDirectory(Path.Combine(source, "Documentos á漢字"));
                Write("shared.txt", "shared bytes", date.UtcDateTime);
                Write(Path.Combine("Documentos á漢字", "árbol.txt"), "shared bytes", date.AddMinutes(1).UtcDateTime);
                Write("variant.txt", "variant-" + (content % 2), date.UtcDateTime);
                Write("unique.txt", "payload-" + content, date.UtcDateTime);
                Write("empty.bin", "", date.UtcDateTime);
                if (index == 0) Write("removed.txt", "recover the removed path", date.UtcDateTime);
                else if (File.Exists(Path.Combine(source, "removed.txt"))) File.Delete(Path.Combine(source, "removed.txt"));
                var point = new Point(Id(date, index + 1), date);
                var expected = Capture(source);
                Check(expected.Files.Values.Sum(file => file.Bytes.Length) <= 1024 * 1024,
                    "Synthetic payload exceeded the 1 MiB campaign budget.");
                points.Add(point); snapshots.Add(expected);
                store.CreatePoint(source, point.Id);
            }

            var policy = new RetentionPolicy(1, 0, 0, 1);
            var review = store.PreviewRetention(policy, now);
            var keep = CalendarOracle(points.ToArray(), policy, now);
            Check(review.Keep.ToHashSet(StringComparer.Ordinal).SetEquals(keep), "Store retention differs from the calendar oracle.");
            Check(review.Expire.ToHashSet(StringComparer.Ordinal).SetEquals(points.Where(point => !keep.Contains(point.Id)).Select(point => point.Id)),
                "Store expiration differs from the calendar oracle.");
            var active = Digests(snapshots[^1]);
            var all = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots)
                foreach (var item in Digests(snapshot)) all[item.Key] = item.Value;
            var retired = all.Keys.Where(digest => !active.ContainsKey(digest)).ToHashSet(StringComparer.Ordinal);
            Check(review.Objects.ToHashSet(StringComparer.Ordinal).SetEquals(retired) &&
                review.QuarantineBytes == retired.Sum(digest => (long)all[digest].Length),
                "The reference union or quarantine bytes differ from independent payload hashes.");

            foreach (var offset in new[] { TimeSpan.FromHours(14), TimeSpan.FromHours(-12) })
            {
                var other = store.PreviewRetention(policy, now.ToOffset(offset));
                Check(other.Keep.SequenceEqual(review.Keep) && other.Expire.SequenceEqual(review.Expire) &&
                    other.Objects.SequenceEqual(review.Objects) && other.ReviewedUtc == now,
                    "The public retention review changed across equivalent UTC instants.");
            }

            string detached = Path.Combine(workspace.Root, "source absent");
            workspace.CheckChild(source); workspace.CheckChild(detached);
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Move(source, detached);
            string operation = store.BeginRetention(review, clock: now);
            Recover(points.Count - 1, "retained during quarantine");
            // The independent reader may recover an expired point if all its bytes
            // remain in active CAS. The product UI still requires undo for expired dates.
            Recover(points.Count - 2, "shared expired independent reader");
            RejectRestore(0, "expired exclusive unavailable");
            store.UndoRetention(operation);
            for (int index = 0; index < points.Count; index++) Recover(index, "after undo " + index);

            Budget();
            string next = store.BeginRetention(store.PreviewRetention(policy, now), clock: now);
            store.PurgeRetention(next, clock: now.AddDays(2));
            Check(store.ListRetention().Single(item => item.Id == next).State == "purged", "The synthetic purge did not finish.");
            Recover(points.Count - 1, "retained after purge");
            Recover(points.Count - 2, "shared expired after purge");
            RejectRestore(0, "purged exclusive unavailable");

            // New composition: corrupt shared data after a purge; verification,
            // restore and another cleanup review must all stop without the source.
            byte[] shared = snapshots[^1].Files["shared.txt"].Bytes;
            string digest = Convert.ToHexStringLower(SHA256.HashData(shared));
            string objectPath = Path.Combine(destination, "objects", "sha256", digest[..2], digest);
            File.WriteAllBytes(objectPath, "injected corruption"u8.ToArray());
            Reject(() => store.Verify(points[^1].Id), "Shared corruption was accepted by verification.");
            RejectRestore(points.Count - 1, "corrupt shared output");
            Reject(() => store.PreviewRetention(policy, now.AddDays(2)), "Cleanup accepted corrupt retained data.");
            File.WriteAllBytes(objectPath, shared); // Restore only the exact synthetic bytes injected above.
            Check(!store.Verify(points[^1].Id), "The repaired synthetic control did not verify.");
            Compare(detached, snapshots[^1]);
            Budget();
            Console.WriteLine($"PASS recovery audit seed={Seed}: {calendars} independent calendar selections; 6 points, UTC offset equivalence, independently hashed reference union, offline quarantine/undo/purge, shared-expired rescue and corruption fail-closed; seconds={timer.Elapsed.TotalSeconds:F3}.");
            Console.WriteLine("LIMIT independent CAS recovery can read an expired point whose objects remain active; this is not permission to bypass the product UI's expiry review or purge missing data.");
            return 0;

            string Child(string name)
            {
                string path = Path.Combine(workspace.Root, name);
                workspace.CheckChild(path);
                return Directory.CreateDirectory(path).FullName;
            }
            void Write(string relative, string content, DateTime timestamp)
            {
                string path = Path.Combine(source, relative);
                File.WriteAllText(path, content);
                File.SetLastWriteTimeUtc(path, timestamp);
            }
            void Recover(int index, string name)
            {
                Budget();
                string output = Child(name);
                store.Restore(points[index].Id, output);
                Compare(output, snapshots[index]);
            }
            void RejectRestore(int index, string name)
            {
                Budget();
                string output = Child(name);
                Reject(() => store.Restore(points[index].Id, output), "An unavailable object was accepted by restore.");
                Check(!Directory.EnumerateFileSystemEntries(output).Any(), "Failed verification wrote into the restore output.");
            }
            void Budget()
            {
                Check(timer.Elapsed < TimeSpan.FromSeconds(60), "Recovery audit exceeded its 60-second budget.");
            }
        }
        finally
        {
            // Reopen validates absolute temporary parent, name, physical identity
            // and marker before recursively removing this newly-created fixture.
            var owned = LabWorkspace.Open(workspace.Root);
            using (owned.OpenRegisteredRoot()) { }
            Directory.Delete(owned.Root, recursive: true);
        }
    }

    private static int CheckCalendars()
    {
        DateTimeOffset[] clocks = [
            new(2027, 1, 1, 12, 0, 0, TimeSpan.Zero),
            new(2028, 3, 1, 12, 0, 0, TimeSpan.Zero),
            new(2028, 12, 31, 23, 59, 59, TimeSpan.Zero),
            new(2029, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new(2040, 2, 29, 12, 0, 0, TimeSpan.Zero)
        ];
        RetentionPolicy[] policies = [new(1, 0, 0, 1), new(2, 1, 1, 7), new(), new(3660, 520, 120, 365)];
        var random = new Random(Seed);
        int count = 0, serial = 100;
        foreach (var now in clocks)
        {
            var dates = new List<DateTimeOffset>();
            foreach (int age in new[] { 740, 400, 366, 365, 64, 33, 32, 31, 30, 29, 15, 8, 7, 6, 2, 1, 0 })
            {
                DateTimeOffset midnight = new(now.UtcDateTime.Date.AddDays(-age), TimeSpan.Zero);
                dates.Add(midnight);
                if (midnight.AddHours(1) <= now) dates.Add(midnight.AddHours(1));
            }
            dates.Add(now);
            var points = dates.Select(date => new Point(Id(date, ++serial), date)).ToArray();
            for (int index = points.Length - 1; index > 0; index--)
            {
                int other = random.Next(index + 1);
                (points[index], points[other]) = (points[other], points[index]);
            }
            foreach (var order in new[] { points, points.Reverse().ToArray() })
            foreach (var policy in policies)
            {
                var expected = CalendarOracle(order, policy, now);
                var actual = policy.Select(order.Select(point => point.Id).ToArray(), now);
                Check(actual.SetEquals(expected), $"Calendar oracle disagrees at {now:O} for {policy}.");
                count++;
            }
        }
        return count;
    }

    private static HashSet<string> CalendarOracle(Point[] points, RetentionPolicy policy, DateTimeOffset now)
    {
        // DateOnly day zero is a Monday. Integer day/week/month ages avoid the
        // production algorithm's ISOWeek helpers, date thresholds and GroupBy.
        int today = DateOnly.FromDateTime(now.UtcDateTime).DayNumber;
        int month = now.Year * 12 + now.Month;
        var days = new HashSet<int>(); var weeks = new HashSet<int>(); var months = new HashSet<int>();
        var kept = new HashSet<string>(StringComparer.Ordinal);
        if (points.Length > 0) kept.Add(points[^1].Id);
        foreach (var point in points.Reverse())
        {
            Check(point.Date <= now, "The calendar oracle input contains a future point.");
            int day = DateOnly.FromDateTime(point.Date.UtcDateTime).DayNumber;
            int pointMonth = point.Date.Year * 12 + point.Date.Month;
            if (today - day < policy.DailyDays && days.Add(day)) kept.Add(point.Id);
            if (today / 7 - day / 7 < policy.WeeklyWeeks && weeks.Add(day / 7)) kept.Add(point.Id);
            if (month - pointMonth < policy.MonthlyMonths && months.Add(pointMonth)) kept.Add(point.Id);
        }
        return kept;
    }

    private static string Id(DateTimeOffset date, int serial) =>
        date.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture) + "-" + serial.ToString("x32", CultureInfo.InvariantCulture);

    private static Snapshot Capture(string root) => new(
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => Path.GetRelativePath(root, path),
            path => new Payload(File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)), StringComparer.Ordinal),
        Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path)).ToHashSet(StringComparer.Ordinal));

    private static Dictionary<string, byte[]> Digests(Snapshot snapshot)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var payload in snapshot.Files.Values)
            result[Convert.ToHexStringLower(SHA256.HashData(payload.Bytes))] = payload.Bytes;
        return result;
    }

    private static void Compare(string root, Snapshot expected)
    {
        var actual = Capture(root);
        string[] payloadNames = actual.Files.Keys.Where(path => !Path.GetFileName(path).StartsWith(".foldertwin-", StringComparison.Ordinal)).ToArray();
        Check(payloadNames.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Files.Keys) && actual.Directories.SetEquals(expected.Directories),
            "Restored paths differ from the independent source snapshot.");
        foreach (var (path, payload) in expected.Files)
            Check(actual.Files.TryGetValue(path, out var file) && file.Bytes.AsSpan().SequenceEqual(payload.Bytes) && file.LastWriteUtc == payload.LastWriteUtc,
                "Restored bytes or modification time differ from the captured synthetic payload: " + path);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
