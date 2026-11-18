using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

internal static class ProductBackupRecoveryProbe
{
    internal static int Run()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var fixture = LabWorkspace.Create();
        bool passed = false;
        try
        {
            foreach (var mode in Enum.GetValues<ProductBackupMode>()) Test(mode);
            CheckRegistryCoordination();
            passed = true;
            Console.WriteLine("PASS standalone product recovery restored two dates of all modes with source and registry absent, denied backup writes, checked replicas and hashes, and preserved occupied outputs.");
            return 0;
        }
        finally
        {
            if (passed)
            {
                using (fixture.OpenRegisteredRoot()) { }
                string root = System.IO.Path.GetFullPath(fixture.Root);
                if (System.IO.Path.GetDirectoryName(root) != System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath()))
                    throw new IOException("Refusing cleanup outside the synthetic temporary directory.");
                Directory.Delete(root, recursive: true);
            }
            else Console.Error.WriteLine("Synthetic recovery fixture preserved at " + fixture.Root);
        }

        void Test(ProductBackupMode mode)
        {
            string scope = Folder(mode.ToString());
            string source = Directory.CreateDirectory(System.IO.Path.Combine(scope, "source")).FullName;
            string targets = Directory.CreateDirectory(System.IO.Path.Combine(scope, "targets")).FullName;
            string settings = Directory.CreateDirectory(System.IO.Path.Combine(scope, "settings")).FullName;
            byte[] first = "first synthetic version"u8.ToArray(), second = "second synthetic version"u8.ToArray();
            File.WriteAllBytes(System.IO.Path.Combine(source, "sample.txt"), first);
            File.WriteAllText(System.IO.Path.Combine(source, "removed.txt"), "keep the earlier date");
            Directory.CreateDirectory(System.IO.Path.Combine(source, "empty"));
            ProductJob job;
            string initial, next;
            using (var registry = new ProductJobRegistry(settings))
            {
                job = registry.Create(new(mode.ToString(), mode, null), LocalJobRoots.ReviewDedicatedDestination(source, targets));
                var result = ProductRunner.Run(registry, job.Id);
                Check(result.State == ProductRunState.Confirmed && result.Generation is not null, "Initial synthetic backup failed.");
                initial = result.Generation!;
                File.WriteAllBytes(System.IO.Path.Combine(source, "sample.txt"), second);
                File.Delete(System.IO.Path.Combine(source, "removed.txt"));
                var store = VersionStore.OpenProduct(job);
                if (mode == ProductBackupMode.Versions)
                {
                    next = VersionStore.NewGenerationId();
                    store.CreatePoint(job.Source.VolumePath, next);
                }
                else next = store.BeginMirror(store.PreviewMirror(job.Source.VolumePath), emptyReviewed: true, massReviewed: true);
            }
            Directory.Move(source, source + "-absent");
            Directory.Move(settings, settings + "-absent");
            string backup = job.Destination.LastVolumePath;
            var original = Snapshot(backup);
            var folder = new DirectoryInfo(backup);
            string permissions = folder.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            var restricted = folder.GetAccessControl(AccessControlSections.Access);
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException("Missing synthetic ACL identity.");
            restricted.AddAccessRule(new FileSystemAccessRule(user,
                FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
            folder.SetAccessControl(restricted);
            try
            {
                var reader = ProductBackupRecovery.Open(backup);
                Check(reader.RegistryProblem() is null && reader.ListPoints().Length == 2, "Standalone recovery lost confirmed dates.");
                foreach (var (generation, content) in new[] { (initial, first), (next, second) })
                {
                    string output = Folder(mode + "-" + generation);
                    reader.Restore(generation, output);
                    Check(!Directory.EnumerateFileSystemEntries(output, ".foldertwin-*", SearchOption.AllDirectories).Any(),
                        "Standalone restoration left internal .foldertwin-* artifacts in output.");
                    Check(SHA256.HashData(File.ReadAllBytes(System.IO.Path.Combine(output, "sample.txt"))).AsSpan()
                        .SequenceEqual(SHA256.HashData(content)), "Independent restoration hash differs.");
                    Check(Directory.Exists(System.IO.Path.Combine(output, "empty")), "Empty directory was lost.");
                    Check(File.Exists(System.IO.Path.Combine(output, "removed.txt")) == (generation == initial || mode == ProductBackupMode.Accumulative),
                        "Restoration changed the mode's removal semantics.");
                    Reject(() => reader.Restore(generation, output));
                }
                string single = Folder(mode + "-single");
                reader.Restore(initial, single, "removed.txt");
                Check(File.Exists(System.IO.Path.Combine(single, "removed.txt")) && !File.Exists(System.IO.Path.Combine(single, "sample.txt")),
                    "Scoped restoration wrote unrelated files.");
                Reject(() => reader.Restore(initial, backup));
                var guarded = VersionStore.OpenProductRecovery(backup);
                Check(!guarded.SupportsRetention, "Read-only recovery advertised retention.");
                Reject(() => guarded.PreviewRetention(new RetentionPolicy()));
            }
            finally
            {
                var restoredPermissions = new DirectorySecurity();
                restoredPermissions.SetSecurityDescriptorSddlForm(permissions, AccessControlSections.Access);
                folder.SetAccessControl(restoredPermissions);
            }
            Check(original.OrderBy(item => item.Key).SequenceEqual(Snapshot(backup).OrderBy(item => item.Key)),
                "Standalone recovery wrote to its backup.");

            string primary = System.IO.Path.Combine(backup, "manifests", initial + ".jsonl");
            byte[] catalog = File.ReadAllBytes(primary);
            File.WriteAllText(primary, "corrupt synthetic replica");
            var degraded = ProductBackupRecovery.Open(backup);
            Check(degraded.ReadCatalog(initial).Degraded && degraded.Verify(initial), "One damaged replica did not retain a degraded reader.");
            degraded.Restore(initial, Folder(mode + "-degraded"));
            File.WriteAllBytes(primary, catalog);
            string ledger = System.IO.Path.Combine(backup, GenerationRegistry.PrimaryName);
            byte[] history = File.ReadAllBytes(ledger);
            File.WriteAllText(ledger, "damaged synthetic ledger");
            var independent = ProductBackupRecovery.Open(backup);
            Check(independent.RegistryProblem() is not null, "A damaged registry was hidden.");
            independent.Restore(initial, Folder(mode + "-independent"));
            File.WriteAllBytes(ledger, history);

            var point = independent.ReadCatalog(initial);
            var file = point.Files.Single(item => item.RelativePath == "sample.txt");
            string digest = Convert.ToHexStringLower(file.Hash);
            string objectPath = System.IO.Path.Combine(backup, "objects", "sha256", digest[..2], digest);
            byte[] bytes = File.ReadAllBytes(objectPath);
            File.WriteAllText(objectPath, "corrupt object");
            string rejected = Folder(mode + "-corrupt");
            Reject(() => independent.Restore(initial, rejected));
            Check(!Directory.EnumerateFileSystemEntries(rejected).Any(), "Corrupt data wrote to the recovery destination.");
            File.WriteAllBytes(objectPath, bytes);
            string marker = System.IO.Path.Combine(backup, DestinationBinding.MarkerName);
            byte[] identity = File.ReadAllBytes(marker);
            File.WriteAllBytes(marker, Guid.NewGuid().ToByteArray());
            Reject(() => ProductBackupRecovery.Open(backup));
            File.WriteAllBytes(marker, identity);
        }

        string Folder(string name) => Directory.CreateDirectory(System.IO.Path.Combine(fixture.Root, name)).FullName;

        void CheckRegistryCoordination()
        {
            string source = Folder("coordinated-source"), targets = Folder("coordinated-backups");
            string settings = Folder("coordinated-registry"), outputParent = Folder("coordinated-output");
            File.WriteAllText(System.IO.Path.Combine(source, "sample.txt"), "emergency recovery bytes");
            using var registry = new ProductJobRegistry(settings);
            var job = registry.Create(new("Healthy recovery", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, targets));
            var point = ProductRunner.Run(registry, job.Id);
            Check(point.State == ProductRunState.Confirmed, "The coordinated backup failed.");
            var output = LocalJobRoots.ReviewDedicatedDestination(job.Destination.LastVolumePath, outputParent);
            using (var running = registry.StartRun(job.Id))
            {
                Reject(() => { using var blocked = registry.BeginIndependentRecovery(output); });
                running.Finish(ProductRunState.Unchanged);
            }
            var draft = LocalJobRoots.ReviewDedicatedDestination(source, targets);
            try
            {
                registry.Create(new("Pending registration", ProductBackupMode.Versions, null), draft,
                    step => { if (step == ProductRegistrationStep.Prepared) throw new InjectedStopException(); });
                throw new InvalidDataException("The synthetic registration did not stop.");
            }
            catch (InjectedStopException) { }
            using (var exclusion = registry.BeginIndependentRecovery(output))
            {
                Reject(() => { using var blocked = registry.StartRun(job.Id); });
                using var roots = LocalJobRoots.CreateDedicatedDestination(output);
                ProductBackupRecovery.Open(job.Destination.LastVolumePath).Restore(point.Generation!, roots.Review.Destination.VolumePath);
            }
            string intent = System.IO.Path.Combine(settings, draft.FolderId.ToString("N"), "intent.json");
            File.WriteAllText(intent, "damaged synthetic registration");
            Reject(() => { using var blocked = registry.BeginIndependentRecovery(output); });
            var isolated = LocalJobRoots.ReviewDedicatedDestination(job.Destination.LastVolumePath,
                System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath())!);
            using var exclusionForUnknownRoots = registry.BeginIndependentRecovery(isolated);
            string? created = null;
            try
            {
                using var roots = LocalJobRoots.CreateDedicatedDestination(isolated);
                created = roots.Review.Destination.VolumePath;
                ProductBackupRecovery.Open(job.Destination.LastVolumePath).Restore(point.Generation!, created);
                Check(File.ReadAllText(System.IO.Path.Combine(created, "sample.txt")) == "emergency recovery bytes" &&
                    File.ReadAllText(intent) == "damaged synthetic registration", "Damaged registration affected independent recovery.");
            }
            finally
            {
                if (created is not null)
                {
                    string expected = System.IO.Path.Combine(isolated.Parent.VolumePath, isolated.FolderName);
                    if (!created.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected synthetic recovery output.");
                    using (var verified = new DirectoryFence(created)) { }
                    Directory.Delete(created, recursive: true);
                }
            }
            Console.WriteLine("PASS emergency recovery excludes active workers, accepts pending jobs, and recovers beside unknown roots while preserving damaged configuration.");
        }
    }

    private sealed class InjectedStopException : Exception;

    private static Dictionary<string, string> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => System.IO.Path.GetRelativePath(root, path), path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidDataException("An unsafe standalone recovery operation was accepted.");
    }
}
