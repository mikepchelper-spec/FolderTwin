internal static class AutomaticMirrorProbe
{
    internal static int Run()
    {
        var fixture = LabWorkspace.Create();
        try
        {
            string source = Folder("source");
            string settings = Folder("settings");
            string destinations = Folder("destinations");
            string away = Folder("away");
            string output = Folder("restore");
            for (int i = 0; i < 20; i++) File.WriteAllText(Path.Combine(source, Name(i)), $"before {i}");
            using var registry = new ProductJobRegistry(settings);
            var job = registry.Create(new("Synthetic mirror", ProductBackupMode.ProtectedMirror, null),
                LocalJobRoots.ReviewDedicatedDestination(source, destinations));
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching);
            var first = RunAutomatic();
            Check(first.State == ProductRunState.Confirmed, first.Detail ?? "The initial mirror failed.");
            var store = VersionStore.OpenProduct(job);

            File.Delete(Path.Combine(source, Name(0)));
            Check(RunAutomatic().State == ProductRunState.Review && ExistsInMirror(0),
                "An unattended retirement was accepted without consent.");
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, authorizeMirror: true);
            using (var reopened = new ProductJobRegistry(settings))
                Check(reopened.Get(job.Id).AutomaticMirrorPolicy == AutomaticMirrorRules.Version, "Mirror consent was not durable.");
            Check(RunAutomatic().State == ProductRunState.Confirmed && !ExistsInMirror(0), "An ordinary retirement did not converge.");

            string detached = Path.Combine(fixture.Root, "source-detached");
            Directory.Move(source, detached);
            store.Restore(first.Generation!, output);
            Check(File.ReadAllText(Path.Combine(output, Name(0))) == "before 0", "The retired file did not restore without its source.");
            Directory.Move(detached, source);

            for (int i = 1; i <= 6; i++) Move(i, source, away);
            Check(RunAutomatic().State == ProductRunState.Review && Enumerable.Range(1, 6).All(ExistsInMirror),
                "A destructive batch exceeded its proportional budget.");
            for (int i = 1; i <= 6; i++) Move(i, away, source);

            for (int i = 1; i <= 7; i++)
            {
                File.Delete(Path.Combine(source, Name(i)));
                var result = RunAutomatic();
                Check(result.State == ProductRunState.Confirmed && !ExistsInMirror(i), result.Detail ?? "An ordinary retirement was not applied.");
            }
            File.Delete(Path.Combine(source, Name(8)));
            var split = ProductRunner.Run(registry, job.Id, clock: () => DateTimeOffset.UtcNow.AddMinutes(-30), automatic: true);
            Check(split.State == ProductRunState.Review && ExistsInMirror(8) &&
                split.Detail?.Contains("recientes", StringComparison.Ordinal) == true,
                "Splitting a destructive change into small batches or moving the clock bypassed the rolling budget.");

            for (int i = 9; i < 20; i++) Move(i, source, away);
            Check(RunAutomatic().State == ProductRunState.Review && ExistsInMirror(19), "The automated policy accepted an empty origin.");
            for (int i = 9; i < 20; i++) Move(i, away, source);

            string foreign = Path.Combine(store.MirrorCurrentPath, Name(19));
            File.WriteAllText(foreign, "external edit");
            Check(RunAutomatic().State == ProductRunState.Review && File.ReadAllText(foreign) == "external edit",
                "An external change was overwritten.");
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Off);
            Check(registry.Get(job.Id).AutomaticMirrorPolicy == 0, "Disabling monitoring left consent behind.");

            var small = registry.Create(new("Synthetic small mirror", ProductBackupMode.ProtectedMirror, null),
                LocalJobRoots.ReviewDedicatedDestination(Folder("small-source"), destinations));
            string item = Path.Combine(small.Source.VolumePath, "one.txt");
            File.WriteAllText(item, "first");
            registry.UpdateSettings(small.Id, small.Options.Name, null, true, false, ProductWatchMode.Watching, authorizeMirror: true);
            Check(ProductRunner.Run(registry, small.Id, automatic: true).State == ProductRunState.Confirmed, "The small mirror failed to initialize.");
            for (int i = 0; i < 10; i++)
            {
                File.WriteAllText(item, $"edit {i}");
                var result = ProductRunner.Run(registry, small.Id, automatic: true);
                Check(result.State == ProductRunState.Confirmed, result.Detail ?? "The same path consumed the rolling budget more than once.");
            }
            var smallStore = VersionStore.OpenProduct(small);
            string currentItem = Path.Combine(smallStore.MirrorCurrentPath, "one.txt");
            File.WriteAllText(item, "proposed");
            try
            {
                _ = smallStore.UpdateAutomaticMirror(small.Source.VolumePath, captureAffectedPaths: true, fresh =>
                {
                    Check(fresh.AffectedPaths is ["one.txt"], "The locked review did not include the replacement.");
                    File.WriteAllText(item, "changed again");
                    return true;
                });
                throw new InvalidOperationException("A stale automatic inventory was applied.");
            }
            catch (SourceChangedDuringBackupException error) when (error.RetryAfterReservation) { }
            Check(File.ReadAllText(currentItem) == "edit 9" && smallStore.ListMirrorOperations().All(operation => operation.Applied),
                "A change after automatic review mutated the previous mirror or left an obsolete plan.");

            foreach (var mode in new[] { ProductBackupMode.Accumulative, ProductBackupMode.ProtectedMirror })
            {
                string live = Folder("late-source-" + mode);
                string data = Path.Combine(live, "edit.txt");
                File.WriteAllText(data, "first");
                var late = registry.Create(new("Synthetic late " + mode, mode, null),
                    LocalJobRoots.ReviewDedicatedDestination(live, destinations));
                registry.UpdateSettings(late.Id, late.Options.Name, null, true, false, ProductWatchMode.Watching,
                    authorizeMirror: mode == ProductBackupMode.ProtectedMirror);
                var initial = ProductRunner.Run(registry, late.Id, automatic: true);
                Check(initial.State == ProductRunState.Confirmed, initial.Detail ?? "The late-drift fixture did not initialize.");
                var lateStore = VersionStore.OpenProduct(late);
                string current = Path.Combine(lateStore.MirrorCurrentPath, "edit.txt");
                File.WriteAllText(data, "prepared snapshot");
                string? snapshot = null;
                try
                {
                    lateStore.UpdateAutomaticMirror(late.Source.VolumePath, captureAffectedPaths: false, review =>
                    {
                        snapshot = review.Generation;
                        return true;
                    }, checkpoint: step =>
                    {
                        if (step == MirrorStep.Staged) File.WriteAllText(data, "final stable edit");
                    });
                    throw new InvalidOperationException("The superseded automatic snapshot mutated current.");
                }
                catch (SourceChangedDuringBackupException error) when (error.RetryAfterReservation) { }
                Check(snapshot is not null && lateStore.ListMirrorOperations().All(operation => operation.Applied) &&
                    lateStore.RegisteredGenerations().MirrorGeneration == initial.Generation && File.ReadAllText(current) == "first",
                    "Late source drift changed the published copy or blocked the next attempt.");
                var converged = ProductRunner.Run(registry, late.Id, automatic: true);
                Check(converged.State == ProductRunState.Confirmed && File.ReadAllText(current) == "final stable edit",
                    converged.Detail ?? "The automatic mirror could not reconcile after source drift.");
                Directory.Move(live, Path.Combine(fixture.Root, "offline-" + mode));
                string recovered = Folder("late-restore-" + mode);
                lateStore.Restore(snapshot!, recovered);
                Check(File.ReadAllText(Path.Combine(recovered, "edit.txt")) == "prepared snapshot",
                    "Archiving a superseded mirror operation lost its recoverable snapshot.");
            }
            Console.WriteLine("PASS automatic mirror consent, recoverable retirement, small-folder edits, split mass changes, rollback of clock, empty source and external conflict.");
            Console.WriteLine("PASS late source drift preserves the prepared snapshot without mutating current, then converges in both mirror modes.");
            return 0;

            ProductRunResult RunAutomatic() => ProductRunner.Run(registry, job.Id, automatic: true);
            bool ExistsInMirror(int i) => File.Exists(Path.Combine(store.MirrorCurrentPath, Name(i)));
            string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
        }
        finally
        {
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static string Name(int index) => $"file-{index:D2}.txt";
    private static void Move(int index, string from, string to) => File.Move(Path.Combine(from, Name(index)), Path.Combine(to, Name(index)));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
