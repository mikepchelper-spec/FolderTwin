using System.Security.Cryptography;

internal static class WorkspaceProbe
{
    internal static int Run()
    {
        var workspace = LabWorkspace.Create();
        var foreign = LabWorkspace.Create();
        var original = Directory.CreateDirectory(Path.Combine(workspace.Root, "entrada café")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(workspace.Root, "respaldo 漢字")).FullName;
        var restored = Directory.CreateDirectory(Path.Combine(workspace.Root, "restauración")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(original, "nivel")).FullName;
        string sourceFile = Path.Combine(original, "inventario ñ.txt");
        string nestedFile = Path.Combine(nested, "datos.bin");
        string conflict = Path.Combine(target, "ocupado.txt");
        string link = Path.Combine(workspace.Root, "redirección");
        try
        {
            File.WriteAllText(sourceFile, "contenido sintético configurable");
            File.WriteAllBytes(nestedFile, [42, 17]);
            byte[] expected = SHA256.HashData(File.ReadAllBytes(sourceFile));
            DateTime date = File.GetLastWriteTimeUtc(sourceFile);
            Reject(() => workspace.Analyze(original, original));
            Reject(() => workspace.Analyze(original, nested));
            Reject(() => workspace.Analyze(original, foreign.Root));
            PathProbe.CreateJunction(link, foreign.Root);
            Reject(() => workspace.Analyze(original, link));
            Directory.Delete(link);

            File.WriteAllText(conflict, "preservar");
            Reject(() => workspace.Analyze(original, target));
            Check(File.ReadAllText(conflict) == "preservar", "Analysis modified a conflicting file.");
            File.Delete(conflict);
            FileAttributes attributes = File.GetAttributes(sourceFile);
            File.SetAttributes(sourceFile, attributes | FileAttributes.Offline);
            try
            {
                Reject(() => workspace.Analyze(original, target), "inventario ñ.txt · datos no disponibles localmente");
                Check(!Directory.EnumerateFileSystemEntries(target).Any(), "An unsupported source wrote to the destination.");
            }
            finally { File.SetAttributes(sourceFile, attributes); }
            var preview = workspace.Analyze(original, target);
            Check(preview.Plan.Files.Length == 2 && preview.Plan.Bytes == new FileInfo(sourceFile).Length + 2 &&
                preview.AvailableBytes > 0 && !Directory.EnumerateFileSystemEntries(target).Any(),
                "Preview did not read the arbitrary inventory and free space without writing.");
            Reject(() => workspace.Start(preview, measureAvailable: () => 0));
            Check(!Directory.EnumerateFileSystemEntries(target).Any(), "A space failure registered or wrote to the destination.");
            File.WriteAllText(conflict, "preservar de nuevo");
            Reject(() => workspace.Start(preview));
            Check(Directory.EnumerateFileSystemEntries(target).SequenceEqual([conflict]) &&
                File.ReadAllText(conflict) == "preservar de nuevo", "A late destination conflict was modified.");
            File.Delete(conflict);
            string moved = Path.Combine(workspace.Root, "destino original");
            Directory.Move(target, moved);
            Directory.CreateDirectory(target);
            try
            {
                Reject(() => workspace.Start(preview), "identidad");
                Check(!Directory.EnumerateFileSystemEntries(target).Any(), "The replacement destination was registered.");
            }
            finally
            {
                Directory.Delete(target);
                Directory.Move(moved, target);
            }
            Console.WriteLine("PASS configurable workspace rejects overlap, foreign paths, junctions, destination conflicts and insufficient capacity.");

            using (var writer = new FileStream(sourceFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                var partial = workspace.Start(preview);
                Check(!partial.Complete && partial.VerifiedFiles == 1 &&
                    !File.Exists(Path.Combine(target, BatchCatalog.CommitName)), "An open writer was not reported as partial.");
            }
            var reopened = LabWorkspace.Open(workspace.Root);
            var pending = reopened.OpenJob(target);
            Check(!pending.Confirmed && pending.Source == original && pending.FileCount == 2,
                "The pending job lost its selected roots or count.");
            Check(reopened.Resume(pending).Complete, "Retry did not confirm the configurable job.");
            var confirmed = LabWorkspace.Open(workspace.Root).OpenJob(target);
            Check(confirmed.Confirmed && confirmed.FileCount == 2 && reopened.Verify(confirmed).Complete,
                "The confirmed catalog did not verify its arbitrary file names.");
            Check(SHA256.HashData(File.ReadAllBytes(sourceFile)).AsSpan().SequenceEqual(expected) &&
                File.GetLastWriteTimeUtc(sourceFile) == date, "Copying modified the original content or date.");

            Directory.Move(original, Path.Combine(workspace.Root, "origen no disponible"));
            Check(reopened.Restore(confirmed, restored).Complete &&
                SHA256.HashData(File.ReadAllBytes(Path.Combine(restored, Path.GetFileName(sourceFile)))).AsSpan().SequenceEqual(expected) &&
                File.GetLastWriteTimeUtc(Path.Combine(restored, Path.GetFileName(sourceFile))) == date &&
                File.ReadAllBytes(Path.Combine(restored, "nivel", "datos.bin")).AsSpan().SequenceEqual(new byte[] { 42, 17 }),
                "Independent restoration failed when the original source was unavailable.");
            Console.WriteLine("PASS configurable workspace resumes, verifies and restores arbitrary synthetic data after reopening without the source.");

            File.Copy(Path.Combine(workspace.Root, LabWorkspace.MarkerName),
                Path.Combine(foreign.Root, LabWorkspace.MarkerName), overwrite: true);
            Reject(() => LabWorkspace.Open(foreign.Root));
            Console.WriteLine("PASS a copied workspace marker does not authorize a different directory.");
            return 0;
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(workspace.Root, recursive: true);
            Directory.Delete(foreign.Root, recursive: true);
        }
    }

    private static void Reject(Action operation, string? expectedDetail = null)
    {
        try { operation(); }
        catch (Exception error) when ((error is IOException or InvalidDataException or ArgumentException) &&
            (expectedDetail is null || error.Message.Contains(expectedDetail, StringComparison.Ordinal))) { return; }
        throw new InvalidOperationException("An invalid workspace operation was accepted.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
