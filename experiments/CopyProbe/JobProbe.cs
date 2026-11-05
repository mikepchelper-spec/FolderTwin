using System.Diagnostics;
using System.Reflection;

internal static class JobProbe
{
    internal static int Run()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "origen ñ")).FullName;
            string destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "versiones")).FullName;
            File.WriteAllText(Path.Combine(source, "dato.txt"), "contenido del trabajo");
            var store = VersionStore.Initialize(workspace, destination);
            Guid id = VersionJob.Create(workspace, source, destination);
            Check(VersionJob.Create(workspace, source, destination) == id && VersionJob.List(workspace).Jobs.Single().Id == id,
                "Reopening the same configured roots duplicated the job.");
            var interrupted = VersionJob.Run(workspace, id, (step, _) =>
            {
                if (step == CopyStep.Verified)
                    throw new IOException("Injected job interruption.");
            });
            Check(interrupted.State == VersionJobState.Review && !interrupted.HistoryProblem &&
                store.ListPending().Single().Id == interrupted.Generation && store.ListPoints().Length == 0,
                "The interrupted worker lost its pending generation.");
            string absent = Path.Combine(workspace.Root, "origen apartado");
            Directory.Move(source, absent);
            RunChild(workspace.Root, id, expectedExit: 0);
            Check(store.ListPoints().Single().Id == interrupted.Generation && store.ListPending().Length == 0,
                "The new worker did not recover its verified temporary without the source.");
            Check(VersionJob.History(workspace, id).Results.Select(result => result.State)
                .SequenceEqual(new[] { VersionJobState.Confirmed, VersionJobState.Review }),
                "The successful worker hid the prior failure.");
            var backdated = VersionJob.Run(workspace, id, clock: () => DateTimeOffset.UtcNow.AddDays(-1));
            Check(backdated.State == VersionJobState.WaitingForSource &&
                VersionJob.History(workspace, id).Results.First() == backdated,
                "A new run hid its missing source.");
            Directory.Move(absent, source);
            RunChild(workspace.Root, id, expectedExit: 0);
            Check(store.ListPoints().Length == 2, "A later run did not create a fresh generation.");
            Console.WriteLine("PASS a headless worker reopens its job, resumes a verified object without the source, preserves failures and creates a later point.");

            using (var scope = workspace.OpenRegisteredRoot())
            using (WindowsProbeNative.OpenPlainFile(scope.Root, $".foldertwin-job-{id:N}.lock", FileAccess.ReadWrite, FileShare.None))
                Check(VersionJob.Run(workspace, id).State == VersionJobState.Busy, "Concurrent workers were not excluded.");
            Directory.Move(destination, Path.Combine(workspace.Root, "destino apartado"));
            Check(VersionJob.Run(workspace, id).State == VersionJobState.WaitingForDestination,
                "A missing destination was not reported.");
            Directory.CreateDirectory(destination);
            foreach (string name in new[] { ".foldertwin-version-store", DestinationBinding.MarkerName, FolderBatch.LockName })
                File.Copy(Path.Combine(workspace.Root, "destino apartado", name), Path.Combine(destination, name));
            Check(VersionJob.Run(workspace, id).State == VersionJobState.Review &&
                Directory.EnumerateFileSystemEntries(destination).Count() == 3, "A substituted store was accepted or modified.");
            Directory.Delete(destination, recursive: true); // Only the three copied fixture control files.
            Directory.Move(Path.Combine(workspace.Root, "destino apartado"), destination);

            Directory.Move(source, absent);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "dato.txt"), "contenido del trabajo");
            Check(VersionJob.Run(workspace, id).State == VersionJobState.Review && store.ListPoints().Length == 2,
                "A replacement source with identical content was accepted.");
            Directory.Delete(source, recursive: true); // Only this test's replacement source.
            Directory.Move(absent, source);
            using (var target = new DirectoryFence(destination))
            using (WindowsProbeNative.OpenPlainFile(target.Root, FolderBatch.LockName, FileAccess.ReadWrite, FileShare.None))
                Check(VersionJob.Run(workspace, id).State == VersionJobState.Busy,
                    "A manual store operation did not exclude the independent worker.");
            var reconnected = VersionJob.Run(workspace, id);
            var afterReconnect = VersionJob.History(workspace, id).Results;
            Check(reconnected.State == VersionJobState.Confirmed && store.ListPoints().Length == 3 &&
                afterReconnect.First() == reconnected && afterReconnect.Any(result => result.State == VersionJobState.WaitingForDestination) &&
                afterReconnect.Any(result => result.State == VersionJobState.Review) && !store.Verify(reconnected.Generation!),
                "Returning the registered roots failed to recover or erased prior incidents.");
            string result = Directory.EnumerateFiles(workspace.Root, $".foldertwin-job-{id:N}-*.result").First();
            File.WriteAllText(result, "truncado");
            var history = VersionJob.History(workspace, id);
            Check(history.Unreadable == 1 && history.Results.Length >= 5 && File.ReadAllText(result) == "truncado",
                "A damaged diagnostic record hid other results or was overwritten.");
            Console.WriteLine("PASS per-job locking, unavailable roots, substituted identities and damaged history are reported without changing the backup.");
            CheckTerminatedWorker(workspace, source);
            return 0;
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    internal static int InterruptChild(string root, Guid id)
    {
        _ = VersionJob.Run(LabWorkspace.Open(root), id, (step, _) =>
        {
            if (step == CopyStep.Verified)
            {
                Console.WriteLine("job checkpoint");
                Console.Out.Flush();
                Thread.Sleep(Timeout.Infinite);
            }
        });
        return 2;
    }

    private static void CheckTerminatedWorker(LabWorkspace workspace, string source)
    {
        string destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "trabajador terminado")).FullName;
        var store = VersionStore.Initialize(workspace, destination);
        Guid id = VersionJob.Create(workspace, source, destination);
        using (var child = StartChild(workspace.Root, id, interrupt: true))
        {
            try
            {
                var checkpoint = child.StandardOutput.ReadLineAsync();
                if (!checkpoint.Wait(30_000) || checkpoint.Result != "job checkpoint")
                    throw new InvalidOperationException("The worker did not reach its verified object checkpoint.");
            }
            finally
            {
                if (!child.HasExited)
                    child.Kill();
                child.WaitForExit();
            }
        }
        string generation = store.ListPending().Single().Id;
        Check(store.ListPoints().Length == 0 && VersionJob.History(workspace, id).Results.Length == 0,
            "A terminated worker reported a confirmed point or a finished attempt.");
        string moved = Path.Combine(workspace.Root, "origen del trabajador terminado");
        Directory.Move(source, moved);
        try { RunChild(workspace.Root, id, expectedExit: 0); }
        finally { Directory.Move(moved, source); }
        Check(store.ListPoints().Single().Id == generation &&
            VersionJob.History(workspace, id).Results.Single().Generation == generation && !store.Verify(generation),
            "The new worker failed to recover the point after real process termination.");
        bool rejected = false;
        try { store.ResumePoint(generation, expectedSourceIdentity: new byte[24]); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "A confirmed point bypassed the job's required source identity.");
        Console.WriteLine("PASS a new worker recovers the same point after real process termination with the source absent.");
    }

    private static void RunChild(string root, Guid id, int expectedExit)
    {
        using var child = StartChild(root, id, interrupt: false);
        if (!child.WaitForExit(30_000))
        {
            child.Kill();
            child.WaitForExit();
            throw new TimeoutException("The job worker did not finish.");
        }
        Check(child.ExitCode == expectedExit, "The job worker failed. " + child.StandardError.ReadToEnd());
    }

    private static Process StartChild(string root, Guid id, bool interrupt)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in new[] { interrupt ? "--interrupt-version-job" : "--run-version-job", root, id.ToString("N") })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start job worker.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
