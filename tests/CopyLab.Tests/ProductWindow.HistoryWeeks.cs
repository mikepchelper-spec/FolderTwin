using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace FolderTwin;

public partial class ProductWindow
{
    internal static async Task RunHistoryWeeksAsync()
    {
        const int attempts = 3_024; // 21 days of attempts at ten-minute intervals per job.
        var fixture = LabWorkspace.Create();
        ProductWindow? window = null;
        try
        {
            using (var root = fixture.OpenRegisteredRoot()) LabWorkspace.RequireSpace(2L * 1024 * 1024 * 1024, LabWorkspace.AvailableSpace(root.Root));
            string settings = Folder("settings"), destination = Folder("backups");
            ProductJobRegistry Open() => new(settings);
            using var registry = Open();
            var jobs = Enum.GetValues<ProductBackupMode>().Select(mode =>
            {
                string source = Folder(mode.ToString());
                File.WriteAllText(Path.Combine(source, "initial.txt"), "synthetic backup for measuring history");
                var job = registry.Create(new($"Synthetic {mode}", mode, null), LocalJobRoots.ReviewDedicatedDestination(source, destination));
                registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching,
                    authorizeMirror: mode == ProductBackupMode.ProtectedMirror);
                var initial = ProductRunner.Run(registry, job.Id, automatic: true, clock: () => DateTimeOffset.UtcNow.AddDays(-21));
                Check(initial.State == ProductRunState.Confirmed && initial.Generation is not null, $"Could not create the initial {mode} point.");
                var verified = ProductRunner.Verify(registry, job.Id, initial.Generation!);
                Check(verified.State == ProductRunState.Confirmed, $"Could not verify the initial {mode} point.");
                return (Job: job, Backup: initial.FinishedUtc, Verify: verified.FinishedUtc,
                    Template: registry.RunHistory(job.Id).Runs.Single(run => run.Intent.Sequence == 1).Intent);
            }).ToArray();
            window = new ProductWindow(Open, testSchedule: (_, _, _) => { });
            window.Show();
            await window.InitializeAsync();
            window.runtimeTimer.Stop();
            await Measure("short");

            var seed = Stopwatch.StartNew();
            foreach (var (job, _, _, template) in jobs)
            {
                string runs = Path.Combine(settings, job.Id.ToString("N"), "runs");
                for (int sequence = 4; sequence <= attempts; sequence++)
                {
                    if (seed.Elapsed > TimeSpan.FromMinutes(3)) throw new TimeoutException("History setup exceeded three minutes.");
                    var intent = template with { Id = Guid.NewGuid(), Sequence = sequence, StartedUtc = template.StartedUtc.AddMinutes(10 * sequence) };
                    byte[] encodedIntent = ProductHistoryAuditProbe.Encode(intent);
                    var result = new ProductRunResult(1, SHA256.HashData(encodedIntent),
                        sequence == attempts ? ProductRunState.Review : ProductRunState.Unchanged, intent.StartedUtc,
                        null, sequence == attempts ? "synthetic-incident.txt" : null, null);
                    string folder = Directory.CreateDirectory(Path.Combine(runs,
                        sequence.ToString("D20", CultureInfo.InvariantCulture) + "-" + intent.Id.ToString("N"))).FullName;
                    File.WriteAllBytes(Path.Combine(folder, "intent"), encodedIntent);
                    File.WriteAllBytes(Path.Combine(folder, "result"), ProductHistoryAuditProbe.Encode(result));
                }
            }
            await Measure("three-weeks");
            foreach (var (job, backup, verify, _) in jobs)
            {
                window.SelectJob(job.Id);
                Check(window.RouteBackupDate.Text == RouteDate(backup) && window.RouteVerifyDate.Text == RouteDate(verify),
                    "Ruta lost the latest complete backup or integral verification behind the simulated attempts.");
                Check(window.RunHistoryList.Items.OfType<HistoryRow>().Any(row => row.Label.Contains("synthetic-incident.txt", StringComparison.Ordinal)),
                    "Ruta lost the recent incident after reserving a new attempt.");
                var history = registry.RunHistory(job.Id);
                Check(history.Unreadable == 0 && history.Runs.Any(run => run.Result?.RelativePath == "synthetic-incident.txt") &&
                    history.Runs.Any(run => run.Intent.Sequence == 1), "The simulated history lost an incident or its oldest run.");
            }
            Console.Error.WriteLine("PASS Ruta, tray and automatic reservation with 3,024 attempts per job, three modes, and preserved incidents and dates.");

            string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;

            async Task Measure(string phase)
            {
                var route = new List<double>();
                var tray = new List<double>();
                var reservation = new List<double>();
                for (int sample = 0; sample < 6; sample++)
                {
                    var timer = Stopwatch.StartNew();
                    await window.RefreshRuntimeAsync();
                    if (sample != 0) route.Add(timer.Elapsed.TotalMilliseconds);
                    timer.Restart();
                    var snapshot = await Task.Run(() => TraySnapshot.ReadProduct(registry, window.displayHistory));
                    Check(snapshot.Jobs.Length == jobs.Length && snapshot.Age != "Sin respaldo completo consultable.", "The tray lost an available job or the complete backup age.");
                    if (phase == "three-weeks") Check(jobs.All(job => snapshot.Attention.Contains($"{job.Job.Id:N}-Review")),
                        "The tray lost an incident in the simulated history.");
                    if (sample != 0) tray.Add(timer.Elapsed.TotalMilliseconds);
                }
                foreach (var (job, _, _, _) in jobs)
                {
                    var timer = Stopwatch.StartNew();
                    using var run = registry.StartRun(job.Id, automatic: true);
                    reservation.Add(timer.Elapsed.TotalMilliseconds);
                    Check(run.Intent.Sequence > (phase == "short" ? 2 : attempts), "The next reservation reused an attempt sequence.");
                    run.Finish(ProductRunState.Unchanged);
                }
                Console.Error.WriteLine(FormattableString.Invariant($"history {phase}: route_ms={route.Order().ElementAt(2):F3}, tray_ms={tray.Order().ElementAt(2):F3}, reservation_ms={reservation.Order().ElementAt(1):F3}"));
            }
        }
        finally
        {
            window?.Close();
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }
    }
}
