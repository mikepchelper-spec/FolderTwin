internal static class MirrorProbe
{
    internal static int Run()
    {
        foreach (var boundary in Enum.GetValues<MirrorStep>())
        {
            using var fixture = new Fixture();
            fixture.Change();
            var review = fixture.Store.PreviewMirror(fixture.Source);
            Check(review is { Added: 2, Replaced: 1, Removed: 2, Conflicts.Length: 0, RequiresMassReview: true },
                "The preview did not identify updates, removals or a type change.");
            Reject(() => fixture.Store.BeginMirror(review));
            bool reached = false;
            Reject(() => fixture.Store.BeginMirror(review, massReviewed: true, checkpoint: step =>
            {
                if (step != boundary) return;
                reached = true;
                throw new IOException("Injected mirror interruption.");
            }));
            Check(reached, $"Mirror did not reach {boundary}.");
            var store = VersionStore.Open(fixture.Workspace, fixture.Destination);
            if (boundary != MirrorStep.Applied)
                Reject(() => store.PreviewRetention(new(1, 0, 0, 1)));
            string away = Path.Combine(fixture.Workspace.Root, "apartado");
            if (boundary >= MirrorStep.Ready)
                Directory.Move(fixture.Source, away);
            store.ResumeMirror(review.Generation);
            store.ResumeMirror(review.Generation);
            Check(store.ListMirrorOperations().All(item => item.Applied) &&
                store.RegisteredGenerations().MirrorGeneration == review.Generation,
                $"Reopening did not finish the mirror after {boundary}.");
            fixture.VerifyUpdated();
            var output = fixture.Folder("restaurado");
            store.Restore(fixture.First, output);
            Check(File.ReadAllText(Path.Combine(output, "se retira.txt")) == "anterior" &&
                File.ReadAllText(Path.Combine(output, "cambia.txt")) == "uno" &&
                Directory.Exists(Path.Combine(output, "antes carpeta", "vacía")),
                "The previous generation did not preserve the retired content.");
        }
        Console.WriteLine("PASS mirror reopens at every transition, completes without the source after ready, and restores retired content.");

        foreach (var boundary in Enum.GetValues<RegistryStep>())
        {
            using var fixture = new Fixture();
            fixture.Change();
            var review = fixture.Store.PreviewMirror(fixture.Source);
            bool reached = false;
            Reject(() => fixture.Store.BeginMirror(review, massReviewed: true, registryCheckpoint: step =>
            {
                if (step != boundary) return;
                reached = true;
                throw new IOException("Injected mirror registry interruption.");
            }));
            Check(reached, "The mirror registry append was not interrupted.");
            Directory.Move(fixture.Source, Path.Combine(fixture.Workspace.Root, "fuera"));
            fixture.Store.ResumeMirror(review.Generation);
            fixture.VerifyUpdated();
        }
        Console.WriteLine("PASS the completed mirror repairs the exact split registry publication with source absent.");
        CheckGuards();
        CheckReadyConflict();
        return 0;
    }

    private static void CheckReadyConflict()
    {
        using var fixture = new Fixture();
        fixture.Change();
        var review = fixture.Store.PreviewMirror(fixture.Source);
        Reject(() => fixture.Store.BeginMirror(review, massReviewed: true, checkpoint: step =>
        {
            if (step == MirrorStep.Ready) throw new IOException("Injected before retirement.");
        }));
        string current = fixture.Store.MirrorCurrentPath;
        string foreign = Path.Combine(current, "apareció tarde.txt");
        File.WriteAllText(foreign, "externo");
        Reject(() => fixture.Store.ResumeMirror(review.Generation));
        Check(File.ReadAllText(foreign) == "externo" &&
            File.ReadAllText(Path.Combine(current, "se retira.txt")) == "anterior" &&
            File.ReadAllText(Path.Combine(current, "cambia.txt")) == "uno",
            "Resumption changed files despite an unexpected destination entry.");
        File.Delete(foreign);
        fixture.Store.ResumeMirror(review.Generation);
        fixture.VerifyUpdated();
        Console.WriteLine("PASS a conflict introduced after staging stops all further moves until resolved.");
    }

    private static void CheckGuards()
    {
        using var fixture = new Fixture();
        string current = fixture.Store.MirrorCurrentPath;
        string foreign = Path.Combine(current, "ajeno.txt");
        File.WriteAllText(foreign, "preservar");
        var conflict = fixture.Store.PreviewMirror(fixture.Source);
        Check(conflict.Conflicts.SequenceEqual(["ajeno.txt"]), "Preview hid unexpected destination content.");
        Reject(() => fixture.Store.BeginMirror(conflict, true, true));
        Check(File.ReadAllText(foreign) == "preservar", "A conflict was overwritten.");
        File.Delete(foreign);
        var stale = fixture.Store.PreviewMirror(fixture.Source);
        string changed = Path.Combine(fixture.Source, "cambia.txt");
        var timestamp = File.GetLastWriteTimeUtc(changed);
        File.WriteAllText(changed, "dos");
        File.SetLastWriteTimeUtc(changed, timestamp);
        Reject(() => fixture.Store.BeginMirror(stale, true, true));
        using (var writer = new FileStream(changed, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            Reject(() => fixture.Store.PreviewMirror(fixture.Source));
        string away = Path.Combine(fixture.Workspace.Root, "fuera");
        Directory.Move(fixture.Source, away);
        Reject(() => fixture.Store.PreviewMirror(fixture.Source));
        Directory.CreateDirectory(fixture.Source);
        Reject(() => fixture.Store.PreviewMirror(fixture.Source));
        Directory.Delete(fixture.Source);
        Directory.Move(away, fixture.Source);
        foreach (string file in Directory.GetFiles(fixture.Source))
            File.Delete(file);
        Directory.Delete(Path.Combine(fixture.Source, "antes carpeta", "vacía"));
        Directory.Delete(Path.Combine(fixture.Source, "antes carpeta"));
        var empty = fixture.Store.PreviewMirror(fixture.Source, 100);
        Check(empty is { RequiresEmptyReview: true, RequiresMassReview: true, Removed: 3 }, "Empty origin was not independently gated.");
        Reject(() => fixture.Store.BeginMirror(empty, emptyReviewed: true));
        Reject(() => fixture.Store.BeginMirror(empty, massReviewed: true));
        Check(File.Exists(Path.Combine(current, "se retira.txt")), "A rejected operation retired a file.");
        fixture.Store.BeginMirror(empty, emptyReviewed: true, massReviewed: true);
        Check(!Directory.EnumerateFileSystemEntries(current).Any(), "The explicitly reviewed empty mirror is not empty.");
        Check(!fixture.Store.Verify(fixture.First), "Empty mirror lost the previous point.");

        // A later ordinary backup must not cause retention to expire the mirror head.
        File.WriteAllText(changed, "contenido nuevo");
        string later = VersionStore.NewGenerationId();
        fixture.Store.CreatePoint(fixture.Source, later);
        var retain = fixture.Store.PreviewRetention(new(1, 0, 0, 1));
        Check(retain.Keep.Contains(empty.Generation) && retain.Keep.Contains(later),
            "Retention did not protect the published mirror head.");
        Console.WriteLine("PASS missing, substituted, busy or stale sources and unexpected destination entries stop retirement; empty requires two approvals.");
    }

    private sealed class Fixture : IDisposable
    {
        internal LabWorkspace Workspace { get; } = LabWorkspace.Create();
        internal string Source { get; }
        internal string Destination { get; }
        internal VersionStore Store { get; }
        internal string First { get; }
        internal Fixture()
        {
            Source = Folder("origen");
            Destination = Folder("almacén");
            File.WriteAllText(Path.Combine(Source, "cambia.txt"), "uno");
            File.WriteAllText(Path.Combine(Source, "se retira.txt"), "anterior");
            File.WriteAllText(Path.Combine(Source, "antes archivo"), "anterior");
            Directory.CreateDirectory(Path.Combine(Source, "antes carpeta", "vacía"));
            Store = VersionStore.Initialize(Workspace, Destination);
            Store.InitializeMirror();
            First = Store.BeginMirror(Store.PreviewMirror(Source));
        }

        internal void Change()
        {
            File.WriteAllText(Path.Combine(Source, "cambia.txt"), "dos");
            File.Delete(Path.Combine(Source, "se retira.txt"));
            File.Delete(Path.Combine(Source, "antes archivo"));
            Directory.CreateDirectory(Path.Combine(Source, "antes archivo"));
            File.WriteAllText(Path.Combine(Source, "antes archivo", "nuevo.txt"), "nuevo");
            Directory.Delete(Path.Combine(Source, "antes carpeta", "vacía"));
            Directory.Delete(Path.Combine(Source, "antes carpeta"));
            File.WriteAllText(Path.Combine(Source, "antes carpeta"), "ahora archivo");
        }

        internal void VerifyUpdated()
        {
            string current = Store.MirrorCurrentPath;
            Check(File.ReadAllText(Path.Combine(current, "cambia.txt")) == "dos" &&
                !File.Exists(Path.Combine(current, "se retira.txt")) &&
                File.ReadAllText(Path.Combine(current, "antes archivo", "nuevo.txt")) == "nuevo" &&
                File.ReadAllText(Path.Combine(current, "antes carpeta")) == "ahora archivo",
                "The current tree did not match the approved source.");
        }

        internal string Folder(string name) => Directory.CreateDirectory(Path.Combine(Workspace.Root, name)).FullName;
        public void Dispose()
        {
            using (Workspace.OpenRegisteredRoot()) { }
            Directory.Delete(Workspace.Root, recursive: true);
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("A mirror operation unexpectedly succeeded.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
