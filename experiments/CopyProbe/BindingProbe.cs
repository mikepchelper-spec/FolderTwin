using System.Text.Json;

internal static class BindingProbe
{
    public static void Run()
    {
        using var fixture = new Fixture();
        fixture.Seed("registered destination"u8);
        string original = Path.Combine(fixture.Root, "registered");
        string moved = Path.Combine(fixture.Root, "renamed");
        string configuration = Path.Combine(fixture.Root, "destination.json");
        string marker = Path.Combine(original, DestinationBinding.MarkerName);
        string pending = Path.Combine(original, "pending.tmp");
        string final = Path.Combine(original, "backup.bin");
        try
        {
            Directory.CreateDirectory(original);
            using (var directory = new DirectoryFence(original))
                DestinationBinding.Register(directory.Root).SaveNew(configuration);

            DestinationBinding binding = DestinationBinding.Load(configuration);
            using (var directory = new DirectoryFence(binding.LastVolumePath))
            {
                Expect<IOException>(() => DestinationBinding.Register(directory.Root));
                using var verifiedMarker = binding.OpenVerifiedMarker(directory.Root);
            }
            Console.WriteLine("PASS the saved destination reopens via its volume GUID path and keeps its existing marker.");

            byte[] markerBytes = File.ReadAllBytes(marker);
            File.Delete(marker);
            Expect<InvalidDataException>(() => VerifiedCopy.Run(
                fixture.Source, final, pending, recoveryRoot: fixture.Root, binding: binding));
            if (Directory.EnumerateFileSystemEntries(original).Any())
                throw new InvalidDataException("Copy started without a destination marker.");
            File.WriteAllBytes(marker, Guid.NewGuid().ToByteArray());
            Expect<InvalidDataException>(() => VerifiedCopy.Run(
                fixture.Source, final, pending, recoveryRoot: fixture.Root, binding: binding));
            if (Directory.EnumerateFileSystemEntries(original).Count() != 1)
                throw new InvalidDataException("Copy started with the wrong marker.");
            File.WriteAllBytes(marker, markerBytes);
            Console.WriteLine("PASS a missing or altered marker stops a configured copy before writing.");

            Expect<InjectedFailure>(() => VerifiedCopy.Run(fixture.Source, final, pending, (step, _) =>
            {
                if (step == CopyStep.Verified)
                {
                    Expect<IOException>(() => File.Delete(marker));
                    throw new InjectedFailure();
                }
            }, recoveryRoot: fixture.Root, binding: binding));

            Directory.Move(original, moved);
            Directory.CreateDirectory(original);
            File.Copy(Path.Combine(moved, DestinationBinding.MarkerName), marker);
            Expect<InvalidDataException>(() => CopyRecovery.Recover(fixture.Root, pending, final, binding));
            Expect<InvalidDataException>(() => VerifiedCopy.Run(
                fixture.Source, final, pending, recoveryRoot: fixture.Root, binding: binding));
            if (Directory.EnumerateFileSystemEntries(original).Count() != 1)
                throw new InvalidDataException("An operation touched the replacement destination.");
            Console.WriteLine("PASS a substitute destination with a copied marker cannot start or recover a copy.");

            string volumeRoot;
            using (var directory = WindowsProbeNative.OpenPlainDirectory(fixture.Root))
                volumeRoot = WindowsProbeNative.VolumePath(directory);
            string relocated = Path.Combine(volumeRoot, Path.GetFileName(moved));
            string movedPending = Path.Combine(relocated, "pending.tmp");
            string movedFinal = Path.Combine(relocated, "backup.bin");
            DestinationBinding reloaded = DestinationBinding.Load(configuration);
            if (CopyRecovery.Recover(volumeRoot, movedPending, movedFinal, reloaded) != RecoveryResult.Published ||
                CopyRecovery.Recover(volumeRoot, movedPending, movedFinal, reloaded) != RecoveryResult.AlreadyPublished ||
                !File.ReadAllBytes(movedFinal).AsSpan().SequenceEqual("registered destination"u8))
                throw new InvalidDataException("The renamed destination did not recover.");
            Console.WriteLine("PASS the original destination recovers after rename using the volume GUID path.");

            string originalConfiguration = File.ReadAllText(configuration);
            File.WriteAllText(configuration, """{"Version":1}""");
            Expect<JsonException>(() => DestinationBinding.Load(configuration));
            File.WriteAllText(configuration, """{"Version":2,""" + originalConfiguration[1..]);
            Expect<JsonException>(() => DestinationBinding.Load(configuration));
            Console.WriteLine("PASS incomplete and duplicate configuration fields are rejected.");
        }
        finally
        {
            foreach (string directory in new[] { original, moved })
            {
                if (!Directory.Exists(directory))
                    continue;
                string temporary = Path.Combine(directory, "pending.tmp");
                foreach (string path in new[] { temporary, CopyRecovery.ReceiptPath(temporary),
                    CopyRecovery.ReceiptDraftPath(temporary), CopyRecovery.LockPath(temporary),
                    Path.Combine(directory, DestinationBinding.MarkerName), Path.Combine(directory, "backup.bin") })
                    File.Delete(path);
                Directory.Delete(directory);
            }
            File.Delete(configuration);
        }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
