using System.Diagnostics;
using System.Reflection;

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

        foreach (bool begin in new[] { true, false })
        foreach (var boundary in Enum.GetValues<RegistryStep>())
        {
            using var fixture = new Fixture();
            fixture.Change();
            var review = fixture.Store.PreviewMirror(fixture.Source);
            bool reached = false;
            Action<RegistryStep> interrupt = step =>
            {
                if (step != boundary) return;
                reached = true;
                throw new IOException("Injected mirror registry interruption.");
            };
            Reject(() => fixture.Store.BeginMirror(review, massReviewed: true,
                registryCheckpoint: begin ? null : interrupt, beginRegistryCheckpoint: begin ? interrupt : null));
            Check(reached, "The mirror registry append was not interrupted.");
            Directory.Move(fixture.Source, Path.Combine(fixture.Workspace.Root, "fuera"));
            fixture.Store.ResumeMirror(review.Generation);
            fixture.VerifyUpdated();
        }
        Console.WriteLine("PASS the mirror repairs the exact split registry events for start and completion with source absent.");
        CheckGuards();
        CheckReadyConflict();
        CheckMissingJournal();
        CheckTerminatedWorker();
        return 0;
    }

    private static void CheckMissingJournal()
    {
        using var fixture = new Fixture();
        fixture.Change();
        var review = fixture.Store.PreviewMirror(fixture.Source);
        Reject(() => fixture.Store.BeginMirror(review, massReviewed: true, checkpoint: step =>
        {
            if (step == MirrorStep.Retired) throw new IOException("Injected after retirement.");
        }));
        Check(fixture.Store.RegisteredGenerations().PendingMirrorGeneration == review.Generation,
            "A file was retired before the mirrored intent was registered.");
        string journal = Path.Combine(fixture.Destination, "mirror", "operations", review.Generation);
        string moved = Path.Combine(fixture.Workspace.Root, "apartado");
        Directory.Move(journal, moved);
        Reject(() => fixture.Store.PreviewRetention(new(1, 0, 0, 1)));
        Reject(() => fixture.Store.MirrorStatus());
        Directory.Move(moved, journal);
        fixture.Store.ResumeMirror(review.Generation);
        Check(fixture.Store.RegisteredGenerations().PendingMirrorGeneration is null,
            "A completed operation left a pending mirror reference.");
        Console.WriteLine("PASS the replicated intent blocks cleanup when the pending mirror journal disappears.");
    }

    internal static int InterruptChild(string root)
    {
        var workspace = LabWorkspace.Open(root);
        var store = VersionStore.Open(workspace, Path.Combine(root, "almacén"));
        var review = store.PreviewMirror(Path.Combine(root, "origen"));
        store.BeginMirror(review, massReviewed: true, checkpoint: step =>
        {
            if (step != MirrorStep.Retired)
                return;
            Console.WriteLine("READY");
            Console.Out.Flush();
            Console.ReadLine();
        });
        return 0;
    }

    private static void CheckTerminatedWorker()
    {
        using var fixture = new Fixture();
        fixture.Change();
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--interrupt-mirror-child");
        start.ArgumentList.Add(fixture.Workspace.Root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start mirror worker.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            string? line = process.StandardOutput.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
            Check(line == "READY", "The child did not retire a file.");
            process.Kill();
            Check(process.WaitForExit(15000), "The child did not exit.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(15000); }
        }
        Directory.Move(fixture.Source, Path.Combine(fixture.Workspace.Root, "origen apartado"));
        var reopened = VersionStore.Open(LabWorkspace.Open(fixture.Workspace.Root), fixture.Destination);
        string pending = reopened.ListMirrorOperations().Single(item => !item.Applied).Generation;
        reopened.Restore(fixture.First, fixture.Folder("restauración durante interrupción"));
        reopened.ResumeMirror(pending);
        fixture.VerifyUpdated();
        Console.WriteLine("PASS terminating the mirror worker during retirement preserves independent restore and allows resumption without source.");
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
        string linked = Path.Combine(fixture.Source, "no enumerar");
        string external = fixture.Folder("fuera del origen");
        File.WriteAllText(Path.Combine(external, "conservar.txt"), "afuera");
        PathProbe.CreateJunction(linked, external);
        Reject(() => fixture.Store.PreviewMirror(fixture.Source));
        Directory.Delete(linked);
        Check(File.ReadAllText(Path.Combine(external, "conservar.txt")) == "afuera", "The mirror touched the junction target.");
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
