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
            Check(ProductRunner.Run(registry, job.Id, automatic: true, editHints: [Name(0)]).State == ProductRunState.Review && ExistsInMirror(0),
                "An unattended retirement was accepted without consent.");
            registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, authorizeMirror: true);
            using (var reopened = new ProductJobRegistry(settings))
                Check(reopened.Get(job.Id).AutomaticMirrorPolicy == AutomaticMirrorRules.Version, "Mirror consent was not durable.");
            Check(ProductRunner.Run(registry, job.Id, automatic: true, editHints: [Name(0)]) is
                { State: ProductRunState.Confirmed, Scoped: true } && !ExistsInMirror(0), "An ordinary retirement did not converge.");

            string detached = Path.Combine(fixture.Root, "source-detached");
            Directory.Move(source, detached);
            store.Restore(first.Generation!, output);
            Check(File.ReadAllText(Path.Combine(output, Name(0))) == "before 0", "The retired file did not restore without its source.");
            Directory.Move(detached, source);

            for (int i = 1; i <= 6; i++) Move(i, source, away);
            Check(ProductRunner.Run(registry, job.Id, automatic: true, editHints: Enumerable.Range(1, 6).Select(Name).ToArray()).State == ProductRunState.Review &&
                Enumerable.Range(1, 6).All(ExistsInMirror),
                "A destructive batch exceeded its proportional budget.");
            for (int i = 1; i <= 6; i++) Move(i, away, source);

            for (int i = 1; i <= 7; i++)
            {
                File.Delete(Path.Combine(source, Name(i)));
                var result = RunAutomatic();
                Check(result.State == ProductRunState.Confirmed && !ExistsInMirror(i), result.Detail ?? "An ordinary retirement was not applied.");
            }
            File.Delete(Path.Combine(source, Name(8)));
            var split = ProductRunner.Run(registry, job.Id, clock: () => DateTimeOffset.UtcNow.AddMinutes(-30), automatic: true, editHints: [Name(8)]);
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

                string editing = Folder("scoped-source-" + mode);
                string existingFolder = Directory.CreateDirectory(Path.Combine(editing, "existing")).FullName;
                for (int index = 0; index < 12; index++) File.WriteAllText(Path.Combine(editing, Name(index)), "before");
                var scoped = registry.Create(new("Synthetic scoped " + mode, mode, null),
                    LocalJobRoots.ReviewDedicatedDestination(editing, destinations));
                registry.UpdateSettings(scoped.Id, scoped.Options.Name, null, true, false, ProductWatchMode.Watching,
                    authorizeMirror: mode == ProductBackupMode.ProtectedMirror);
                var initialScoped = ProductRunner.Run(registry, scoped.Id, automatic: true);
                Check(initialScoped.State == ProductRunState.Confirmed && !initialScoped.Scoped, "The scoped fixture needs a complete baseline.");
                string edited = Path.Combine(editing, Name(0));
                var time = File.GetLastWriteTimeUtc(edited);
                File.WriteAllText(edited, "edited");
                File.SetLastWriteTimeUtc(edited, time);
                var point = ProductRunner.Run(registry, scoped.Id, automatic: true, editHints: ["save.tmp", Name(0)]);
                Check(point is { State: ProductRunState.Confirmed, Scoped: true }, point.Detail ?? "The hinted edit did not use the scoped path.");
                var scopedStore = VersionStore.OpenProduct(scoped);
                string Prepared(string generation) => Path.Combine(scoped.Destination.LastVolumePath, "mirror", "operations", generation, "source.jsonl");
                Check(!File.Exists(Prepared(point.Generation!)) && File.Exists(Path.Combine(scoped.Destination.LastVolumePath,
                    "pending", point.Generation + ".jsonl")), "An applied scoped update retained an exact duplicate or lost the durable descriptor.");
                Check(File.ReadAllText(Path.Combine(scopedStore.MirrorCurrentPath, Name(0))) == "edited", "Scoped edit did not update the mirror.");
                var comparison = ProductRunner.Run(registry, scoped.Id, automatic: true);
                Check(comparison is { State: ProductRunState.Unchanged, Scoped: false }, "Reconciliation was not independent of the scoped path.");

                File.WriteAllText(edited, "second");
                string? interrupted = null;
                try
                {
                    scopedStore.UpdateAutomaticMirror(scoped.Source.VolumePath, captureAffectedPaths: false, preview =>
                    {
                        Check(preview.Scoped, "The recovery fixture did not select a scoped journal.");
                        interrupted = preview.Generation;
                        return true;
                    }, checkpoint: step =>
                    {
                        if (step == MirrorStep.Retired)
                        {
                            Check(File.Exists(Prepared(interrupted!)), "A pending update lost its preparation before the applied marker.");
                            throw new IOException("Synthetic interruption after retirement.");
                        }
                    }, editHints: [Name(0)]);
                    throw new InvalidOperationException("The scoped interruption was not reached.");
                }
                catch (IOException error) when (error.Message == "Synthetic interruption after retirement.") { }
                string detachedEditing = Path.Combine(fixture.Root, "scoped-offline-" + mode);
                Directory.Move(editing, detachedEditing);
                var resumedScoped = ProductRunner.Run(registry, scoped.Id, automatic: true);
                Check(resumedScoped is { State: ProductRunState.Confirmed, Scoped: true } && resumedScoped.Generation == interrupted,
                    resumedScoped.Detail ?? "The scoped journal could not finish without the source.");
                Check(!File.Exists(Prepared(interrupted!)), "Resuming an interrupted scoped update did not release the redundant preparation.");
                string scopedRestore = Folder("scoped-restore-" + mode);
                scopedStore.Restore(resumedScoped.Generation!, scopedRestore, Name(0));
                Check(File.ReadAllText(Path.Combine(scopedRestore, Name(0))) == "second", "The scoped journal did not restore the edited bytes.");
                Directory.Move(detachedEditing, editing);

                File.WriteAllText(edited, "synthetic later bytes");
                string? altered = null;
                Check(scopedStore.UpdateAutomaticMirror(scoped.Source.VolumePath, captureAffectedPaths: false, preview =>
                {
                    altered = Prepared(preview.Generation);
                    return preview.Scoped;
                }, checkpoint: step =>
                {
                    if (step == MirrorStep.Registered) File.WriteAllText(altered!, "synthetic nonmatching preparation");
                }, editHints: [Name(0)]) && File.ReadAllText(altered!) == "synthetic nonmatching preparation",
                    "An externally altered preparation was removed as a duplicate.");

                File.WriteAllText(Path.Combine(editing, "added.txt"), "new structural entry");
                var added = ProductRunner.Run(registry, scoped.Id, automatic: true, editHints: [Name(0)]);
                Check(added is { State: ProductRunState.Confirmed, Scoped: false } && File.Exists(Path.Combine(scopedStore.MirrorCurrentPath, "added.txt")),
                    "A scoped hint bypassed an actual change in the tree layout.");

                const string newName = @"existing\new.txt", renamedName = @"existing\renamed.txt";
                string newPath = Path.Combine(existingFolder, "new.txt");
                File.WriteAllText(newPath, "scoped new bytes");
                var newFile = ProductRunner.Run(registry, scoped.Id, automatic: true, editHints: ["?existing", newName]);
                Check(newFile is { State: ProductRunState.Confirmed, Scoped: true }, newFile.Detail ?? "An isolated addition used a complete comparison.");
                File.Move(newPath, Path.Combine(existingFolder, "renamed.txt"));
                if (mode == ProductBackupMode.ProtectedMirror)
                {
                    registry.UpdateSettings(scoped.Id, scoped.Options.Name, null, true, false, ProductWatchMode.Watching, authorizeMirror: false);
                    var withheldRename = ProductRunner.Run(registry, scoped.Id, automatic: true, editHints: ["?existing", newName, renamedName]);
                    Check(withheldRename.State == ProductRunState.Review && File.Exists(Path.Combine(scopedStore.MirrorCurrentPath, newName)),
                        "A scoped rename bypassed retirement consent.");
                    registry.UpdateSettings(scoped.Id, scoped.Options.Name, null, true, false, ProductWatchMode.Watching, authorizeMirror: true);
                }
                interrupted = null;
                try
                {
                    scopedStore.UpdateAutomaticMirror(scoped.Source.VolumePath, captureAffectedPaths: true, preview =>
                    {
                        Check(preview.Scoped && preview.Added == 1 && preview.Removed == (mode == ProductBackupMode.ProtectedMirror ? 1 : 0),
                            "A simple rename did not produce the expected scoped delta.");
                        Check(preview.AffectedPaths!.SequenceEqual(mode == ProductBackupMode.ProtectedMirror ? [newName] : []),
                            "A rename did not charge exactly the previously protected path it retires.");
                        interrupted = preview.Generation;
                        return true;
                    }, checkpoint: step =>
                    {
                        if (step == MirrorStep.Promoted) throw new IOException("Synthetic interruption after rename promotion.");
                    }, editHints: ["?existing", newName, renamedName]);
                    throw new InvalidOperationException("The scoped rename interruption was not reached.");
                }
                catch (IOException error) when (error.Message == "Synthetic interruption after rename promotion.") { }
                Directory.Move(editing, detachedEditing);
                var resumedRename = ProductRunner.Run(registry, scoped.Id, automatic: true);
                Check(resumedRename is { State: ProductRunState.Confirmed, Scoped: true } && resumedRename.Generation == interrupted &&
                    File.Exists(Path.Combine(scopedStore.MirrorCurrentPath, newName)) == (mode == ProductBackupMode.Accumulative),
                    resumedRename.Detail ?? "The interrupted rename could not finish without the source.");
                string renamedRestore = Folder("renamed-restore-" + mode), originalRestore = Folder("original-restore-" + mode);
                scopedStore.Restore(resumedRename.Generation!, renamedRestore, renamedName);
                scopedStore.Restore(newFile.Generation!, originalRestore, newName);
                Check(File.ReadAllText(Path.Combine(renamedRestore, renamedName)) == "scoped new bytes" &&
                    File.ReadAllText(Path.Combine(originalRestore, newName)) == "scoped new bytes", "The new and previous paths did not restore offline.");
                Directory.Move(detachedEditing, editing);

                string occupied = Path.Combine(existingFolder, "occupied.txt");
                using (var writer = new FileStream(occupied, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
                {
                    writer.WriteByte(42);
                    writer.Flush();
                    var busy = ProductRunner.Run(registry, scoped.Id, automatic: true, editHints: ["?existing", @"existing\occupied.txt"]);
                    Check(busy.State == ProductRunState.Busy && !File.Exists(Path.Combine(scopedStore.MirrorCurrentPath, @"existing\occupied.txt")),
                        "The isolated addition applied a path that could not be read safely.");
                }
                File.Delete(occupied);
                string replacement = Folder("replacement-directory-" + mode);
                File.WriteAllText(Path.Combine(replacement, "renamed.txt"), "changed same size");
                Directory.Move(existingFolder, Path.Combine(fixture.Root, "previous-directory-" + mode));
                Directory.Move(replacement, existingFolder);
                var folderSwap = ProductRunner.Run(registry, scoped.Id, automatic: true, editHints: ["existing", renamedName]);
                Check(folderSwap is { State: ProductRunState.Confirmed, Scoped: false } &&
                    File.ReadAllText(Path.Combine(scopedStore.MirrorCurrentPath, renamedName)) == "changed same size",
                    "A real directory name event was treated as metadata-only.");

                if (mode == ProductBackupMode.Accumulative)
                {
                    File.Delete(Path.Combine(editing, "added.txt"));
                    Check(ProductRunner.Run(registry, scoped.Id, automatic: true, editHints: ["added.txt"]).State == ProductRunState.Unchanged,
                        "An accumulative deletion altered the destination point.");
                    File.WriteAllText(edited, "third!");
                    var withExclusive = ProductRunner.Run(registry, scoped.Id, automatic: true, editHints: [Name(0)]);
                    Check(withExclusive is { State: ProductRunState.Confirmed, Scoped: true } &&
                        File.ReadAllText(Path.Combine(scopedStore.MirrorCurrentPath, "added.txt")) == "new structural entry",
                        "A destination-only file defeated or was lost during a scoped accumulative edit.");
                }

                string unobserved = Path.Combine(scopedStore.MirrorCurrentPath, Name(1));
                var previousWrite = File.GetLastWriteTimeUtc(unobserved);
                File.WriteAllText(unobserved, "extern");
                File.SetLastWriteTimeUtc(unobserved, previousWrite);
                File.WriteAllText(edited, "nearby");
                var nearby = ProductRunner.Run(registry, scoped.Id, automatic: true, editHints: [Name(0)]);
                Check(nearby is { State: ProductRunState.Confirmed, Scoped: true } && File.ReadAllText(unobserved) == "extern",
                    "A scoped replacement modified an unobserved path.");
                Check(ProductRunner.Run(registry, scoped.Id, automatic: true).State == ProductRunState.Review,
                    "Full reconciliation did not detect the unobserved destination conflict.");

                string conflict = Path.Combine(scopedStore.MirrorCurrentPath, Name(0));
                File.WriteAllText(conflict, "extern");
                File.WriteAllText(edited, "source");
                var denied = ProductRunner.Run(registry, scoped.Id, automatic: true, editHints: [Name(0)]);
                Check(denied.State == ProductRunState.Review && File.ReadAllText(conflict) == "extern",
                    "A scoped edit overwrote an independent destination edit.");
            }
            Console.WriteLine("PASS automatic mirror consent, recoverable retirement, small-folder edits, split mass changes, rollback of clock, empty source and external conflict.");
            Console.WriteLine("PASS late source drift preserves the prepared snapshot without mutating current, then converges in both mirror modes.");
            Console.WriteLine("PASS scoped edits, isolated additions, rename consent, mixed-state resumption and offline restoration, full structural fallback and destination conflicts in both mirror modes.");
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
