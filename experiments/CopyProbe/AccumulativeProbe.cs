using System.Diagnostics;
using System.Reflection;

internal static class AccumulativeProbe
{
    internal static int Run()
    {
        using (var fixture = new Fixture())
        {
            string initial = fixture.Update();
            File.WriteAllText(Path.Combine(fixture.Source, "documento.txt"), "actualizado");
            File.WriteAllText(Path.Combine(fixture.Source, "nuevo.txt"), "nuevo");
            File.Delete(Path.Combine(fixture.Source, "retirado.txt"));
            Directory.CreateDirectory(Path.Combine(fixture.Store.MirrorCurrentPath, "Carpeta ajena"));
            File.WriteAllText(Path.Combine(fixture.Store.MirrorCurrentPath, "Carpeta ajena", "exclusivo.txt"), "conservar");
            var review = fixture.Store.PreviewMirror(fixture.Job.Source.VolumePath);
            Check(review.Added == 1 && review.Replaced == 1 && review.Removed == 0 && review.RemovedDirectories == 0 &&
                review.Conflicts.Length == 0, "The cumulative review lost its additive semantics.");
            string latest = fixture.Store.BeginMirror(review);
            Check(File.ReadAllText(Path.Combine(fixture.Store.MirrorCurrentPath, "documento.txt")) == "actualizado" &&
                File.ReadAllText(Path.Combine(fixture.Store.MirrorCurrentPath, "retirado.txt")) == "anterior" &&
                File.ReadAllText(Path.Combine(fixture.Store.MirrorCurrentPath, "Carpeta ajena", "exclusivo.txt")) == "conservar",
                "The cumulative update lost current or destination-exclusive data.");
            Check(!fixture.Store.Verify(latest) && !fixture.Store.Verify(initial), "A cumulative point was not verifiable.");
            string output = fixture.Folder("Recuperación acumulativa");
            fixture.RemoveSource();
            fixture.Store.Restore(latest, output);
            Check(File.ReadAllText(Path.Combine(output, "Carpeta ajena", "exclusivo.txt")) == "conservar" &&
                File.ReadAllText(Path.Combine(output, "retirado.txt")) == "anterior", "Offline restoration lost destination-exclusive data.");
            var policy = new RetentionPolicy(1, 0, 0, 1);
            DateTimeOffset later = DateTimeOffset.UtcNow.AddDays(2);
            string retention = fixture.Store.BeginRetention(fixture.Store.PreviewRetention(policy, later), clock: later);
            Check(!fixture.Store.Verify(latest), "Retention lost a destination-exclusive object referenced by the current copy.");
            fixture.Store.UndoRetention(retention);
        }
        using (var fixture = new Fixture())
        {
            fixture.Update();
            string current = Path.Combine(fixture.Store.MirrorCurrentPath, "documento.txt");
            File.WriteAllText(current, "edición externa");
            File.WriteAllText(Path.Combine(fixture.Source, "documento.txt"), "nuevo contenido de origen");
            var review = fixture.Store.PreviewMirror(fixture.Job.Source.VolumePath);
            Check(review.Conflicts.SequenceEqual(["documento.txt"]), "A destination edit did not cause a cumulative conflict.");
            Reject(() => fixture.Store.BeginMirror(review));
            Check(File.ReadAllText(current) == "edición externa", "A cumulative conflict was overwritten.");
            string generation = fixture.Store.RegisteredGenerations().MirrorGeneration!;
            Check(!fixture.Store.Verify(generation), "The independent recovery data changed after a current-folder edit.");
            Reject(() => fixture.Store.VerifyCurrent(generation));
        }
        CheckSourceAndDestinationGuards();
        foreach (var boundary in Enum.GetValues<MirrorStep>().Where(step => step != MirrorStep.DirectoryRemoved))
        {
            using var fixture = new Fixture();
            fixture.Update();
            File.WriteAllText(Path.Combine(fixture.Source, "documento.txt"), "segunda versión");
            File.Delete(Path.Combine(fixture.Source, "retirado.txt"));
            File.WriteAllText(Path.Combine(fixture.Store.MirrorCurrentPath, "exclusivo.txt"), "exclusivo nuevo");
            var review = fixture.Store.PreviewMirror(fixture.Job.Source.VolumePath);
            bool reached = false;
            Reject(() => fixture.Store.BeginMirror(review, checkpoint: step =>
            {
                if (step != boundary) return;
                reached = true;
                throw new IOException("Injected cumulative interruption.");
            }));
            Check(reached, "A cumulative publication boundary was not reached.");
            if (boundary >= MirrorStep.Ready) fixture.RemoveSource();
            var reopened = VersionStore.OpenProduct(fixture.Job);
            reopened.ResumeMirror(review.Generation);
            Check(!reopened.VerifyCurrent(review.Generation) &&
                File.ReadAllText(Path.Combine(reopened.MirrorCurrentPath, "documento.txt")) == "segunda versión" &&
                File.ReadAllText(Path.Combine(reopened.MirrorCurrentPath, "retirado.txt")) == "anterior" &&
                File.ReadAllText(Path.Combine(reopened.MirrorCurrentPath, "exclusivo.txt")) == "exclusivo nuevo",
                "An interrupted cumulative publication lost data after reopening.");
        }
        CheckTerminatedWorker();
        Console.WriteLine("PASS cumulative copies add and update, preserve destination-exclusive data, reject external conflicts and resume publication boundaries.");
        return 0;
    }

