internal static class ProductJobRegistryProbe
{
    internal static int Run()
    {
        var fixture = LabWorkspace.Create();
        try
        {
            string settings = Folder("Configuración");
            string source = Folder("Datos de prueba");
            string parent = Folder("Destino superior");
            var options = new ProductJobOptions("Prueba de registro", ProductBackupMode.Versions, new(20, 30));
            var reviews = new List<DedicatedDestinationReview>();
            foreach (var boundary in Enum.GetValues<ProductRegistrationStep>())
            {
                var review = LocalJobRoots.ReviewDedicatedDestination(source, parent);
                reviews.Add(review);
                bool reached = false;
                using (var registry = new ProductJobRegistry(settings))
                {
                    Reject(() => registry.Create(options, review, step =>
                    {
                        if (step != boundary) return;
                        reached = true;
                        throw new IOException("Injected registration interruption.");
                    }));
                    Check(reached, "The requested registration boundary was not reached.");
                    if (boundary != ProductRegistrationStep.Confirmed)
                        Check(registry.List().Pending.Contains(review.FolderId), "An incomplete job was not reported as pending.");
                }
                string? draft = boundary switch
                {
                    ProductRegistrationStep.Prepared => "intent.json",
                    ProductRegistrationStep.DestinationRecorded => "target.json",
                    _ => null
                };
                if (draft is not null)
                {
                    string file = Path.Combine(settings, review.FolderId.ToString("N"), draft);
                    File.Move(file, file + ".tmp");
                }
                using var reopened = new ProductJobRegistry(settings);
                var job = reopened.Resume(review.FolderId);
                Check(job.Id == review.FolderId && job.Options == options &&
                    job.Source.Identity.AsSpan().SequenceEqual(review.Source.Identity), "The job did not recover its original selection.");
                using var destination = new DirectoryFence(job.Destination.LastVolumePath);
                using var marker = job.Destination.OpenVerifiedMarker(destination.Root);
                Check(reopened.List().Pending.Length == 0, "A completed job still appeared pending.");
            }
            var last = reviews[^1];
            string away = Path.Combine(fixture.Root, "Origen apartado");
            Directory.Move(source, away);
            using (var offline = new ProductJobRegistry(settings))
                Check(offline.List().Jobs.Length == reviews.Count && offline.Resume(last.FolderId).Options == options,
                    "A registered job could not be listed without its source.");
            Directory.Move(away, source);
            string substituteSettings = Folder("Configuración sustituida");
            string copiedJob = Directory.CreateDirectory(Path.Combine(substituteSettings, last.FolderId.ToString("N"))).FullName;
            foreach (string file in Directory.EnumerateFiles(Path.Combine(settings, last.FolderId.ToString("N"))))
                File.Copy(file, Path.Combine(copiedJob, Path.GetFileName(file)));
            using (var substituted = new ProductJobRegistry(substituteSettings))
            {
                Check(substituted.List() is { Jobs.Length: 0, Unreadable: 1 }, "Copied settings authorized another registry.");
                Reject(() => substituted.Resume(last.FolderId));
            }

            using (var registry = new ProductJobRegistry(settings))
            {
                var stale = LocalJobRoots.ReviewDedicatedDestination(source, parent);
                Reject(() => registry.Create(options, stale, step =>
                {
                    if (step == ProductRegistrationStep.Prepared) throw new IOException("Injected before create.");
                }));
                string foreign = Directory.CreateDirectory(Path.Combine(parent, stale.FolderName)).FullName;
                string file = Path.Combine(foreign, "ajeno.txt");
                File.WriteAllText(file, "conservar");
                Reject(() => registry.Resume(stale.FolderId));
                Check(File.ReadAllText(file) == "conservar" &&
                    !File.Exists(Path.Combine(foreign, DestinationBinding.MarkerName)), "The incomplete job adopted somebody else's folder.");

                var overlapping = LocalJobRoots.ReviewDedicatedDestination(foreign, parent);
                Reject(() => registry.Create(options, overlapping));
                var internalSource = LocalJobRoots.ReviewDedicatedDestination(settings, parent);
                Reject(() => registry.Create(options, internalSource));
                Check(registry.List().Jobs.Length == reviews.Count, "Unsafe settings were published.");
            }
            Console.WriteLine("PASS job registration replays each durable boundary, lists offline jobs, and refuses adoption and overlap.");
            return 0;
        }
        finally
        {
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
    }

    private static void Reject(Action attempt)
    {
        try { attempt(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("The test operation should have failed.");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
