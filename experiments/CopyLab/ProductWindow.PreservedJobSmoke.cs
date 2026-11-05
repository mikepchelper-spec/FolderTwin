using System.IO;
using System.Security.Cryptography;

namespace CopyLab;

public partial class ProductWindow
{
    // Resolve an incompatible intent through existing retirement and creation,
    // preserving every old byte instead of discarding a partially applied journal.
    internal static async Task RunPreservedJobSmokeAsync()
    {
        foreach (var boundary in Enum.GetValues<PendingStep>())
            await CheckPreservedJobAsync(ProductBackupMode.Versions, boundary, null);
        foreach (var boundary in new[] { PendingStep.MetadataFlushed, PendingStep.LocationFlushed })
            await CheckPreservedJobAsync(ProductBackupMode.Versions, boundary, null, truncateDraft: true);
        foreach (var mode in new[] { ProductBackupMode.Accumulative, ProductBackupMode.ProtectedMirror })
        foreach (var boundary in new[] { MirrorStep.Planned, MirrorStep.Ready, MirrorStep.Promoted })
            await CheckPreservedJobAsync(mode, null, boundary);
        Console.Error.WriteLine("PASS WPF retires incompatible version drafts and mirror/cumulative journals before and after current-folder mutation, preserves every old backup byte, creates a separate fresh job and restores prior confirmed data without the source.");
    }

    private static async Task CheckPreservedJobAsync(ProductBackupMode mode, PendingStep? draft, MirrorStep? mirror,
        bool truncateDraft = false)
    {
        var fixture = LabWorkspace.Create();
        ProductWindow? window = null;
        try
        {
            string Folder(string name) => Directory.CreateDirectory(Path.Combine(fixture.Root, name)).FullName;
            string settings = Folder("settings"), source = Folder("source"), parent = Folder("backups"), recovery = Folder("recovery");
            string data = Path.Combine(source, "datos.txt");
            File.WriteAllText(data, "previous confirmed data");
            ProductJobRegistry Open() => new(settings);
            ProductJob old;
            ProductRunResult baseline;
            using (var registry = Open())
            {
                old = registry.Create(new("Plan anterior", mode, null), LocalJobRoots.ReviewDedicatedDestination(source, parent));
                baseline = ProductRunner.Run(registry, old.Id);
            }
            Check(baseline.State == ProductRunState.Confirmed, "The preserved-job fixture lacked a confirmed baseline.");
            var store = VersionStore.OpenProduct(old);
            File.WriteAllText(data, "pending inventory");
            bool reached = false;
            string generation = VersionStore.NewGenerationId();
            using (var registry = Open())
            using (var attempt = registry.StartRun(old.Id))
            {
                attempt.SetGeneration(generation);
                try
                {
                    if (draft is { } draftBoundary)
                        store.CreatePoint(old.Source.VolumePath, generation, pendingCheckpoint: step =>
                        {
                            if (step == draftBoundary) { reached = true; throw new IOException("Synthetic version draft interruption."); }
                        });
                    else
                        store.BeginMirror(store.PreviewMirror(old.Source.VolumePath, generation: generation), massReviewed: true, checkpoint: step =>
                        {
                            if (step == mirror) { reached = true; throw new IOException("Synthetic current-folder journal interruption."); }
                        });
                }
                catch (IOException) when (reached) { }
                attempt.Finish(ProductRunState.Review, detail: "Synthetic interrupted backup; preserve its original intent.");
            }
            Check(reached, "The requested preserved-job interruption was not reached.");
            if (truncateDraft)
            {
                string suffix = draft == PendingStep.MetadataFlushed ? ".jsonl.tmp" : ".location.json.tmp";
                using var partial = new FileStream(Path.Combine(old.Destination.LastVolumePath, "pending", generation + suffix),
                    FileMode.Open, FileAccess.Write, FileShare.None);
                partial.SetLength(partial.Length / 2);
                partial.Flush(flushToDisk: true);
            }
            File.WriteAllText(data, "latest source for a separate job");
            var original = Snapshot(old.Destination.LastVolumePath);
            var picks = new Queue<string>();
            window = new ProductWindow(Open, _ => picks.Dequeue(), testSchedule: (_, _, _) => { });
            await window.InitializeAsync();
            window.SelectJob(old.Id);
            window.EditRetired.IsChecked = true;
            await Click(window, window.SaveSettingsButton);
            using (var registry = Open())
                Check(registry.Get(old.Id) is { Retired: true, Enabled: false } &&
                    registry.RunHistory(old.Id).Runs[0].Result?.State == ProductRunState.Review,
                    "Retirement discarded the incident or left the old job enabled.");
            Check(!window.BackupJobButton.IsEnabled, "WPF allowed a new backup on the preserved retired job.");
            window.Close();
            window = new ProductWindow(Open, _ => picks.Dequeue(), testSchedule: (_, _, _) => { });
            await window.InitializeAsync();
            window.SelectJob(old.Id);
            Check(!window.BackupJobButton.IsEnabled, "Reopening re-enabled the preserved retired job.");
            await Click(window, window.NewJobButton);
            picks.Enqueue(source);
            await Click(window, window.ChooseOriginButton);
            await Click(window, window.NextStepButton);
            picks.Enqueue(parent);
            await Click(window, window.ChooseParentButton);
            await Click(window, window.NextStepButton);
            window.JobName.Text = "Trabajo nuevo";
            window.BackupMode.SelectedIndex = (int)mode;
            await Click(window, window.NextStepButton);
            await Click(window, window.SaveJobButton);
            ProductJob fresh;
            using (var registry = Open()) fresh = registry.List().Jobs.Single(job => job.Id != old.Id);
            Check(fresh.Destination.Marker != old.Destination.Marker &&
                !fresh.Destination.Identity.AsSpan().SequenceEqual(old.Destination.Identity),
                "The replacement job adopted the pending destination.");
            await Click(window, window.BackupJobButton);
            if (mode == ProductBackupMode.ProtectedMirror) await Click(window, window.ApplyMirrorButton);
            await Click(window, window.RecoverJobButton);
            await Click(window, window.VerifyPointButton);
            Check(window.StatusText.Text.StartsWith("Verificación completa", StringComparison.Ordinal), window.StatusText.Text);
            await Click(window, window.ReturnToJobsButton);
            Check(original.SequenceEqual(Snapshot(old.Destination.LastVolumePath)), "Creating the fresh job changed old backup bytes.");
            Directory.Move(source, Path.Combine(fixture.Root, "source absent"));
            window.SelectJob(old.Id);
            await Click(window, window.RecoverJobButton);
            var date = window.RecoveryDates.Items.OfType<RecoveryDate>().Single(item => item.Point.Id == baseline.Generation);
            window.RecoveryDates.SelectedItem = date;
            await window.activeTask;
            picks.Enqueue(recovery);
            await Click(window, window.ReviewRestoreButton);
            await Click(window, window.RestorePointButton);
            string restored = Path.Combine(Directory.EnumerateDirectories(recovery).Single(), "datos.txt");
            Check(File.ReadAllText(restored) == "previous confirmed data" && original.SequenceEqual(Snapshot(old.Destination.LastVolumePath)),
                "Recovery from the retired job needed the source or changed its pending journal.");
        }
        finally
        {
            if (window is not null) { await window.WaitForWorkersAsync(); window.Close(); }
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }

        static string[] Snapshot(string path) => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(path, file) + " " + Hash(file)).ToArray();
        static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    }
}
