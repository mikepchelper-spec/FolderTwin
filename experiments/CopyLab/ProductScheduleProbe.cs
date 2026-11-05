using System.Diagnostics;
using System.IO;

namespace CopyLab;

internal static class ProductScheduleProbe
{
    internal static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("The schedule probe requires a disposable hosted Windows runner.");
        var fixture = LabWorkspace.Create();
        string? executable = null;
        string? updatedExecutable = null;
        var jobs = new List<ProductJob>();
        string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
            string parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "backups")).FullName;
            File.WriteAllText(Path.Combine(source, "dato ñ.txt"), "contenido del horario de producto");
            using var registry = new ProductJobRegistry(settings);
            using var scheduler = new WindowsSchedule();
            executable = LabInstallation.EnsureWorker(product: true);
            Check(LabInstallation.EnsureWorker(product: true) == executable, "The verified installation was not reused.");
            foreach (var mode in Enum.GetValues<ProductBackupMode>())
            {
                var job = registry.Create(new(mode.ToString(), mode, new TimeOnly(20, 30)),
                    LocalJobRoots.ReviewDedicatedDestination(source, parent));
                jobs.Add(job);
                scheduler.RegisterProduct(registry, job.Id, executable, DateTime.Now.AddSeconds(5), daily: false);
                Check(scheduler.ReadProduct(registry, job.Id) is { Enabled: true, MeetsPolicy: true }, "The native task policy did not verify.");
                var attempt = await WaitAsync(job, 0);
                Check(attempt.Result?.State == ProductRunState.Confirmed &&
                    !VersionStore.OpenProduct(job).Verify(attempt.Generation!), "A real product time trigger failed to verify its backup.");
                scheduler.RegisterProduct(registry, job.Id, executable, DateTime.Today.AddDays(1).AddHours(20).AddMinutes(30));
                registry.UpdateSettings(job.Id, job.Options.Name, new TimeOnly(19, 30), true, false);
                Check(scheduler.ReadProduct(registry, job.Id) is { MeetsPolicy: false }, "A changed hour left a stale task runnable.");
                registry.UpdateSettings(job.Id, job.Options.Name, new TimeOnly(20, 30), true, false);
            }
            var versioned = jobs.Single(job => job.Options.Mode == ProductBackupMode.Versions);
            long sequence = registry.RunHistory(versioned.Id).Runs[0].Intent.Sequence;
            // Keep the same volume-GUID prefix: Directory.Move compares textual roots.
            string detached = Path.Combine(Path.GetDirectoryName(versioned.Destination.LastVolumePath)!, "detached");
            Directory.Move(versioned.Destination.LastVolumePath, detached);
            ProductRunView failed;
            try
            {
                scheduler.RegisterProduct(registry, versioned.Id, executable, DateTime.Now.AddSeconds(5), daily: false);
                failed = await WaitAsync(versioned, sequence, waiting: true);
                Check(failed.Result?.State == ProductRunState.WaitingForDestination && failed.RetryRunning,
                    "An absent target did not preserve its incident and live retry.");
                scheduler.RunProductNow(registry, versioned.Id);
                await Task.Delay(1000);
                Check(registry.RunHistory(versioned.Id).Runs[0].Intent.Sequence == failed.Intent.Sequence,
                    "IgnoreNew started another worker during a deferred retry.");
            }
            finally { Directory.Move(detached, versioned.Destination.LastVolumePath); }
            var retried = await WaitAsync(versioned, failed.Intent.Sequence, limit: TimeSpan.FromMinutes(7));
            Check(retried.Result?.State == ProductRunState.Confirmed &&
                retried.Result.FinishedUtc - failed.Result!.FinishedUtc >= TimeSpan.FromMinutes(4.5) &&
                registry.RunHistory(versioned.Id).Runs.Any(run => run.Intent.Id == failed.Intent.Id && run.Result is { } saved &&
                    saved.IntentHash.AsSpan().SequenceEqual(failed.Result.IntentHash) && saved.State == failed.Result.State &&
                    saved.FinishedUtc == failed.Result.FinishedUtc && saved.Detail == failed.Result.Detail && !run.RetryRunning),
                "The deferred retry lost its incident or did not run after the configured wait.");
            Console.Error.WriteLine($"PASS product time triggers run all three modes; missing target retries after {(retried.Result!.FinishedUtc - failed.Result!.FinishedUtc).TotalSeconds:F1} seconds and preserves the incident.");

            Directory.Move(versioned.Destination.LastVolumePath, detached);
            try
            {
                scheduler.RegisterProduct(registry, versioned.Id, executable, DateTime.Now.AddSeconds(5), daily: false);
                var waiting = await WaitAsync(versioned, retried.Intent.Sequence, waiting: true);
                // Remove first: disabling must not leave a native schedule that can reactivate work.
                scheduler.RemoveProduct(registry, versioned.Id);
                registry.UpdateSettings(versioned.Id, "Deshabilitado", new TimeOnly(20, 30), false, false);
                await WaitReleasedAsync(registry, versioned.Id);
                Check(registry.RunHistory(versioned.Id).Runs[0].Intent.Id == waiting.Intent.Id &&
                    !registry.RunHistory(versioned.Id).Runs[0].RetryRunning && scheduler.ReadProduct(registry, versioned.Id) is null,
                    "Disabling a job failed to stop its deferred retry.");
            }
            finally { Directory.Move(detached, versioned.Destination.LastVolumePath); }
            Console.Error.WriteLine("PASS changing the hour detects stale policy; removing and disabling stops a live retry without deleting backup data.");
            CheckForeignOwnership(registry, jobs.First(job => job.Id != versioned.Id));

            string updatePackage = Directory.CreateDirectory(Path.Combine(fixture.Root, "updated-package")).FullName;
            foreach (string path in Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(updatePackage, Path.GetRelativePath(AppContext.BaseDirectory, path));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(path, target);
            }
            string updateNote = Path.Combine(updatePackage, "UPDATE-PROBE.txt");
            File.WriteAllText(updateNote, "Synthetic package revision for native update acceptance.\n");
            string manifest = Path.Combine(updatePackage, "SHA256SUMS.txt");
            var lines = File.ReadAllLines(manifest).Append(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(updateNote))) + "  UPDATE-PROBE.txt")
                .OrderBy(line => line[66..], StringComparer.Ordinal).ToArray();
            File.WriteAllText(manifest, string.Join("\n", lines) + "\n", new System.Text.UTF8Encoding(false));
            byte[] oldManifest = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(executable)!, "SHA256SUMS.txt"));
            updatedExecutable = ProductDeployment.Install(registry, registry.ReviewMaintenance(), () =>
                LabInstallation.EnsureAt(updatePackage, Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!));
            Check(updatedExecutable != executable && LabInstallation.IsInstalledWorker(updatedExecutable, product: true) &&
                oldManifest.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(executable)!, "SHA256SUMS.txt"))),
                "The native application update replaced its earlier installation.");
            foreach (var job in registry.List().Jobs)
                Check(job.Enabled ? scheduler.ReadProduct(registry, job.Id) is { Enabled: true, MeetsPolicy: true } task &&
                    task.Executable == updatedExecutable : scheduler.ReadProduct(registry, job.Id) is null,
                    "Updating the package lost schedule policy or re-enabled a disabled job.");
            var updatedJob = registry.List().Jobs.First(job => job.Enabled);
            long beforeUpdate = registry.RunHistory(updatedJob.Id).Runs[0].Intent.Sequence;
            scheduler.RunProductNow(registry, updatedJob.Id);
            var updatedAttempt = await WaitAsync(updatedJob, beforeUpdate);
            Check(updatedAttempt.Result?.State == ProductRunState.Confirmed && !VersionStore.OpenProduct(updatedJob).Verify(updatedAttempt.Generation!),
                "A task redirected to the updated runtime did not create a verified point.");
            ProductDeployment.RemoveSchedules(registry, registry.ReviewMaintenance());
            Check(registry.List().Jobs.All(job => !job.Enabled && scheduler.ReadProduct(registry, job.Id) is null),
                "Application withdrawal did not remove all owned tasks and disable jobs.");
            string installsRoot = Path.GetDirectoryName(Path.GetDirectoryName(executable)!)!;
            var versions = LabInstallation.ListRemovalsAt(installsRoot);
            Check(versions.Length == 2 && versions.All(version => !version.Current), "The native installed-package review lost a version.");
            CheckVersionReference(registry, installsRoot, versions[0].Review);
            foreach (var version in versions)
                ProductDeployment.RemoveVersion(registry, registry.ReviewMaintenance(), installsRoot, version.Review);
            Check(LabInstallation.ListRemovalsAt(installsRoot).Length == 0 && !File.Exists(executable) && !File.Exists(updatedExecutable),
                "The native uninstall left its reviewed runtime files installed.");
            foreach (var job in registry.List().Jobs)
            {
                var store = VersionStore.OpenProduct(job);
                string point = registry.RunHistory(job.Id).Runs.First(run => run.Intent.Operation == ProductRunOperation.Backup &&
                    run.Result?.State == ProductRunState.Confirmed).Generation!;
                Check(!store.Verify(point), "Withdrawing the application damaged a backup.");
                string output = Directory.CreateDirectory(Path.Combine(fixture.Root, "restore-withdrawn-" + job.Id.ToString("N"))).FullName;
                _ = store.Restore(point, output);
                Check(File.ReadAllText(Path.Combine(output, "dato ñ.txt")) == "contenido del horario de producto",
                    "A withdrawn job could no longer restore its backup.");
            }
            Console.Error.WriteLine("PASS native deployment installs a second verified package, redirects only enabled schedules, runs the updated worker and withdraws owned tasks while preserving restoration for all modes.");
            Console.Error.WriteLine("PASS native uninstall checks Task Scheduler references, removes both reviewed real runtimes and restores every preserved backup mode afterward.");

            async Task<ProductRunView> WaitAsync(ProductJob job, long after, bool waiting = false, TimeSpan? limit = null)
            {
                var timer = Stopwatch.StartNew();
                while (timer.Elapsed < (limit ?? TimeSpan.FromMinutes(2)))
                {
                    var latest = registry.RunHistory(job.Id).Runs.FirstOrDefault();
                    if (latest is { Result: not null } && latest.Intent.Sequence > after &&
                        (waiting ? latest.RetryRunning : scheduler.ReadProduct(registry, job.Id) is { Running: false })) return latest;
                    await Task.Delay(250);
                }
                throw new InvalidOperationException("The product task did not reach its expected result.");
            }
        }
        finally
        {
            using (var registry = new ProductJobRegistry(settings))
            using (var scheduler = new WindowsSchedule())
            {
                foreach (var job in jobs)
                {
                    scheduler.RemoveProduct(registry, job.Id);
                    await WaitReleasedAsync(registry, job.Id);
                }
            }
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
            if (executable is not null && File.Exists(executable) && LabInstallation.IsInstalledWorker(executable, product: true))
                Directory.Delete(Path.GetDirectoryName(executable)!, recursive: true);
            if (updatedExecutable is not null && File.Exists(updatedExecutable) && LabInstallation.IsInstalledWorker(updatedExecutable, product: true))
                Directory.Delete(Path.GetDirectoryName(updatedExecutable)!, recursive: true);
        }
    }

    private static async Task WaitReleasedAsync(ProductJobRegistry registry, Guid id)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { using var released = registry.AcquireScheduledLock(id); return; }
            catch (Exception error) when (VersionJob.SharingViolation(error) && timer.Elapsed < TimeSpan.FromSeconds(20))
            { await Task.Delay(250); }
        }
    }

    private static void CheckForeignOwnership(ProductJobRegistry registry, ProductJob job)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("Scheduler unavailable.");
        dynamic service = Activator.CreateInstance(type) ?? throw new IOException("Scheduler unavailable.");
        dynamic? folder = null;
        string? original = null;
        string name = $"FolderTwin-Job-{job.Id:N}";
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        try
        {
            service.Connect(); folder = service.GetFolder("\\");
            dynamic task = folder.GetTask(name);
            try { original = (string)task.Xml; }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(task); }
            var changed = System.Xml.Linq.XDocument.Parse(original);
            System.Xml.Linq.XNamespace schema = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            changed.Root!.Element(schema + "RegistrationInfo")!.Element(schema + "Description")!.Value = "Synthetic foreign task ownership";
            Save(changed.ToString());
            bool installed = false;
            Reject(() => ProductDeployment.Install(registry, registry.ReviewMaintenance(), () =>
            { installed = true; throw new IOException("Must not prepare a package for a foreign task."); }));
            Reject(() => ProductDeployment.RemoveSchedules(registry, registry.ReviewMaintenance()));
            dynamic preserved = folder.GetTask(name);
            try
            {
                Check(!installed && ((string)preserved.Xml).Contains("Synthetic foreign task ownership", StringComparison.Ordinal) &&
                    registry.List().Jobs.All(item => item.Id != job.Id || item.Enabled),
                    "Deployment changed a foreign task or its job before rejecting ownership.");
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(preserved); }
            Console.Error.WriteLine("PASS application maintenance refuses a foreign task before installing, removing tasks or disabling jobs, and preserves its definition.");
        }
        finally
        {
            try { if (original is not null) Save(original); }
            finally
            {
                if (folder is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(folder);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(service);
            }
        }

        void Save(string xml)
        {
            dynamic saved = folder!.RegisterTask(name, xml, 6, user, null, 3, null);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(saved);
        }
        static void Reject(Action action)
        {
            try { action(); }
            catch (Exception error) when (ProductAutomation.Expected(error)) { return; }
            throw new InvalidOperationException("Maintenance accepted a foreign task.");
        }
    }

    private static void CheckVersionReference(ProductJobRegistry registry, string installations, InstallRemovalReview version)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("Scheduler unavailable.");
        dynamic service = Activator.CreateInstance(type) ?? throw new IOException("Scheduler unavailable.");
        dynamic? root = null;
        string name = "FolderTwin-Reference-Probe-" + Guid.NewGuid().ToString("N");
        bool created = false;
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        string executable = Path.Combine(installations, version.Fingerprint, "CopyLab.exe");
        try
        {
            service.Connect(); root = service.GetFolder("\\");
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            System.Xml.Linq.XElement Element(string key, object value) => new(ns + key, value);
            var xml = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement(ns + "Task", new System.Xml.Linq.XAttribute("version", "1.4"),
                Element("RegistrationInfo", Element("Description", "Synthetic external reference; never executed")),
                Element("Principals", new System.Xml.Linq.XElement(ns + "Principal", new System.Xml.Linq.XAttribute("id", "Author"),
                    Element("UserId", user), Element("LogonType", "InteractiveToken"), Element("RunLevel", "LeastPrivilege"))),
                Element("Settings", new[] { Element("Enabled", false), Element("Hidden", true) }),
                new System.Xml.Linq.XElement(ns + "Actions", new System.Xml.Linq.XAttribute("Context", "Author"),
                    Element("Exec", Element("Command", executable)))));
            dynamic task = root.RegisterTask(name, xml.ToString(), 2, user, null, 3, null);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(task);
            created = true;
            bool rejected = false;
            try { ProductDeployment.RemoveVersion(registry, registry.ReviewMaintenance(), installations, version); }
            catch (InvalidDataException error) when (error.Message.Contains("tarea aún usa", StringComparison.Ordinal)) { rejected = true; }
            dynamic preserved = root.GetTask(name);
            try { Check(rejected && File.Exists(executable) && !((bool)preserved.Enabled), "Removal changed a hidden foreign reference or deleted its runtime."); }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(preserved); }
            Console.Error.WriteLine("PASS a hidden task outside the job registry blocks runtime removal and retains its definition and referenced files.");
        }
        finally
        {
            try { if (created) root!.DeleteTask(name, 0); }
            finally
            {
                if (root is not null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(root);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(service);
            }
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
