using System.Diagnostics;
using System.IO;
using System.Windows;

namespace CopyLab;

internal static class TrayProbe
{
    internal static async Task RunAsync()
    {
        var workspace = LabWorkspace.Create();
        try
        {
            var jobs = new List<Guid>();
            foreach (string name in new[] { "primero", "segundo" })
            {
                string source = Directory.CreateDirectory(Path.Combine(workspace.Root, name + " origen")).FullName;
                string destination = Directory.CreateDirectory(Path.Combine(workspace.Root, name + " versiones")).FullName;
                File.WriteAllText(Path.Combine(source, "dato.txt"), name);
                _ = VersionStore.Initialize(workspace, destination);
                Guid id = VersionJob.Create(workspace, source, destination);
                jobs.Add(id);
                if (VersionJob.Run(workspace, id).State != VersionJobState.Confirmed)
                    throw new InvalidOperationException("The tray fixture could not confirm its initial point.");
            }
            using var tray = new LabTray(workspace, () => { }, () => { }, notifications: false);
            var timer = Stopwatch.StartNew();
            await tray.RefreshAsync();
            timer.Stop();
            if (!tray.CanRun || tray.NotificationCount != 0 ||
                !tray.StatusText.Contains("2 trabajos", StringComparison.Ordinal) ||
                !tray.AgeText.Contains("más antiguo", StringComparison.Ordinal))
                throw new InvalidOperationException("The tray did not show both confirmed jobs and their age.");
            Console.Error.WriteLine(FormattableString.Invariant(
                $"Tray query for two jobs; seconds={timer.Elapsed.TotalSeconds:F3}."));
            string sourceMissing = Path.Combine(workspace.Root, "primero origen");
            Directory.Move(sourceMissing, Path.Combine(workspace.Root, "primero apartado"));
            if (VersionJob.Run(workspace, jobs[0]).State != VersionJobState.WaitingForSource)
                throw new InvalidOperationException("The tray fixture did not leave the first job pending.");
            await tray.RefreshAsync();
            await tray.RefreshAsync();
            if (!tray.StatusText.Contains("requieren atención", StringComparison.Ordinal) || tray.NotificationCount != 1)
                throw new InvalidOperationException("The healthy job hid a failure or repeated queries duplicated its notice.");
            _ = VersionJob.Run(workspace, jobs[0]);
            await tray.RefreshAsync();
            if (tray.NotificationCount != 1)
                throw new InvalidOperationException("The same failed retry repeated its notice.");
            Directory.Move(Path.Combine(workspace.Root, "primero apartado"), sourceMissing);
            if (VersionJob.Run(workspace, jobs[0]).State != VersionJobState.Confirmed)
                throw new InvalidOperationException("The tray fixture could not recover its first job.");
            await tray.RefreshAsync();
            int[] before = jobs.Select(id => VersionJob.History(workspace, id).Results.Length).ToArray();
            tray.RunNow();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline &&
                jobs.Where((id, index) => VersionJob.History(workspace, id).Results.Length <= before[index]).Any())
                await Task.Delay(100);
            if (jobs.Any(id => VersionJob.History(workspace, id).Results.First().State != VersionJobState.Confirmed) ||
                jobs.Where((id, index) => VersionJob.History(workspace, id).Results.Length <= before[index]).Any())
                throw new InvalidOperationException("The tray did not start independent workers for both jobs.");
            string generation = VersionJob.History(workspace, jobs[1]).Results.First().Generation!;
            File.WriteAllText(Path.Combine(workspace.Root, "segundo versiones", "manifests", generation + ".jsonl"), "truncado");
            await tray.RefreshAsync();
            if (tray.NotificationCount != 2 || !tray.StatusText.Contains("requieren atención", StringComparison.Ordinal))
                throw new InvalidOperationException("The tray hid the loss of a catalog replica.");
            Console.Error.WriteLine("PASS tray status covers both jobs; failures stay visible, repeated retries do not repeat notices and Run now starts both workers.");
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }
    }
}
