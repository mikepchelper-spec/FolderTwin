internal static class ProductStoreProbe
{
    internal static int Run()
    {
        var fixture = LabWorkspace.Create();
        try
        {
            string source = Folder("Origen independiente");
            string parent = Folder("Destinos independientes");
            string settings = Folder("Configuración de prueba");
            Directory.CreateDirectory(Path.Combine(source, "Carpeta vacía"));
            File.WriteAllText(Path.Combine(source, "documento.txt"), "primera versión");
            ProductJob Create(ProductBackupMode mode)
            {
                using var registry = new ProductJobRegistry(settings);
                return registry.Create(new(mode.ToString(), mode, null), LocalJobRoots.ReviewDedicatedDestination(source, parent));
            }
            foreach (var boundary in Enum.GetValues<ProductStoreStep>())
            {
                var job = Create(ProductBackupMode.Versions);
                bool reached = false;
                try { VersionStore.OpenProduct(job, initialize: true, step =>
                {
                    if (step != boundary) return;
                    reached = true;
                    throw new IOException("Injected store initialization interruption.");
                }); }
                catch (IOException) when (reached) { }
                Check(reached, "The requested initialization boundary was not reached.");
                var store = VersionStore.OpenProduct(job, initialize: true);
                string generation = VersionStore.NewGenerationId();
                store.CreatePoint(job.Source.VolumePath, generation);
                Check(!store.Verify(generation) && store.ListPoints().Single().Id == generation,
                    "The product generation was not independently verifiable.");
                string output = Folder("Restauración " + boundary);
                store.Restore(generation, output);
                Check(File.ReadAllText(Path.Combine(output, "documento.txt")) == "primera versión" &&
                    Directory.Exists(Path.Combine(output, "Carpeta vacía")), "Restoration lost data or empty folders.");
            }
            foreach (var boundary in Enum.GetValues<PendingStep>())
            {
                var job = Create(ProductBackupMode.Versions);
                var store = VersionStore.OpenProduct(job, initialize: true);
                string generation = VersionStore.NewGenerationId();
                bool reached = false;
                Reject(() => store.CreatePoint(job.Source.VolumePath, generation, pendingCheckpoint: step =>
                {
                    if (step != boundary) return;
                    reached = true;
                    throw new IOException("Injected product intent interruption.");
                }));
                Check(reached, "A product descriptor boundary was not reached.");
                store = VersionStore.OpenProduct(job);
                if (store.ListPending().Any(point => point.Id == generation)) store.ResumePoint(generation);
                else store.CreatePoint(job.Source.VolumePath, generation);
                Check(!store.Verify(generation), "A product descriptor did not resume to the same verified point.");
            }
            foreach (var boundary in Enum.GetValues<CatalogStep>())
            {
                var job = Create(ProductBackupMode.Versions);
                var store = VersionStore.OpenProduct(job, initialize: true);
                string generation = VersionStore.NewGenerationId();
                bool reached = false;
                Reject(() => store.CreatePoint(job.Source.VolumePath, generation, checkpoint: step =>
                {
                    if (step != boundary) return;
                    reached = true;
                    throw new IOException("Injected product catalog interruption.");
                }));
                Check(reached, "A product publication boundary was not reached.");
                string moved = Path.Combine(fixture.Root, "Origen apartado para publicación");
                Directory.Move(source, moved);
                try
                {
                    store = VersionStore.OpenProduct(job);
                    store.ResumePoint(generation, expectedSourceIdentity: job.Source.Identity);
                    Check(!store.Verify(generation), "A product publication did not resume without the source.");
                }
                finally { Directory.Move(moved, source); }
            }
            var versionJob = Create(ProductBackupMode.Versions);
            var versioned = VersionStore.OpenProduct(versionJob, initialize: true);
            string pending = VersionStore.NewGenerationId();
            Reject(() => versioned.CreatePoint(versionJob.Source.VolumePath, pending,
                objectCheckpoint: (step, _) =>
                {
                    if (step == CopyStep.Verified) throw new IOException("Injected after verified receipt.");
                }));
            Check(versioned.ListPending().Single().Id == pending, "A pending generation was lost.");
            string absent = Path.Combine(fixture.Root, "Origen ausente");
            Directory.Move(source, absent);
            versioned = VersionStore.OpenProduct(versionJob);
            versioned.ResumePoint(pending, expectedSourceIdentity: versionJob.Source.Identity);
            Check(!versioned.Verify(pending) && versioned.ListPending().Length == 0,
                "A verified temporary could not complete without the source.");
            versioned.Restore(pending, Folder("Recuperación sin origen"));
            var retained = versioned.PreviewRetention(new RetentionPolicy());
            Check(retained.Keep.SequenceEqual([pending]), "Retention required the disconnected source.");
            Directory.Move(absent, source);

            using (var writer = new FileStream(Path.Combine(source, "documento.txt"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.Read))
                Reject(() => versioned.CreatePoint(versionJob.Source.VolumePath, VersionStore.NewGenerationId()));
            Check(versioned.ListPoints().Length == 1, "A busy source published an incomplete generation.");
            using (var gate = new DirectoryFence(versionJob.Destination.LastVolumePath))
            using (WindowsProbeNative.OpenPlainFile(gate.Root, FolderBatch.LockName, FileAccess.ReadWrite, FileShare.None))
                Reject(() => versioned.Verify(pending));
            Reject(() => versioned.Restore(pending, source));
            File.WriteAllText(Path.Combine(source, "documento.txt"), "contenido para retención");
            string latest = VersionStore.NewGenerationId();
            versioned.CreatePoint(versionJob.Source.VolumePath, latest);
            DateTimeOffset observation = DateTimeOffset.UtcNow.AddDays(2);
            Directory.Move(source, absent);
            var policy = new RetentionPolicy(1, 0, 0, 1);
            var review = versioned.PreviewRetention(policy, observation);
            Check(review.Expire.SequenceEqual([pending]) && review.Objects.Length == 1,
                "Product retention did not calculate the unshared retired object.");
            string operation = versioned.BeginRetention(review, clock: observation);
            Check(versioned.ListRetention().Single().State == "quarantined" && !versioned.Verify(latest),
                "Quarantine damaged a retained product generation.");
            Reject(() => versioned.PurgeRetention(operation, clock: observation));
            versioned.UndoRetention(operation);
            Check(!versioned.Verify(pending) && versioned.ListRetention().Single().State == "undone",
                "Product retention undo lost the earlier point.");
            Directory.Move(absent, source);
            File.WriteAllText(Path.Combine(source, "documento.txt"), "primera versión");

            var mirrorJob = Create(ProductBackupMode.ProtectedMirror);
            var mirror = VersionStore.OpenProduct(mirrorJob, initialize: true);
            Directory.CreateDirectory(Path.Combine(mirror.Destination, MirrorJournal.DirectoryName, MirrorJournal.CurrentName));
            mirror.InitializeMirror(resumeInitialization: true);
            mirror.InitializeMirror(resumeInitialization: true);
            var initial = mirror.PreviewMirror(mirrorJob.Source.VolumePath);
            mirror.BeginMirror(initial);
            File.WriteAllText(Path.Combine(source, "documento.txt"), "segunda versión");
            File.WriteAllText(Path.Combine(source, "nuevo.txt"), "contenido nuevo");
            var update = mirror.PreviewMirror(mirrorJob.Source.VolumePath);
            Check(update.Replaced == 1 && update.Added == 1, "The mirror review lost changes.");
            mirror.BeginMirror(update, massReviewed: true);
            Check(File.ReadAllText(Path.Combine(mirror.MirrorCurrentPath, "documento.txt")) == "segunda versión",
                "The product mirror was not updated.");
            mirror.Restore(initial.Generation, Folder("Recuperación anterior del espejo"));
            var delete = mirror.PreviewMirror(mirrorJob.Source.VolumePath);
            File.WriteAllText(Path.Combine(mirror.MirrorCurrentPath, "documento.txt"), "edición externa");
            Reject(() => mirror.BeginMirror(delete));
            Check(File.ReadAllText(Path.Combine(mirror.MirrorCurrentPath, "documento.txt")) == "edición externa",
                "A mirror conflict was overwritten.");

            Directory.Move(source, absent);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "documento.txt"), "suplantado");
            Reject(() => versioned.CreatePoint(versionJob.Source.VolumePath, VersionStore.NewGenerationId()));
            Reject(() => mirror.PreviewMirror(mirrorJob.Source.VolumePath));
            string substituted = Path.Combine(Path.GetDirectoryName(versionJob.Destination.LastVolumePath)!, "Destino apartado");
            Directory.Move(versionJob.Destination.LastVolumePath, substituted);
            Directory.CreateDirectory(versionJob.Destination.LastVolumePath);
            File.Copy(Path.Combine(substituted, DestinationBinding.MarkerName),
                Path.Combine(versionJob.Destination.LastVolumePath, DestinationBinding.MarkerName));
            Reject(() => VersionStore.OpenProduct(versionJob));
            Console.WriteLine("PASS product versions and mirror reuse verified objects, restore offline and reject substituted roots and conflicts.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("An unsafe or interrupted operation was accepted.");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
