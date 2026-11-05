using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace CopyLab;

internal static class IndexScaleProbe
{
    internal static void Run()
    {
        const int count = 329_000;
        const int perDirectory = 256;
        var workspace = LabWorkspace.Create();
        using var process = Process.GetCurrentProcess();
        try
        {
            var binding = new DestinationBinding
            {
                Version = 1, Marker = Guid.NewGuid(), Identity = new byte[24], LastVolumePath = ""
            };
            string[] directories = Enumerable.Range(0, (count + perDirectory - 1) / perDirectory)
                .Select(index => $"grupo {index:D4}").ToArray();
            byte[] hash = SHA256.HashData("index scale fixture"u8);
            var files = Enumerable.Range(0, count).Select(index =>
                new BatchFile(Path.Combine(directories[index / perDirectory], $"dato ñ {index:D6}.bin"), 19, hash)).ToArray();
            var before = Snapshot(files);
            var edited = files.ToArray();
            edited[0] = edited[0] with { Hash = SHA256.HashData("modified fixture"u8) };
            edited[^1] = edited[^1] with { RelativePath = Path.Combine(directories[^1], "nuevo.bin") };
            var after = Snapshot(edited);
            var index = new VersionIndex(workspace);
            string key = VersionIndex.Key(after, before);
            Measure("index_import", () => index.Ensure(key, () => new(after, before)));
            Measure("index_cached", () => index.Ensure(key,
                () => throw new InvalidOperationException("A valid cached view was rebuilt.")));
            Measure("index_root", () =>
            {
                if (index.Children(key, "", false).Length != directories.Length ||
                    index.Children(key, "", true).Length != 2)
                    throw new InvalidOperationException("The root view did not isolate both changed folders.");
            });
            Measure("index_folder", () =>
            {
                var first = index.Children(key, directories[0], true);
                var last = index.Children(key, directories[^1], true);
                if (first is not [{ Change: VersionChange.Modified }] ||
                    last.Count(entry => entry.Change == VersionChange.Removed) != 1 ||
                    last.Count(entry => entry.Change == VersionChange.Added) != 1)
                    throw new InvalidOperationException("The indexed changes differed from the catalog changes.");
                int observed = directories.Sum(directory => index.Children(key, directory, false).Length);
                if (observed != count)
                    throw new InvalidOperationException("The indexed view lost files.");
            });
            Measure("index_pages_and_search", () =>
            {
                var first = index.Page(key, directories[0], false, "", 0, 250);
                var last = index.Page(key, directories[0], false, "", 250, 250);
                var unicode = index.Page(key, "", false, "DATO Ñ 00000", 0, 250);
                var removed = index.Page(key, "", false, files[^1].RelativePath, 0, 250);
                if (first.Entries.Length != 250 || !first.HasMore || last.Entries.Length != 6 || last.HasMore ||
                    first.Entries.Concat(last.Entries).Distinct().Count() != perDirectory || unicode.Entries.Length != 10 ||
                    removed.Entries is not [{ Change: VersionChange.Removed }])
                    throw new InvalidOperationException("Paging, Unicode search or deleted-file search lost indexed entries.");
            });
            string path = Path.Combine(workspace.Root, VersionIndex.CacheDirectory, VersionIndex.DatabaseName);
            Console.Error.WriteLine($"PASS indexed_files={count}; directories={directories.Length}; database_bytes={new FileInfo(path).Length}.");

            CatalogSnapshot Snapshot(BatchFile[] entries)
            {
                string generation = VersionStore.NewGenerationId();
                var plan = new BatchPlan(workspace.Root, "", "", [], binding, directories, entries);
                using var encoded = new MemoryStream();
                _ = BatchCatalog.WriteMetadata(encoded, plan, generation);
                encoded.Position = 0;
                return BatchCatalog.Parse(encoded, generation);
            }

            void Measure(string phase, Action operation)
            {
                var timer = Stopwatch.StartNew();
                operation();
                timer.Stop();
                process.Refresh();
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"{phase}; seconds={timer.Elapsed.TotalSeconds:F3}; peak_working_set_bytes={process.PeakWorkingSet64}."));
            }
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }
    }
}