    private static void CheckSourceAndDestinationGuards()
    {
        using var fixture = new Fixture();
        string first = fixture.Update();
        string path = Path.Combine(fixture.Source, "documento.txt");
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            Reject(() => fixture.Store.PreviewMirror(fixture.Job.Source.VolumePath));
        string outside = fixture.Folder("No atravesar");
        string link = Path.Combine(fixture.Source, "Enlace excluido");
        File.WriteAllText(Path.Combine(outside, "ajeno.txt"), "intacto");
        PathProbe.CreateJunction(link, outside);
        Reject(() => fixture.Store.PreviewMirror(fixture.Job.Source.VolumePath));
        Directory.Delete(link);
        Check(File.ReadAllText(Path.Combine(outside, "ajeno.txt")) == "intacto", "The incomplete inventory wrote outside the source.");
        var stale = fixture.Store.PreviewMirror(fixture.Job.Source.VolumePath);
        File.Delete(path);
        // The resulting cumulative content would be identical. The independently
        // recorded source inventory must still invalidate the earlier review.
        Reject(() => fixture.Store.BeginMirror(stale));
        Check(!fixture.Store.VerifyCurrent(first), "A stale review changed the accumulated copy.");
        var beforeForeign = fixture.Store.PreviewMirror(fixture.Job.Source.VolumePath);
        string foreign = Path.Combine(fixture.Store.MirrorCurrentPath, "ajeno.txt");
        File.WriteAllText(foreign, "conservar");
        Reject(() => fixture.Store.BeginMirror(beforeForeign));
        Check(File.ReadAllText(foreign) == "conservar", "A stale destination review changed a foreign file.");
        fixture.RemoveSource();
        Reject(() => fixture.Store.PreviewMirror(fixture.Job.Source.VolumePath));
        Directory.CreateDirectory(fixture.Source);
        Reject(() => fixture.Store.PreviewMirror(fixture.Job.Source.VolumePath));
        Check(fixture.Store.ListPoints().Length == 1, "An unavailable or substituted source published another cumulative point.");
    }

    internal static int InterruptChild(string root)
    {
        var workspace = LabWorkspace.Open(root);
        using var registry = new ProductJobRegistry(Path.Combine(workspace.Root, "Configuración"));
        var job = registry.List().Jobs.Single();
        var store = VersionStore.OpenProduct(job);
        store.BeginMirror(store.PreviewMirror(job.Source.VolumePath), checkpoint: step =>
        {
            if (step != MirrorStep.Retired) return;
            Console.WriteLine("READY");
            Console.Out.Flush();
            Console.ReadLine();
        });
        return 0;
    }

    private static void CheckTerminatedWorker()
    {
        using var fixture = new Fixture();
        string previous = fixture.Update();
        File.WriteAllText(Path.Combine(fixture.Source, "documento.txt"), "tras interrupción");
        File.WriteAllText(Path.Combine(fixture.Store.MirrorCurrentPath, "exclusivo.txt"), "conservar tras interrupción");
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--interrupt-accumulative-child");
        start.ArgumentList.Add(fixture.WorkspaceRoot);
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start cumulative worker.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            string? line = worker.StandardOutput.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
            Check(line == "READY", "The cumulative worker did not reach retirement.");
            worker.Kill();
            Check(worker.WaitForExit(15000), "The cumulative worker did not terminate.");
        }
        finally { if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(15000); } }
        fixture.RemoveSource();
        var reopened = VersionStore.OpenProduct(fixture.Job);
        string pending = reopened.ListMirrorOperations().Single(operation => !operation.Applied).Generation;
        reopened.Restore(previous, fixture.Folder("Recuperación anterior tras terminar proceso"));
        reopened.Restore(pending, fixture.Folder("Recuperación nueva tras terminar proceso"));
        reopened.ResumeMirror(pending);
        Check(!reopened.VerifyCurrent(pending) &&
            File.ReadAllText(Path.Combine(reopened.MirrorCurrentPath, "exclusivo.txt")) == "conservar tras interrupción",
            "Cumulative worker recovery lost destination-exclusive data.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly LabWorkspace workspace = LabWorkspace.Create();
        internal string WorkspaceRoot => workspace.Root;
        internal string Source { get; }
        internal ProductJob Job { get; }
        internal VersionStore Store { get; }
        internal Fixture()
        {
            Source = Folder("Origen acumulativo");
            File.WriteAllText(Path.Combine(Source, "documento.txt"), "primera versión");
            File.WriteAllText(Path.Combine(Source, "retirado.txt"), "anterior");
            Directory.CreateDirectory(Path.Combine(Source, "Carpeta vacía"));
            using var registry = new ProductJobRegistry(Folder("Configuración"));
            Job = registry.Create(new("Copia acumulativa sintética", ProductBackupMode.Accumulative, null),
                LocalJobRoots.ReviewDedicatedDestination(Source, Folder("Destinos")));
            Store = VersionStore.OpenProduct(Job, initialize: true);
            Store.InitializeMirror(resumeInitialization: true);
        }
        internal string Folder(string name) => Directory.CreateDirectory(Path.Combine(workspace.Root, name)).FullName;
        internal string Update() => Store.BeginMirror(Store.PreviewMirror(Job.Source.VolumePath));
        internal void RemoveSource() => Directory.Move(Source, Path.Combine(workspace.Root, "Origen apartado"));
        public void Dispose()
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("The cumulative operation did not refuse an unsafe or interrupted request.");
    }
}
