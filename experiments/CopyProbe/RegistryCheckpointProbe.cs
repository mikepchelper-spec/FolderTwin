using System.Security.Cryptography;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

internal static class RegistryCheckpointProbe
{
    internal static int Run()
    {
        var fixture = LabWorkspace.Create();
        bool passed = false;
        try
        {
            foreach (var boundary in Enum.GetValues<RegistryCheckpointStep>())
            {
                string path = Directory.CreateDirectory(Path.Combine(fixture.Root, boundary.ToString())).FullName;
                var store = VersionStore.Initialize(fixture, path);
                using var root = new DirectoryFence(path);
                using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.ReadWrite, FileShare.None);
                string first = VersionStore.NewGenerationId(), mirror = VersionStore.NewGenerationId();
                string next = VersionStore.NewGenerationId(), abandoned = VersionStore.NewGenerationId();
                byte[] hash = SHA256.HashData("synthetic checkpoint"u8);
                foreach (string id in new[] { first, mirror, next, abandoned })
                    GenerationRegistry.Register(root.Root, store.Binding, id, hash);
                GenerationRegistry.Abandon(root.Root, store.Binding, abandoned, hash);
                GenerationRegistry.SetMirror(root.Root, store.Binding, mirror, hash, legacy: true);
                GenerationRegistry.SetMirrorPending(root.Root, store.Binding, next, hash);
                for (int index = 0; index < 24; index++)
                {
                    GenerationRegistry.SetExpired(root.Root, store.Binding, first, hash, true);
                    GenerationRegistry.SetExpired(root.Root, store.Binding, first, hash, false);
                }
                GenerationRegistry.SetExpired(root.Root, store.Binding, first, hash, true);
                var before = GenerationRegistry.Read(root.Root, store.Binding);
                long original = new FileInfo(Path.Combine(path, GenerationRegistry.PrimaryName)).Length;
                byte[] unchanged = File.ReadAllBytes(Path.Combine(path, GenerationRegistry.PrimaryName));
                try
                {
                    GenerationRegistry.Reserve(root.Root, store.Binding, int.MaxValue);
                    throw new InvalidOperationException("An impossible operation reservation was accepted.");
                }
                catch (InvalidDataException) { }
                if (!File.ReadAllBytes(Path.Combine(path, GenerationRegistry.PrimaryName)).AsSpan().SequenceEqual(unchanged))
                    throw new InvalidDataException("A failed capacity reservation changed the registry.");
                bool reached = false;
                try
                {
                    GenerationRegistry.Reserve(root.Root, store.Binding, 2, step =>
                    {
                        if (step != boundary) return;
                        reached = true;
                        throw new IOException("Injected checkpoint interruption.");
                    }, maximumBytes: 16 * 1024);
                    throw new InvalidDataException("The checkpoint boundary was not exercised.");
                }
                catch (IOException) when (reached) { }
                var during = GenerationRegistry.Read(root.Root, store.Binding);
                RequireSameState(before, during);
                GenerationRegistry.Reserve(root.Root, store.Binding, 2, maximumBytes: 16 * 1024);
                var after = GenerationRegistry.Read(root.Root, store.Binding);
                RequireSameState(before, after);
                long compact = new FileInfo(Path.Combine(path, GenerationRegistry.PrimaryName)).Length;
                if (compact >= original || compact + 2 * 2048 > 16 * 1024)
                    throw new InvalidDataException("The registry did not reserve the requested operation capacity.");
                GenerationRegistry.SetMirror(root.Root, store.Binding, next, hash);
                if (GenerationRegistry.Read(root.Root, store.Binding).PendingMirrorGeneration is not null)
                    throw new InvalidDataException("A pending mirror could not finish after compaction.");
                Console.WriteLine($"PASS registry checkpoint {boundary}, before={original}, after={compact}.");
            }
            CheckWorkerTermination();
            passed = true;
            return 0;
        }
        finally
        {
            if (passed)
            {
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
            else Console.Error.WriteLine("Synthetic checkpoint fixture preserved at " + fixture.Root);
        }
    }

    private static void RequireSameState(RegistryState before, RegistryState after)
    {
        if (!before.RegistrationOrder.SequenceEqual(after.RegistrationOrder) || !before.Expired.SetEquals(after.Expired) ||
            before.MirrorGeneration != after.MirrorGeneration || before.PendingMirrorGeneration != after.PendingMirrorGeneration ||
            before.Generations.Count != after.Generations.Count || before.Abandoned.Count != after.Abandoned.Count || before.PurgeProofs.Count != after.PurgeProofs.Count ||
            before.Generations.Any(pair => !after.Generations.TryGetValue(pair.Key, out var hash) || !hash.AsSpan().SequenceEqual(pair.Value)) ||
            before.Abandoned.Any(pair => !after.Abandoned.TryGetValue(pair.Key, out var hash) || !hash.AsSpan().SequenceEqual(pair.Value)) ||
            before.PurgeProofs.Any(pair => !after.PurgeProofs.TryGetValue(pair.Key, out var hash) || !hash.AsSpan().SequenceEqual(pair.Value)))
            throw new InvalidDataException("Registry compaction changed the retained semantic state.");
    }

