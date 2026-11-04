using System.Diagnostics;
using System.Reflection;
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
            Directory.Delete(Path.Combine(destination, "pending")); // Older stores did not have this directory.
            Check(store.ListPending().Length == 0, "A store without the pending directory could not reopen.");
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
            Check(store.ListPending().Single() == new PendingVersion(second, source, 3),
                "The saved generation did not reopen with its original source and count.");
            Reject(() => store.Restore(second, failedOutput));
            Check(!Directory.EnumerateFileSystemEntries(failedOutput).Any(), "An unconfirmed restore modified its output.");
            string missing = Path.Combine(workspace.Root, "origen apartado");
            Directory.Move(source, missing);
            store.ResumePoint(second);
            var points = store.ListPoints();
            Check(points.Length == 2 && points[0].Id == second && points[1].Id == first &&
                points[0].FileCount == 3 && points[1].FileCount == 4 && ObjectCount() == 4 && store.ListPending().Length == 0,
                "Retry did not confirm both points with only four distinct objects.");
            Console.WriteLine("PASS interrupted publication reopens as pending and confirms without the original source when all objects exist.");

            string primary = Path.Combine(destination, "manifests", first + ".jsonl");
            File.WriteAllText(primary, "truncated");
            var earlier = store.Restore(first, outputA);
            Check(earlier.Degraded && !store.Restore(second, outputB).Degraded,
                "An intact replica did not restore the earlier generation.");
            string independent = NewFolder("recuperación independiente");
            RestoreInNewProcess(workspace.Root, Path.GetFileName(destination), first, Path.GetFileName(independent));
            Check(File.ReadAllText(Path.Combine(independent, "cambio.txt")) == "primera versión" &&
                File.ReadAllText(Path.Combine(independent, "borrado.txt")) == "todavía recuperable" &&
                File.GetLastWriteTimeUtc(Path.Combine(independent, Path.GetFileName(duplicate))) == stamp &&
                File.GetAttributes(Path.Combine(independent, Path.GetFileName(duplicate))).HasFlag(FileAttributes.Hidden) &&
                Directory.Exists(Path.Combine(independent, "carpeta vacía")),
                "The standalone recovery did not restore the earlier point and its metadata.");
            Console.WriteLine("PASS standalone recovery in a new process restores a full point with no source or index and one damaged replica.");
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
            CheckDurableIntent(workspace);
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

    private static void CheckDurableIntent(LabWorkspace workspace)
    {
        string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "origen recuperación")).FullName;
        string file = Path.Combine(source, "objeto.bin");
        byte[] content = [12, 34, 56];
        File.WriteAllBytes(file, content);
        foreach (var boundary in Enum.GetValues<PendingStep>())
        {
            string destination = Directory.CreateDirectory(Path.Combine(workspace.Root, boundary.ToString())).FullName;
            var store = VersionStore.Initialize(workspace, destination);
            string generation = VersionStore.NewGenerationId();
            Reject(() => store.CreatePoint(source, generation, pendingCheckpoint: step =>
            {
                if (step == boundary)
                    throw new IOException("Injected descriptor interruption.");
            }), "Injected descriptor interruption");
            store = VersionStore.Open(LabWorkspace.Open(workspace.Root), destination);
            Check(store.ListPoints().Length == 0 &&
                !Directory.EnumerateFiles(Path.Combine(destination, "objects"), "*", SearchOption.AllDirectories).Any(),
                "An interrupted descriptor allowed object writes or confirmation.");
            if (boundary == PendingStep.LocationPublished)
            {
                Check(store.ListPending().Single().Id == generation, "A completed descriptor was not discoverable.");
                string moved = Path.Combine(workspace.Root, "original apartado");
                Directory.Move(source, moved);
                try
                {
                    Directory.CreateDirectory(source);
                    File.WriteAllBytes(file, content);
                    Reject(() => store.ResumePoint(generation), "cambió desde el análisis");
                    Check(store.ListPoints().Length == 0, "A replacement source was accepted.");
                }
                finally
                {
                    File.Delete(file);
                    Directory.Delete(source);
                    Directory.Move(moved, source);
                }
                File.WriteAllBytes(file, [56, 34, 12]);
                Reject(() => store.ResumePoint(generation), "cambió desde el análisis");
                Check(!Directory.EnumerateFiles(Path.Combine(destination, "objects"), "*.tmp", SearchOption.AllDirectories).Any(),
                    "A changed source left an unverified object temporary.");
                File.WriteAllBytes(file, content);
                store.ResumePoint(generation);
            }
            else
            {
                Check(store.ListPending().Length == 0, "An incomplete descriptor was listed as resumable.");
                store.CreatePoint(source, generation);
            }
            Check(store.ListPoints().Single().Id == generation && store.ListPending().Length == 0,
                "Retry did not safely complete the generation.");
        }
        Console.WriteLine("PASS every descriptor transition is recoverable; a replacement source cannot fill a missing object.");

        string objectStore = Directory.CreateDirectory(Path.Combine(workspace.Root, "temporal recuperable")).FullName;
        var interrupted = VersionStore.Initialize(workspace, objectStore);
        string pending = VersionStore.NewGenerationId();
        Reject(() => interrupted.CreatePoint(source, pending, objectCheckpoint: (step, _) =>
        {
            if (step == CopyStep.Verified)
                throw new IOException("Injected object interruption.");
        }), "Injected object interruption");
        Directory.Move(source, Path.Combine(workspace.Root, "origen temporal apartado"));
        interrupted = VersionStore.Open(LabWorkspace.Open(workspace.Root), objectStore);
        Check(interrupted.ListPending().Single().Id == pending, "The object interruption lost its generation descriptor.");
        interrupted.ResumePoint(pending);
        interrupted.ResumePoint(pending);
        interrupted.Verify(pending);
        Check(interrupted.ListPoints().Length == 1 && interrupted.ListPending().Length == 0,
            "The verified temporary did not recover idempotently without the source.");
        Console.WriteLine("PASS a verified temporary object can be promoted without the source after reopening.");
    }

    private static void RestoreInNewProcess(string root, string store, string generation, string output)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in new[] { "--recover-version", root, store, generation, output })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start recovery process.");
        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            process.WaitForExit();
            throw new TimeoutException("Standalone recovery timed out.");
        }
        Check(process.ExitCode == 0 && process.StandardOutput.ReadToEnd().Contains("Degraded catalog replica = True", StringComparison.Ordinal),
            "Standalone recovery failed. " + process.StandardError.ReadToEnd());
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
