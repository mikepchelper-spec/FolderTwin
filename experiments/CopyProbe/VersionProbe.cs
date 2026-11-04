using System.Security.Cryptography;

internal static class VersionProbe
{
    internal static int Run()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            string source = NewFolder("origen");
            string destination = NewFolder("versionado");
            string outputA = NewFolder("restauración anterior");
            string outputB = NewFolder("restauración reciente");
            string failedOutput = NewFolder("sin escrituras");
            Directory.CreateDirectory(Path.Combine(source, "carpeta vacía"));
            string shared = Path.Combine(source, "igual.bin");
            string duplicate = Path.Combine(source, "duplicado ñ.bin");
            string changed = Path.Combine(source, "cambio.txt");
            string removed = Path.Combine(source, "borrado.txt");
            byte[] common = [1, 3, 5, 7];
            File.WriteAllBytes(shared, common);
            File.WriteAllBytes(duplicate, common);
            var stamp = new DateTime(2005, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(1234);
            File.SetLastWriteTimeUtc(duplicate, stamp);
            File.SetAttributes(duplicate, FileAttributes.Hidden);
            File.WriteAllText(changed, "primera versión");
            File.WriteAllText(removed, "todavía recuperable");
            var store = VersionStore.Initialize(workspace, destination);
            string first = VersionStore.NewGenerationId();
            store.CreatePoint(source, first);
            Check(ObjectCount() == 3 && store.ListPoints().Single().Id == first, "The first generation did not deduplicate identical paths.");

            File.Delete(removed);
            File.WriteAllText(changed, "segunda versión");
            string second = VersionStore.NewGenerationId();
            try
            {
                store.CreatePoint(source, second, step =>
                {
                    if (step == CatalogStep.PrimaryPublished)
                        throw new IOException("Injected interruption between replicas.");
                });
                throw new InvalidOperationException("The publication interruption did not occur.");
            }
            catch (IOException error) when (error.Message == "Injected interruption between replicas.") { }
            store = VersionStore.Open(LabWorkspace.Open(workspace.Root), destination);
            Check(store.ListPoints().Single().Id == first && ObjectCount() == 4,
                "An unconfirmed generation became visible or duplicated unchanged objects.");
            Reject(() => store.Restore(second, failedOutput));
            Check(!Directory.EnumerateFileSystemEntries(failedOutput).Any(), "An unconfirmed restore modified its output.");
            store.CreatePoint(source, second);
            var points = store.ListPoints();
            Check(points.Length == 2 && points[0].Id == second && points[1].Id == first &&
                points[0].FileCount == 3 && points[1].FileCount == 4 && ObjectCount() == 4,
                "Retry did not confirm both points with only four distinct objects.");
            Console.WriteLine("PASS two generations share identical objects; interrupted publication is invisible and retryable.");

            string missing = Path.Combine(workspace.Root, "origen apartado");
            Directory.Move(source, missing);
            string primary = Path.Combine(destination, "manifests", first + ".jsonl");
            File.WriteAllText(primary, "truncated");
            var earlier = store.Restore(first, outputA);
            Check(earlier.Degraded && !store.Restore(second, outputB).Degraded,
                "An intact replica did not restore the earlier generation.");
            Check(File.ReadAllText(Path.Combine(outputA, "cambio.txt")) == "primera versión" &&
                File.ReadAllText(Path.Combine(outputB, "cambio.txt")) == "segunda versión" &&
                File.ReadAllText(Path.Combine(outputA, "borrado.txt")) == "todavía recuperable" &&
                !File.Exists(Path.Combine(outputB, "borrado.txt")) &&
                Directory.Exists(Path.Combine(outputA, "carpeta vacía")) &&
                File.GetLastWriteTimeUtc(Path.Combine(outputA, Path.GetFileName(duplicate))) == stamp &&
                File.GetAttributes(Path.Combine(outputA, Path.GetFileName(duplicate))).HasFlag(FileAttributes.Hidden) &&
                !File.GetAttributes(Path.Combine(outputA, Path.GetFileName(shared))).HasFlag(FileAttributes.Hidden),
                "Restoration did not preserve the generation contents and metadata by path.");
            Console.WriteLine("PASS both dates restore without the source or an index; the earlier point uses an intact replica.");

            using (var conflicting = new MemoryStream())
            {
                var edited = earlier.Files.Select(file => file.RelativePath == "cambio.txt" ? file with { Length = file.Length + 1 } : file).ToArray();
                _ = BatchCatalog.WriteMetadata(conflicting,
                    new BatchPlan(workspace.Root, source, destination, [], earlier.Binding, earlier.Directories, edited), first);
                File.WriteAllBytes(primary, conflicting.ToArray());
            }
            Reject(() => store.Verify(first), "válidas pero diferentes");
            File.WriteAllText(primary, "truncated");

            string digest = Convert.ToHexStringLower(SHA256.HashData(common));
            string sharedObject = Path.Combine(destination, "objects", "sha256", digest[..2], digest);
            byte[] damaged = [2, 4, 6, 8];
            File.WriteAllBytes(sharedObject, damaged);
            Reject(() => store.Verify(first), "SHA-256");
            Reject(() => store.Verify(second), "SHA-256");
            Reject(() => store.Restore(second, failedOutput), "SHA-256");
            string third = VersionStore.NewGenerationId();
            Reject(() => store.CreatePoint(missing, third), "SHA-256");
            Check(!Directory.EnumerateFileSystemEntries(failedOutput).Any() &&
                !File.Exists(Path.Combine(destination, "commits", third + ".commit")) &&
                File.ReadAllBytes(sharedObject).SequenceEqual(damaged), "Corruption was overwritten or authorized a new point.");
            Reject(() => store.Verify(@"..\escape"));
            Console.WriteLine("PASS corruption in a shared object is reported for both points and blocks new publication without overwriting it.");
            return 0;

            int ObjectCount() => Directory.EnumerateFiles(Path.Combine(destination, "objects", "sha256"), "*", SearchOption.AllDirectories)
                .Count(path => Path.GetFileName(path).Length == SHA256.HashSizeInBytes * 2);
        }
        finally
        {
            Directory.Delete(workspace.Root, recursive: true); // The test owns every entry in the workspace.
        }

        string NewFolder(string name) => Directory.CreateDirectory(Path.Combine(workspace.Root, name)).FullName;
    }

    private static void Reject(Action operation, string? detail = null)
    {
        try { operation(); }
        catch (Exception error) when ((error is IOException or InvalidDataException or ArgumentException) &&
            (detail is null || error.Message.Contains(detail, StringComparison.Ordinal))) { return; }
        throw new InvalidOperationException("An invalid version operation was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