    // Build a valid large synthetic history in one write; issuing tens of
    // thousands of separately flushed operations would measure the fixture.
    internal static long FillNearProductionLimit(LabWorkspace fixture, string destination, DestinationBinding binding, string generation)
    {
        using var laboratory = fixture.OpenRegisteredRoot();
        using var root = new DirectoryFence(destination);
        string relative = Path.GetRelativePath(WindowsProbeNative.VolumePath(laboratory.Root), WindowsProbeNative.VolumePath(root.Root));
        if (Path.IsPathFullyQualified(relative) || relative.Split(Path.DirectorySeparatorChar).Any(part => part is "." or ".."))
            throw new InvalidDataException("The large registry fixture must remain inside its disposable workspace.");
        using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.ReadWrite, FileShare.None);
        var before = GenerationRegistry.Read(root.Root, binding);
        if (before.Expired.Contains(generation) || before.MirrorGeneration == generation || before.PendingMirrorGeneration == generation)
            throw new InvalidDataException("The synthetic history needs an ordinary retained generation.");
        using var bytes = new MemoryStream();
        bytes.Write(File.ReadAllBytes(Path.Combine(destination, GenerationRegistry.PrimaryName)));
        long sequence = before.NextSequence;
        byte[] previous = before.LastHash;
        while (bytes.Length + 2048 < 16 * 1024 * 1024)
            foreach (string kind in new[] { "expire", "retain" })
            {
                var payload = new { version = 1, sequence = sequence++, kind, generation,
                    documentHash = before.Generations[generation], jobId = binding.Marker, identity = binding.Identity, previousHash = previous };
                byte[] checksum = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload));
                bytes.Write(JsonSerializer.SerializeToUtf8Bytes(new { payload, checksum }));
                bytes.WriteByte(10);
                previous = checksum;
            }
        byte[] history = bytes.ToArray();
        foreach (string name in new[] { GenerationRegistry.PrimaryName, GenerationRegistry.ReplicaName })
        {
            using var file = WindowsProbeNative.OpenPlainFile(root.Root, name, FileAccess.Write, FileShare.None);
            file.Write(history);
            file.SetLength(history.Length);
            file.Flush(flushToDisk: true);
        }
        RequireSameState(before, GenerationRegistry.Read(root.Root, binding));
        return history.LongLength;
    }

    internal static int HeldChild(string fixturePath, string name, string boundaryName)
    {
        var fixture = LabWorkspace.Open(fixturePath);
        fixture.CheckChild(Path.Combine(fixture.Root, name));
        if (!Enum.TryParse<RegistryCheckpointStep>(boundaryName, out var boundary) || !Enum.IsDefined(boundary))
            throw new ArgumentException("Unknown checkpoint boundary.");
        var store = VersionStore.Open(fixture, Path.Combine(fixture.Root, name));
        using var root = new DirectoryFence(store.Destination);
        using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.ReadWrite, FileShare.None);
        GenerationRegistry.Reserve(root.Root, store.Binding, 2, step =>
        {
            if (step != boundary) return;
            Console.WriteLine("checkpoint-paused");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
        }, maximumBytes: 16 * 1024);
        return 1;
    }

    private static void CheckWorkerTermination()
    {
        var fixture = LabWorkspace.Create();
        bool passed = false;
        try
        {
            string name = "worker-checkpoint";
            string path = Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
            var store = VersionStore.Initialize(fixture, path);
            string generation = VersionStore.NewGenerationId();
            byte[] hash = SHA256.HashData("synthetic worker checkpoint"u8);
            using (var root = new DirectoryFence(path))
                GenerationRegistry.Register(root.Root, store.Binding, generation, hash);
            foreach (var boundary in new[] { RegistryCheckpointStep.PrimarySaved, RegistryCheckpointStep.ReplicaPublished })
            {
                RegistryState before;
                using (var root = new DirectoryFence(path))
                {
                    for (int index = 0; index < 24; index++)
                    {
                        GenerationRegistry.SetExpired(root.Root, store.Binding, generation, hash, true);
                        GenerationRegistry.SetExpired(root.Root, store.Binding, generation, hash, false);
                    }
                    before = GenerationRegistry.Read(root.Root, store.Binding);
                }
                string executable = Environment.ProcessPath ?? throw new IOException("Missing test host.");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                foreach (string argument in new[] { "--checkpoint-held-child", fixture.Root, name, boundary.ToString() })
                    start.ArgumentList.Add(argument);
                using var child = Process.Start(start) ?? throw new IOException("Could not start checkpoint worker.");
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    if (child.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult() != "checkpoint-paused")
                        throw new InvalidDataException("The worker did not reach its checkpoint boundary.");
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit();
                    using var root = new DirectoryFence(path);
                    using var operation = WindowsProbeNative.OpenPlainFile(root.Root, FolderBatch.LockName, FileAccess.ReadWrite, FileShare.None);
                    RequireSameState(before, GenerationRegistry.Read(root.Root, store.Binding));
                    GenerationRegistry.Reserve(root.Root, store.Binding, 2, maximumBytes: 16 * 1024);
                    RequireSameState(before, GenerationRegistry.Read(root.Root, store.Binding));
                    Console.WriteLine($"PASS real checkpoint worker terminated at {boundary} and reopened its durable state.");
                }
                finally
                {
                    if (!child.HasExited) { child.Kill(entireProcessTree: true); child.WaitForExit(); }
                }
            }
            passed = true;
        }
        finally
        {
            if (passed)
            {
                using (fixture.OpenRegisteredRoot()) { }
                Directory.Delete(fixture.Root, recursive: true);
            }
            else Console.Error.WriteLine("Synthetic worker checkpoint fixture preserved at " + fixture.Root);
        }
    }
}
