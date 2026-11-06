internal static class ImportedSourceConcurrencyAuditProbe
{
    internal static int Run()
    {
        if (new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace < 256L * 1024 * 1024)
            throw new IOException("The imported-source concurrency audit requires a free reserve of 256 MiB.");
        var fixture = LabWorkspace.Create();
        var failures = new List<string>();
        try
        {
            foreach (bool enable in new[] { false, true })
            {
                string suffix = enable ? "enable" : "retire";
                string source = Folder("source-" + suffix);
                const string payload = "known synthetic bytes; configuration review must preserve them";
                File.WriteAllText(Path.Combine(source, "known.txt"), payload);
                ProductJob original;
                string generation;
                string configuration = Path.Combine(Folder("exports-" + suffix), "job.foldertwin.json");
                using (var first = new ProductJobRegistry(Folder("original-settings-" + suffix)))
                {
                    original = first.Create(new("Before boundary", ProductBackupMode.Versions, null),
                        LocalJobRoots.ReviewDedicatedDestination(source, Folder("backups-" + suffix)));
                    var initial = ProductRunner.Run(first, original.Id);
                    if (initial is not { State: ProductRunState.Confirmed, Generation: not null })
                        throw new InvalidOperationException("Could not prepare a confirmed synthetic point for import.");
                    generation = initial.Generation;
                    first.ExportConfiguration(original.Id, configuration);
                }
                string settings = Folder("imported-settings-" + suffix);
                using var registry = new ProductJobRegistry(settings);
                var imported = registry.ImportConfiguration(registry.ReviewImport(ProductJobRegistry.ReadConfiguration(configuration),
                    original.Destination.LastVolumePath, source));
                if (imported is not { Imported: true, SourceReviewed: true, Enabled: false, Retired: false })
                    throw new InvalidOperationException("The synthetic imported fixture is not reviewed and disabled.");
                bool reached = false, rejected = false;
                string changedName = "Confirmed boundary " + suffix;
                var changedTime = new TimeOnly(19, 47);
                try
                {
                    _ = registry.ReviewImportedSource(imported.Id, source, beforeExecutionLock: () =>
                    {
                        reached = true;
                        using var other = new ProductJobRegistry(settings);
                        _ = other.UpdateSettings(imported.Id, changedName, changedTime, enabled: enable, retired: !enable);
                    });
                }
                catch (Exception error) when (VersionJob.Expected(error)) { rejected = true; }
                var final = registry.Get(imported.Id);
                if (!reached) throw new InvalidOperationException("The imported-source boundary was not reached.");
                if (final.Options.Name != changedName || final.Options.DailyAt != changedTime ||
                    final.Enabled != enable || final.Retired != !enable)
                    failures.Add(suffix + ": source review overwrote settings already confirmed at the execution-lock boundary.");
                if (enable && !rejected)
                    failures.Add("enable: source review admitted a job that had already been enabled at the execution-lock boundary.");
                if (!enable && rejected)
                    failures.Add("retire: reviewing a disabled retired source should preserve the latest settings and succeed.");
                if (registry.RunHistory(imported.Id).Runs.Length != 0 ||
                    VersionStore.OpenProduct(final).ListPoints() is not [{ Id: var point }] || point != generation ||
                    File.ReadAllText(Path.Combine(source, "known.txt")) != payload)
                    throw new InvalidOperationException("The settings-only interleaving changed data or admitted an execution.");
            }
            if (failures.Count != 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            Console.WriteLine("PASS imported-source review reads settings under its execution lease, preserves a completed retirement/name/hour revision and rejects a concurrently enabled job. Deterministic callbacks; no sleeps or native schedules.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string root = Path.GetFullPath(fixture.Root);
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
                !Path.GetFileName(root).StartsWith(LabWorkspace.Prefix, StringComparison.Ordinal))
                throw new IOException("Refusing cleanup outside the registered imported-source audit fixture.");
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(root, recursive: true);
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }
}
