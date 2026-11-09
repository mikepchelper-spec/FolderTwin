internal static class AutomaticPartialProbe
{
    internal static int Run()
    {
        foreach (var mode in Enum.GetValues<ProductBackupMode>())
            CheckMode(mode);
        Console.WriteLine("PASS automatic partial points preserve stable files, resume interrupted publication offline and converge in all three modes.");
        return 0;
    }

    private static void CheckMode(ProductBackupMode mode)
    {
        var fixture = LabWorkspace.Create();
        try
        {
            string source = Folder("source");
            string occupied = Path.Combine(source, "occupied.txt"), stable = Path.Combine(source, "stable.txt");
            File.WriteAllText(occupied, "original occupied");
            File.WriteAllText(stable, "original stable");
            using var registry = new ProductJobRegistry(Folder("settings"));
            var job = registry.Create(new("Synthetic partial " + mode, mode, null),
                LocalJobRoots.ReviewDedicatedDestination(source, Folder("destinations")));
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching,
                authorizeMirror: mode == ProductBackupMode.ProtectedMirror);
            var first = ProductRunner.Run(registry, job.Id, automatic: true);
            Check(first.State == ProductRunState.Confirmed, first.Detail ?? "Initial backup failed.");
            var store = VersionStore.OpenProduct(job);
            string partialId;
            using (var writer = new FileStream(occupied, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                writer.Write("unavailable bytes"u8);
                writer.Flush();
                File.WriteAllText(stable, "new stable data");
                var partial = ProductRunner.Run(registry, job.Id, automatic: true);
                Check(partial is { State: ProductRunState.Busy, RelativePath: "occupied.txt", Generation: not null } &&
                    VersionStore.IsPartialPoint(partial.Generation), partial.Detail ?? "The occupied file starved the stable file.");
                partialId = partial.Generation!;
                Check(ProductRunner.Verify(registry, job.Id, partialId).State == ProductRunState.Confirmed,
                    "The partial point could not be verified independently.");
                Check(ProductRunner.Run(registry, job.Id, automatic: true) is { State: ProductRunState.Busy, Generation: null } &&
                    store.ListPoints().Count(point => point.Partial) == 1, "A repeated retry duplicated the unchanged partial point.");
                if (mode != ProductBackupMode.Versions)
                    Check(File.ReadAllText(Path.Combine(store.MirrorCurrentPath, "stable.txt")) == "original stable" &&
                        store.RegisteredGenerations().MirrorGeneration == first.Generation,
                        "An incomplete inventory changed the published mirror.");
            }
            string detached = Path.Combine(fixture.Root, "offline");
            Directory.Move(source, detached);
            string output = Folder("partial-restore");
            store.Restore(partialId, output);
            Check(File.ReadAllText(Path.Combine(output, "stable.txt")) == "new stable data" &&
                !File.Exists(Path.Combine(output, "occupied.txt")), "The partial point did not recover only the readable data.");
            var retention = store.PreviewRetention(new RetentionPolicy());
            Check(retention.Keep.Contains(first.Generation!) && retention.Keep.Contains(partialId), "A partial point displaced the full retention baseline.");
            Directory.Move(detached, source);

            string? interrupted = null;
            using (var writer = new FileStream(occupied, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                File.WriteAllText(stable, "stable during interruption");
                try
                {
                    _ = store.CaptureReadableFiles(job.Source.VolumePath, point => interrupted = point, step =>
                    {
                        if (step == PendingStep.MetadataPublished) throw new OperationCanceledException();
                    });
                    throw new InvalidOperationException("The fixture did not interrupt publication.");
                }
                catch (OperationCanceledException) { }
            }
            Directory.Move(source, detached);
            var resumed = ProductRunner.Run(registry, job.Id, automatic: true);
            Check(interrupted is not null && resumed.State == ProductRunState.Busy && resumed.Generation == interrupted,
                resumed.Detail ?? "Partial journal did not resume without the source.");
            output = Folder("resumed-restore");
            store.Restore(interrupted!, output);
            Check(File.ReadAllText(Path.Combine(output, "stable.txt")) == "stable during interruption", "Resumed bytes differ.");
            Directory.Move(detached, source);
            var complete = ProductRunner.Run(registry, job.Id, automatic: true);
            Check(complete is { State: ProductRunState.Confirmed, Generation: not null } && !VersionStore.IsPartialPoint(complete.Generation),
                complete.Detail ?? "The work did not converge after closing the writer.");
            output = Folder("complete-restore");
            store.Restore(complete.Generation!, output);
            Check(File.ReadAllText(Path.Combine(output, "occupied.txt")) == "unavailable bytes" &&
                File.ReadAllText(Path.Combine(output, "stable.txt")) == "stable during interruption", "Final point bytes differ.");
        }
        finally
        {
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }
        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
