internal static class ProductConfigurationProbe
{
    internal static int Run()
    {
        foreach (var mode in Enum.GetValues<ProductBackupMode>()) CheckMode(mode);
        CheckInterruptions();
        CheckPendingImports();
        Console.WriteLine("PASS configuration export/import is explicit and disabled, resumes registration, preserves backup identity and history, restores without source, reviews moved roots and retains catalog compatibility.");
        return 0;
    }

    private static void CheckMode(ProductBackupMode mode)
    {
        var fixture = LabWorkspace.Create();
        try
        {
            string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
            string settingsA = Folder("settings A"), settingsB = Folder("settings B"), settingsC = Folder("settings C");
            string source = Folder("source"), parent = Folder("backups"), output = Folder("recovered"), exportParent = Folder("export");
            File.WriteAllText(Path.Combine(source, "dato.txt"), "contenido inicial");
            ProductJob job;
            string generation;
            string file = Path.Combine(exportParent, "job.foldertwin.json");
            using (var registry = new ProductJobRegistry(settingsA))
            {
                job = registry.Create(new("Configuración ñ", mode, new TimeOnly(20, 30)), LocalJobRoots.ReviewDedicatedDestination(source, parent));
                var result = ProductRunner.Run(registry, job.Id);
                Check(result.State == ProductRunState.Confirmed, result.Detail ?? "Could not prepare configuration fixture.");
                generation = result.Generation!;
                registry.ExportConfiguration(job.Id, file);
                Reject(() => registry.ExportConfiguration(job.Id, file));
                Reject(() => registry.ExportConfiguration(job.Id, Path.Combine(source, "must not write.json")));
                Check(!File.Exists(Path.Combine(source, "must not write.json")), "Export modified the source.");
            }
            var configuration = ProductJobRegistry.ReadConfiguration(file);
            Check(configuration.Options == job.Options && configuration.OriginalJob == job.Id && configuration.Destination.Marker == job.Destination.Marker,
                "Export lost configuration or destination identity.");
            string invalid = Path.Combine(exportParent, "invalid.json");
            File.WriteAllText(invalid, File.ReadAllText(file).Replace("Configuraci", "Configuracio", StringComparison.Ordinal));
            Reject(() => ProductJobRegistry.ReadConfiguration(invalid));

            string movedSource = Path.Combine(fixture.Root, "source renamed");
            Directory.Move(source, movedSource);
            string movedTarget = Path.Combine(Path.GetDirectoryName(job.Destination.LastVolumePath)!, "backup renamed");
            Directory.Move(job.Destination.LastVolumePath, movedTarget);
            using (var registry = new ProductJobRegistry(settingsB))
            {
                Reject(() => registry.ReviewImport(configuration, parent));
                var review = registry.ReviewImport(configuration, movedTarget);
                Check(registry.List().Jobs.Length == 0 && !Directory.Exists(Path.Combine(settingsB, review.Id.ToString("N"))),
                    "Import review wrote registration before confirmation.");
                var imported = registry.ImportConfiguration(review);
                Check(imported is { Enabled: false, Imported: true, SourceReviewed: false } && imported.Id != job.Id &&
                    imported.Destination.Marker == job.Destination.Marker && VersionStore.OpenProduct(imported).ListPoints().Single().Id == generation,
                    "Imported job was enabled or lost its original point.");
                Reject(() => registry.UpdateSettings(imported.Id, imported.Options.Name, null, true, false));
                var recover = LocalJobRoots.ReviewDedicatedDestination(movedTarget, output);
                Guid request = registry.ApproveRestore(imported.Id, generation, recover);
                Check(ProductRunner.Restore(registry, imported.Id, request).State == ProductRunState.Confirmed &&
                    File.ReadAllText(Path.Combine(recover.Parent.VolumePath, recover.FolderName, "dato.txt")) == "contenido inicial",
                    "Import could not restore without its source.");
                string wrong = Folder("wrong source");
                File.WriteAllText(Path.Combine(wrong, "dato.txt"), "contenido inicial");
                Reject(() => registry.ReviewImportedSource(imported.Id, wrong));
                imported = registry.ReviewImportedSource(imported.Id, movedSource);
                Check(imported is { Enabled: false, SourceReviewed: true } && imported.SourceAliases!.Contains(configuration.Source.VolumePath),
                    "Review lost the source identity or prior canonical path.");
                Reject(() => registry.ExportConfiguration(imported.Id, Path.Combine(movedSource, "must not write.json")));
                var forbidden = LocalJobRoots.ReviewDedicatedDestination(movedTarget, movedSource);
                Reject(() => registry.ApproveRestore(imported.Id, generation, forbidden));
                Reject(() => registry.ReviewImport(configuration, movedTarget, movedSource));
                var retained = VersionStore.OpenProduct(imported).PreviewRetention(new());
                Check(retained.Keep.Contains(generation), "Moved source broke the old descriptor's retention proof.");
                imported = registry.UpdateSettings(imported.Id, imported.Options.Name, null, true, false);
                File.WriteAllText(Path.Combine(movedSource, "nuevo.txt"), "dato nuevo");
                var copied = ProductRunner.Run(registry, imported.Id);
                if (copied.State == ProductRunState.Review && mode == ProductBackupMode.ProtectedMirror)
                {
                    var mirror = VersionStore.OpenProduct(imported).PreviewMirror(imported.Source.VolumePath);
                    copied = ProductRunner.Run(registry, imported.Id, registry.ApproveMirror(imported.Id, mirror, true, true));
                }
                Check(copied.State == ProductRunState.Confirmed, copied.Detail ?? "Imported job could not back up after source review.");
                string again = Path.Combine(exportParent, "again.foldertwin.json");
                registry.ExportConfiguration(imported.Id, again);
                using var third = new ProductJobRegistry(settingsC);
                var thirdJob = third.ImportConfiguration(third.ReviewImport(ProductJobRegistry.ReadConfiguration(again), movedTarget, movedSource));
                Check(!thirdJob.Enabled && thirdJob.SourceReviewed && VersionStore.OpenProduct(thirdJob).ListPoints().Length == 2 &&
                    VersionStore.OpenProduct(thirdJob).PreviewRetention(new()).Keep.Length >= 1,
                    "Re-export lost the reviewed source or old catalog compatibility.");
            }
        }
        finally
        {
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static void CheckInterruptions()
    {
        foreach (var boundary in Enum.GetValues<ProductRegistrationStep>())
        {
            var fixture = LabWorkspace.Create();
            try
            {
                string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
                string source = Folder("source"), parent = Folder("backups"), first = Folder("first"), second = Folder("second");
                ProductJob job;
                string file = Path.Combine(fixture.Root, "config.foldertwin.json");
                using (var registry = new ProductJobRegistry(first))
                {
                    job = registry.Create(new("Import interrupted", ProductBackupMode.Versions, null), LocalJobRoots.ReviewDedicatedDestination(source, parent));
                    registry.ExportConfiguration(job.Id, file);
                }
                using var other = new ProductJobRegistry(second);
                var review = other.ReviewImport(ProductJobRegistry.ReadConfiguration(file), job.Destination.LastVolumePath, source);
                bool reached = false;
                Reject(() => other.ImportConfiguration(review, step =>
                {
                    if (step != boundary) return;
                    reached = true;
                    throw new IOException("Synthetic import interruption.");
                }));
                Check(reached, "Import did not reach its requested durable boundary.");
                var resumed = other.Resume(review.Id);
                Check(!resumed.Enabled && resumed.Destination.Marker == job.Destination.Marker,
                    "Resuming import enabled the job or replaced its marker.");
            }
            finally
            {
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
        }
    }

    private static void CheckPendingImports()
    {
        foreach (var mode in Enum.GetValues<ProductBackupMode>())
        {
            var fixture = LabWorkspace.Create();
            try
            {
                string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
                string source = Folder("source"), parent = Folder("backups"), first = Folder("first"), second = Folder("second");
                File.WriteAllText(Path.Combine(source, "dato.txt"), "initial content");
                ProductJob original;
                string pending;
                string export = Path.Combine(fixture.Root, "pending.foldertwin.json");
                using (var registry = new ProductJobRegistry(first))
                {
                    original = registry.Create(new("Pending import", mode, null), LocalJobRoots.ReviewDedicatedDestination(source, parent));
                    Check(ProductRunner.Run(registry, original.Id).State == ProductRunState.Confirmed, "Pending import fixture failed.");
                    var store = VersionStore.OpenProduct(original);
                    File.WriteAllText(Path.Combine(source, "dato.txt"), "updated content");
                    if (mode == ProductBackupMode.Versions)
                    {
                        pending = VersionStore.NewGenerationId();
                        Reject(() => store.CreatePoint(original.Source.VolumePath, pending, pendingCheckpoint: step =>
                        { if (step == PendingStep.LocationPublished) throw new IOException("Interrupted copy before import."); }));
                    }
                    else
                    {
                        var preview = store.PreviewMirror(original.Source.VolumePath);
                        pending = preview.Generation;
                        Reject(() => store.BeginMirror(preview, true, true, checkpoint: step =>
                        { if (step == MirrorStep.Planned) throw new IOException("Interrupted materialization before import."); }));
                    }
                    registry.ExportConfiguration(original.Id, export);
                }
                string renamed = Path.Combine(fixture.Root, "source renamed");
                Directory.Move(source, renamed);
                using var imported = new ProductJobRegistry(second);
                var job = imported.ImportConfiguration(imported.ReviewImport(ProductJobRegistry.ReadConfiguration(export), original.Destination.LastVolumePath, renamed));
                job = imported.UpdateSettings(job.Id, job.Options.Name, null, true, false);
                var resumed = ProductRunner.Run(imported, job.Id);
                Check(resumed.State == ProductRunState.Confirmed && resumed.Generation == pending &&
                    !VersionStore.OpenProduct(job).Verify(pending), resumed.Detail ?? "Import lost a pending operation after a reviewed source rename.");
            }
            finally
            {
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
        }
        Console.WriteLine("PASS imported jobs resume pending versions, cumulative updates and protected mirrors after source-path review without rewriting the original descriptors.");
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("An unsafe configuration operation was accepted.");
    }
}
