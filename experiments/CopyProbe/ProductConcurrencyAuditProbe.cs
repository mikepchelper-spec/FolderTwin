internal static class ProductConcurrencyAuditProbe
{
    internal static int Run()
    {
        if (new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace < 256L * 1024 * 1024)
            throw new IOException("The concurrency audit requires a free reserve of 256 MiB.");
        var fixture = LabWorkspace.Create();
        try
        {
            string source = Folder("source");
            File.WriteAllText(Path.Combine(source, "known.txt"), "known synthetic point preserved after disabling");
            using var registry = new ProductJobRegistry(Folder("settings"));
            foreach (var operation in new[] { ProductRunOperation.Backup, ProductRunOperation.ReviewMirror })
            foreach (bool retired in new[] { false, true })
            {
                string scenario = operation + (retired ? "-retired" : "-disabled");
                var job = registry.Create(new("Execution boundary " + scenario, ProductBackupMode.Versions, null),
                    LocalJobRoots.ReviewDedicatedDestination(source, Folder("backups-" + scenario)));
                var initial = ProductRunner.Run(registry, job.Id);
                if (initial is not { State: ProductRunState.Confirmed, Generation: not null })
                    throw new InvalidOperationException("Could not prepare a prior confirmed point.");
                int attempts = registry.RunHistory(job.Id).Runs.Length;
                bool checkpoint = false, rejected = false;
                try
                {
                    using var execution = registry.StartRun(job.Id, operation: operation, beforeExecutionLock: () =>
                    {
                        checkpoint = true;
                        registry.UpdateSettings(job.Id, job.Options.Name, null, enabled: false, retired: retired);
                    });
                }
                catch (Exception error) when (VersionJob.Expected(error)) { rejected = true; }
                var disabled = registry.Get(job.Id);
                if (!checkpoint || !rejected || disabled.Enabled || disabled.Retired != retired || registry.RunHistory(job.Id).Runs.Length != attempts)
                    throw new InvalidOperationException("Disabling/retiring at the execution-lock boundary still admitted a new " + operation + " attempt.");
                var verified = ProductRunner.Verify(registry, job.Id, initial.Generation);
                if (verified.State != ProductRunState.Confirmed)
                    throw new InvalidOperationException("Disabling the job incorrectly blocked verification of its confirmed point.");
                var store = VersionStore.OpenProduct(job);
                string output = Folder("restore-" + scenario);
                _ = store.Restore(initial.Generation, output);
                if (File.ReadAllText(Path.Combine(output, "known.txt")) != "known synthetic point preserved after disabling")
                    throw new InvalidOperationException("Disabling lost prior recoverable content.");
                string freshName = "Enabled at boundary " + scenario;
                using (var admitted = registry.StartRun(job.Id, operation: operation, beforeExecutionLock: () =>
                    registry.UpdateSettings(job.Id, freshName, null, enabled: true, retired: false)))
                {
                    if (admitted.Job.Options.Name != freshName || !admitted.Job.Enabled || admitted.Job.Retired)
                        throw new InvalidOperationException("An execution did not use the settings confirmed before acquiring its lease.");
                    bool settingsBlocked = false;
                    try { registry.UpdateSettings(job.Id, "Must not publish", null, enabled: false, retired: true); }
                    catch (Exception error) when (VersionJob.SharingViolation(error)) { settingsBlocked = true; }
                    if (!settingsBlocked)
                        throw new InvalidOperationException("A settings update bypassed an existing execution lease.");
                }
                if (registry.Get(job.Id).Options.Name != freshName || registry.RunHistory(job.Id).Runs.Length != attempts + 2)
                    throw new InvalidOperationException("The boundary interleaving lost its fresh settings or attempt reservation.");
            }
            Console.WriteLine("PASS execution-lock boundary rejects backup/mirror analysis after completed disable/retire revisions, creates no stale attempt, preserves disabled verification/restoration, reads fresh enabling settings and excludes settings updates while leased. No timing sleeps or native schedules.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string root = Path.GetFullPath(fixture.Root);
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
                !Path.GetFileName(root).StartsWith(LabWorkspace.Prefix, StringComparison.Ordinal))
                throw new IOException("Refusing cleanup outside the registered concurrency fixture.");
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(root, recursive: true);
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }
}
