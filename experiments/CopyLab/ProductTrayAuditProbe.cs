using System.IO;

namespace CopyLab;

internal static class ProductTrayAuditProbe
{
    internal static void Run()
    {
        var fixture = LabWorkspace.Create();
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
            string parent = Directory.CreateDirectory(Path.Combine(fixture.Root, "backups")).FullName;
            string settings = Directory.CreateDirectory(Path.Combine(fixture.Root, "settings")).FullName;
            File.WriteAllText(Path.Combine(source, "synthetic.txt"), "Synthetic tray state audit.");
            using var registry = new ProductJobRegistry(settings);
            var job = registry.Create(new("Synthetic tray state", ProductBackupMode.Versions, null),
                LocalJobRoots.ReviewDedicatedDestination(source, parent));
            var first = ProductRunner.Run(registry, job.Id);
            if (first is not { State: ProductRunState.Confirmed, Generation: not null })
                throw new InvalidOperationException("Could not prepare a confirmed synthetic point.");
            string missingKey = $"{job.Id:N}-{ProductRunState.WaitingForSource}";
            string detached = Path.Combine(fixture.Root, "source-absent");
            fixture.CheckChild(source);
            fixture.CheckChild(detached);
            Directory.Move(source, detached);
            try
            {
                var failed = ProductRunner.Run(registry, job.Id);
                if (failed.State != ProductRunState.WaitingForSource ||
                    !TraySnapshot.ReadProduct(registry).Attention.Contains(missingKey))
                    throw new InvalidOperationException("An unavailable synthetic source was not reported before verification.");
                var verified = ProductRunner.Verify(registry, job.Id, first.Generation);
                if (verified.State != ProductRunState.Confirmed)
                    throw new InvalidOperationException("The existing point could not be verified without its source.");
                if (!TraySnapshot.ReadProduct(registry).Attention.Contains(missingKey))
                    throw new InvalidOperationException("A successful verification hid the outstanding failed backup from tray attention.");
                var history = registry.RunHistory(job.Id);
                if (!ProductWindow.Brief(history).Contains("respaldo requiere atención", StringComparison.Ordinal))
                    throw new InvalidOperationException("The job row hid a failed backup after a successful verification.");
                if (history.Runs.First(run => run.Intent.Operation == ProductRunOperation.Backup).Result?.State != ProductRunState.WaitingForSource ||
                    history.Runs.First(run => run.Intent.Operation == ProductRunOperation.Backup &&
                        run.Result?.State == ProductRunState.Confirmed).Generation != first.Generation)
                    throw new InvalidOperationException("Verifying an old point changed the backup result or its protection date.");
            }
            finally { Directory.Move(detached, source); }
            if (ProductRunner.Run(registry, job.Id).State != ProductRunState.Confirmed ||
                TraySnapshot.ReadProduct(registry).Attention.Contains(missingKey))
                throw new InvalidOperationException("A fresh successful backup did not resolve its own source incident.");
            using (var verification = registry.StartRun(job.Id, operation: ProductRunOperation.Verify))
            {
                verification.SetGeneration(first.Generation);
                _ = verification.Finish(ProductRunState.Review, detail: "Synthetic verification incident, without corrupting content.");
            }
            string verificationKey = $"{job.Id:N}-{ProductRunState.Review}";
            if (ProductRunner.Run(registry, job.Id).State != ProductRunState.Confirmed ||
                !TraySnapshot.ReadProduct(registry).Attention.Contains(verificationKey))
                throw new InvalidOperationException("A successful backup hid an outstanding verification incident.");
            if (!ProductWindow.Brief(registry.RunHistory(job.Id)).Contains("verificación requiere atención", StringComparison.Ordinal))
                throw new InvalidOperationException("The job row hid a failed verification after a successful backup.");
            if (ProductRunner.Verify(registry, job.Id, first.Generation).State != ProductRunState.Confirmed ||
                TraySnapshot.ReadProduct(registry).Attention.Contains(verificationKey))
                throw new InvalidOperationException("A fresh successful verification did not resolve its own incident.");
            Console.Error.WriteLine("PASS tray keeps backup and verification incidents independent, preserves backup identity/date, and resolves each incident only after its own successful operation.");
        }
        finally
        {
            string root = Path.GetFullPath(fixture.Root);
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith(LabWorkspace.Prefix, StringComparison.Ordinal))
                throw new IOException("Tray fixture cleanup target left the owned temporary directory.");
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(root, recursive: true);
        }
    }
}
