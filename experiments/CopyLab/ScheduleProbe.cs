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
                VersionJobResult retryAfter;
                try
                {
                    scheduler.Register(workspace, id, executable, DateTime.Now.AddSeconds(5), daily: false);
                    retryAfter = await WaitForAttemptAsync(result.Sequence, waitForExit: false);
                    if (retryAfter.State != VersionJobState.WaitingForDestination || scheduler.Read(workspace, id) is not { Running: true })
                        throw new InvalidOperationException("The retry fixture did not fail on its time trigger.");
                    scheduler.RunNow(workspace, id);
                    await Task.Delay(1000);
                    if (VersionJob.History(workspace, id).Results.First().Sequence != retryAfter.Sequence)
                        throw new InvalidOperationException("The schedule started a second worker during a deferred retry.");
                }
                finally { Directory.Move(detached, destination); }
                var retried = await WaitForAttemptAsync(retryAfter.Sequence, TimeSpan.FromMinutes(7));
                if (retried.State != VersionJobState.Confirmed || store.Verify(retried.Generation!) ||
                    VersionJob.History(workspace, id).Results.Skip(1).First() != retryAfter ||
                    retried.FinishedUtc - retryAfter.FinishedUtc < TimeSpan.FromMinutes(4.5))
                    throw new InvalidOperationException("The scheduled worker did not run its deferred retry as configured.");
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"PASS a failed time trigger retried without intervention after {(retried.FinishedUtc - retryAfter.FinishedUtc).TotalSeconds:F1} seconds."));

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
            Console.Error.WriteLine("PASS the scheduled action preserves the missing-destination incident and does not start a second instance during retry.");

            async Task<VersionJobResult> WaitForAttemptAsync(long afterSequence, TimeSpan? limit = null, bool waitForExit = true)
            {
                using var scheduler = new WindowsSchedule();
                var deadline = DateTime.UtcNow + (limit ?? TimeSpan.FromMinutes(2));
                while (DateTime.UtcNow < deadline)
                {
                    var latest = VersionJob.History(workspace, id).Results.FirstOrDefault();
                    if (latest is not null && latest.Sequence > afterSequence &&
                        (!waitForExit || scheduler.Read(workspace, id) is { Running: false }))
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
