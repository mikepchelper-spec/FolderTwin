internal static class RegistryProbe
{
    internal static int Run()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            string source = Folder("origen registro");
            File.WriteAllText(Path.Combine(source, "dato.txt"), "contenido del registro");
            foreach (var boundary in Enum.GetValues<RegistryStep>())
            {
                string destination = Folder("registro " + boundary);
                var store = VersionStore.Initialize(workspace, destination);
                string generation = VersionStore.NewGenerationId();
                bool reached = false;
                Reject(() => store.CreatePoint(source, generation, registryCheckpoint: step =>
                {
                    if (step == boundary)
                    {
                        reached = true;
                        throw new IOException("Injected registry interruption.");
                    }
                }));
                Check(reached && store.ListPoints().Length == 0 && store.ListPending().Single().Id == generation &&
                    !Directory.EnumerateDirectories(Path.Combine(destination, "objects", "sha256")).Any(),
                    "Data was copied before the replicated generation registration finished.");
                if (boundary != RegistryStep.ReplicaFlushed)
                    Reject(() => store.RegisteredGenerations());
                string moved = Path.Combine(workspace.Root, "origen registro apartado");
                Directory.Move(source, moved);
                try { Reject(() => store.ResumePoint(generation)); }
                finally { Directory.Move(moved, source); }
                Check(store.RegisteredGenerations().Generations.ContainsKey(generation),
                    "The durable pending plan did not repair the exact interrupted registry event.");
                store.ResumePoint(generation);
                var catalog = store.ReadCatalogs(generation, null).Current;
                Check(store.RegisteredGenerations().Generations[generation].AsSpan().SequenceEqual(catalog.DocumentHash),
                    "The registry did not retain the confirmed document hash.");
                CheckExpirationTransitions(destination, store, generation, catalog.DocumentHash);
            }
            Console.WriteLine("PASS partial registry writes and divergent replicas recover from the exact pending event before any object is copied.");

            string target = Folder("registro dañado");
            var damaged = VersionStore.Initialize(workspace, target);
            string point = VersionStore.NewGenerationId();
            damaged.CreatePoint(source, point);
            string primary = Path.Combine(target, GenerationRegistry.PrimaryName);
            byte[] original = File.ReadAllBytes(primary);
            File.WriteAllBytes(primary, original[..^1]);
            Reject(() => damaged.RegisteredGenerations());
            Reject(() => damaged.CreatePoint(source, VersionStore.NewGenerationId()));
            string output = Folder("restauración sin registro");
            Check(!damaged.Verify(point) && !damaged.Restore(point, output).Degraded &&
                File.ReadAllText(Path.Combine(output, "dato.txt")) == "contenido del registro" &&
                File.ReadAllBytes(primary).AsSpan().SequenceEqual(original.AsSpan(0, original.Length - 1)),
                "A damaged registry blocked independent recovery or was silently rewritten.");
            File.WriteAllBytes(primary, original);
            string foreignTarget = Folder("registro de otro almacén");
            var foreign = VersionStore.Initialize(workspace, foreignTarget);
            File.WriteAllBytes(Path.Combine(foreignTarget, GenerationRegistry.PrimaryName), original);
            File.WriteAllBytes(Path.Combine(foreignTarget, GenerationRegistry.ReplicaName), original);
            Reject(() => foreign.RegisteredGenerations());
            File.Delete(Path.Combine(target, "manifests", point + ".jsonl"));
            File.Delete(Path.Combine(target, "manifests_backup", point + ".jsonl"));
            Check(damaged.RegisteredGenerations().Generations.ContainsKey(point),
                "Losing both manifests erased the expected generation.");
            Console.WriteLine("PASS a damaged registry stops new registration, preserves recovery and retains expectations when both manifests disappear.");

            string legacyTarget = Folder("almacén anterior");
            _ = VersionStore.Initialize(workspace, legacyTarget);
            using (var record = new FileStream(Path.Combine(legacyTarget, ".foldertwin-version-store"), FileMode.Open, FileAccess.Write, FileShare.None))
            {
                record.Write("FTVERS01"u8);
                record.Flush(flushToDisk: true);
            }
            File.Delete(Path.Combine(legacyTarget, GenerationRegistry.PrimaryName));
            File.Delete(Path.Combine(legacyTarget, GenerationRegistry.ReplicaName));
            var legacy = VersionStore.Open(workspace, legacyTarget);
            string legacyPoint = VersionStore.NewGenerationId();
            legacy.CreatePoint(source, legacyPoint);
            Check(!legacy.Verify(legacyPoint), "A legacy store could not continue creating and verifying points.");
            Reject(() => legacy.RegisteredGenerations());
            Console.WriteLine("PASS legacy stores keep their read/write behavior and cannot supply an assumed generation baseline for cleanup.");
            return 0;
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }

        string Folder(string name) => Directory.CreateDirectory(Path.Combine(workspace.Root, name)).FullName;
    }

    private static void CheckExpirationTransitions(string destination, VersionStore store, string generation, byte[] hash)
    {
        foreach (var boundary in Enum.GetValues<RegistryStep>())
        {
            using var root = new DirectoryFence(destination);
            using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.ReadWrite, FileShare.None);
            bool reached = false;
            Reject(() => GenerationRegistry.SetExpired(root.Root, store.Binding, generation, hash, expired: true, checkpoint: step =>
            {
                if (step == boundary)
                {
                    reached = true;
                    throw new IOException("Injected expiration registration interruption.");
                }
            }));
            Check(reached, "An expiration checkpoint was not reached.");
            if (boundary != RegistryStep.ReplicaFlushed)
                Reject(() => GenerationRegistry.Read(root.Root, store.Binding));
            GenerationRegistry.SetExpired(root.Root, store.Binding, generation, hash, expired: true);
            Check(GenerationRegistry.Read(root.Root, store.Binding).Expired.Contains(generation), "The expiration did not recover.");
            GenerationRegistry.SetExpired(root.Root, store.Binding, generation, hash, expired: false);
            Check(!GenerationRegistry.Read(root.Root, store.Binding).Expired.Contains(generation), "The generation could not be retained again.");
        }
    }

    private static void Reject(Action operation)
    {
        try { operation(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("The invalid or interrupted registry operation was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
