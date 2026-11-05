using System.IO;

namespace CopyLab;

internal static class ScheduleProbe
{
    internal static async Task RunAsync()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("The schedule probe requires a disposable hosted Windows runner.");
        var workspace = LabWorkspace.CreatePersistent();
        Guid id = Guid.Empty;
        string? executable = null;
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "origen horario ñ")).FullName;
            string destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "almacén horario")).FullName;
            File.WriteAllText(Path.Combine(source, "dato.txt"), "contenido del horario");
            var store = VersionStore.Initialize(workspace, destination);
            id = VersionJob.Create(workspace, source, destination);
            executable = LabInstallation.EnsureWorker();
            if (LabInstallation.EnsureWorker() != executable || !LabInstallation.IsInstalledWorker(executable))
                throw new InvalidOperationException("The stable installation could not be verified and reused.");
            using (var scheduler = new WindowsSchedule())
            {
                scheduler.Register(workspace, id, executable, DateTime.Now.AddSeconds(5), daily: false);
                var schedule = scheduler.Read(workspace, id);
                if (schedule is not { Enabled: true, MeetsPolicy: true } || schedule.Executable != executable)
                    throw new InvalidOperationException("The registered schedule did not preserve the execution policy.");
            }
            var result = await WaitForAttemptAsync(afterSequence: 0);
            if (result is not { State: VersionJobState.Confirmed } ||
                store.ListPoints().Single().Id != result.Generation || store.Verify(result.Generation))
                throw new InvalidOperationException("The Windows time trigger did not produce a verified recovery point.");
            using (var scheduler = new WindowsSchedule())
            {
                scheduler.Register(workspace, id, executable, DateTime.Today.AddDays(1).AddHours(20));
                if (scheduler.Read(workspace, id) is not { Enabled: true, MeetsPolicy: true })
                    throw new InvalidOperationException("The daily schedule update did not verify.");
                string detached = Path.Combine(workspace.Root, "destino apartado");
                Directory.Move(destination, detached);
                try
                {
                    scheduler.RunNow(workspace, id);
                    var missing = await WaitForAttemptAsync(result.Sequence);
                    if (missing.State != VersionJobState.WaitingForDestination)
                        throw new InvalidOperationException("The scheduled worker did not report its absent destination.");
                    result = missing;
                }
                finally { Directory.Move(detached, destination); }

                using (var target = new DirectoryFence(destination))
                using (WindowsProbeNative.OpenPlainFile(target.Root, FolderBatch.LockName, FileAccess.ReadWrite, FileShare.None))
                {
                    scheduler.RunNow(workspace, id);
                    var busy = await WaitForAttemptAsync(result.Sequence);
                    if (busy.State != VersionJobState.Busy)
                        throw new InvalidOperationException("The scheduled worker bypassed the store lock.");
                    result = busy;
                }
                scheduler.RunNow(workspace, id);
                var recovered = await WaitForAttemptAsync(result.Sequence);
                var history = VersionJob.History(workspace, id).Results;
                if (recovered.State != VersionJobState.Confirmed || store.Verify(recovered.Generation!) ||
                    !history.Take(3).Select(item => item.State).SequenceEqual(
                        [VersionJobState.Confirmed, VersionJobState.Busy, VersionJobState.WaitingForDestination]))
                    throw new InvalidOperationException("The scheduled worker did not recover or preserve the prior incidents.");
                scheduler.Remove(workspace, id);
                if (scheduler.Read(workspace, id) is not null)
                    throw new InvalidOperationException("The owned schedule was not removed.");
                scheduler.SetTrayStartup(workspace, executable, enabled: true);
                if (scheduler.ReadTray(workspace) is not { Enabled: true, MeetsPolicy: true })
                    throw new InvalidOperationException("The optional logon tray definition did not verify.");
                scheduler.SetTrayStartup(workspace, executable: null, enabled: false);
                if (scheduler.ReadTray(workspace) is not null)
                    throw new InvalidOperationException("The optional logon tray definition was not removed.");
            }
            Console.Error.WriteLine("PASS the portable app installs a stable worker; an actual Windows time trigger confirms a point, updates to daily and removes its owned schedule.");
            Console.Error.WriteLine("PASS the scheduled action reports missing destination and manual-store contention, then recovers and preserves both incidents.");

            async Task<VersionJobResult> WaitForAttemptAsync(long afterSequence)
            {
                using var scheduler = new WindowsSchedule();
                var deadline = DateTime.UtcNow.AddMinutes(2);
                while (DateTime.UtcNow < deadline)
                {
                    var latest = VersionJob.History(workspace, id).Results.FirstOrDefault();
                    if (latest is not null && latest.Sequence > afterSequence && scheduler.Read(workspace, id) is { Running: false })
                        return latest;
                    await Task.Delay(250);
                }
                throw new InvalidOperationException("The Windows task did not finish its expected attempt.");
            }
        }
        finally
        {
            if (id != Guid.Empty)
            {
                using var scheduler = new WindowsSchedule();
                scheduler.Remove(workspace, id);
                scheduler.SetTrayStartup(workspace, executable: null, enabled: false);
            }
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
            if (executable is not null && LabInstallation.IsInstalledWorker(executable))
            {
                string directory = Path.GetDirectoryName(executable)!;
                // This probe runs only on a disposable runner and created/reused this exact verified installation.
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
