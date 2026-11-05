using System.Security.Cryptography;

internal static class LocalJobRootsProbe
{
    internal static int Run()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            string origin = Path.Combine(workspace.Root, "Origen ñ");
            string target = Path.Combine(workspace.Root, "Destino");
            string nested = Path.Combine(origin, "Anidada");
            string junction = Path.Combine(workspace.Root, "Enlace");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(origin, "prueba.txt"), "solo lectura");
            LocalRootReview review;
            using (var first = new LocalJobRoots(origin, target))
                review = first.Review;
            using (var reopened = LocalJobRoots.Reopen(review))
                Check(reopened.Review.SameVolume && reopened.Review.AvailableBytes > 0 &&
                    reopened.Review.Destination.VolumePath == review.Destination.VolumePath);

            Reject(() => new LocalJobRoots(origin, review.Source.VolumePath));
            Reject(() => new LocalJobRoots(review.Source.VolumePath, nested));
            Reject(() => new LocalJobRoots(nested, origin));
            Reject(() => new LocalJobRoots(Path.GetPathRoot(origin)!, target));

            PathProbe.CreateJunction(junction, origin);
            try { Reject(() => new LocalJobRoots(Path.Combine(junction, "Anidada"), target)); }
            finally { Directory.Delete(junction); }

            string moved = Path.Combine(workspace.Root, "Destino original");
            Directory.Move(target, moved);
            Directory.CreateDirectory(target);
            Reject(() => LocalJobRoots.Reopen(review));
            Check(!Directory.EnumerateFileSystemEntries(target).Any() &&
                !Directory.EnumerateFileSystemEntries(moved).Any() &&
                File.ReadAllText(Path.Combine(origin, "prueba.txt")) == "solo lectura");
            using var replacement = new LocalJobRoots(origin, target);
            var binding = DestinationBinding.Register(replacement.DestinationHandle);
            Directory.CreateDirectory(Path.Combine(target, "Subcarpeta"));
            byte[] expected = SHA256.HashData("solo lectura"u8);
            try
            {
                VerifiedCopy.RunBound(replacement, binding, @"..\ajeno.txt", "ajeno.txt", "ajeno.tmp", expected);
                throw new InvalidOperationException("The verified transfer accepted an escaped source path.");
            }
            catch (ArgumentException) { }
            Check(!File.Exists(Path.Combine(target, CopyRecovery.LockPath("ajeno.tmp"))));
            VerifiedCopy.RunBound(replacement, binding, "prueba.txt", @"Subcarpeta\copia.txt", @"Subcarpeta\copia.tmp", expected);
            Check(File.ReadAllText(Path.Combine(target, "Subcarpeta", "copia.txt")) == "solo lectura");
            VerifyDedicatedDestination(workspace.Root, origin);
            Console.WriteLine("PASS local roots remain read-only during review; aliases, nested roots, junctions and substituted directories are rejected.");
            Console.WriteLine("PASS the marker-bound copy publishes a verified nested file and rejects path escapes before writing.");
            return 0;
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    private static void VerifyDedicatedDestination(string workspace, string origin)
    {
        var planned = LocalJobRoots.ReviewDedicatedDestination(origin, workspace);
        string child = Path.Combine(workspace, planned.FolderName);
        Check(!Directory.Exists(child));
        Directory.CreateDirectory(child);
        string unrelated = Path.Combine(child, "existente.txt");
        File.WriteAllText(unrelated, "conservar");
        Reject(() => LocalJobRoots.CreateDedicatedDestination(planned));
        Check(File.ReadAllText(unrelated) == "conservar" && Directory.EnumerateFileSystemEntries(child).Count() == 1);

        string parent = Directory.CreateDirectory(Path.Combine(workspace, "Padre")).FullName;
        var stale = LocalJobRoots.ReviewDedicatedDestination(origin, parent);
        Directory.Move(parent, Path.Combine(workspace, "Padre anterior"));
        Directory.CreateDirectory(parent);
        Reject(() => LocalJobRoots.CreateDedicatedDestination(stale));
        Check(!Directory.EnumerateFileSystemEntries(parent).Any());

        var fresh = LocalJobRoots.ReviewDedicatedDestination(origin, parent);
        using var created = LocalJobRoots.CreateDedicatedDestination(fresh);
        Check(Directory.Exists(Path.Combine(parent, fresh.FolderName)) &&
            !Directory.EnumerateFileSystemEntries(Path.Combine(parent, fresh.FolderName)).Any());
        Console.WriteLine("PASS dedicated destination creation refuses occupied names and substituted parents.");
    }

    private static void Reject(Func<LocalJobRoots> open)
    {
        try { using var opened = open(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("The unsafe product root selection was accepted.");
    }

    private static void Check(bool valid)
    {
        if (!valid)
            throw new InvalidOperationException("The product root review changed data or lost a volume identity.");
    }
}
