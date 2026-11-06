using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CopyLab;

internal static class InstallationProbe
{
    internal static void Run()
    {
        foreach (var boundary in new[] { (InstallStep.FilePartial, "CopyLab.dll"), (InstallStep.FileVerified, "CopyLab.dll"),
            (InstallStep.RecordPartial, "SHA256SUMS.txt"), (InstallStep.RecordPartial, ".foldertwin-install-v1"),
            (InstallStep.Confirmed, ".foldertwin-install-v1") })
        {
            using var fixture = new Fixture();
            bool reached = false;
            Reject(() => LabInstallation.EnsureAt(fixture.Package, fixture.Installs, (step, path) =>
            {
                if ((step, path) != boundary) return;
                reached = true;
                throw new IOException("Synthetic interrupted installation.");
            }));
            Check(reached, "The requested installation boundary did not occur.");
            string installed = LabInstallation.EnsureAt(fixture.Package, fixture.Installs);
            Check(LabInstallation.EnsureAt(fixture.Package, fixture.Installs) == installed &&
                LabInstallation.EnsureAt(Path.GetDirectoryName(installed)!, fixture.Installs) == installed,
                "Installation did not resume, reuse or verify from its own immutable package.");
            fixture.Verify(installed);
        }
        using (var fixture = new Fixture())
        {
            string first = LabInstallation.EnsureAt(fixture.Package, fixture.Installs);
            string binary = Path.Combine(Path.GetDirectoryName(first)!, "CopyLab.dll");
            byte[] original = File.ReadAllBytes(binary);
            byte[] corrupt = (byte[])original.Clone(); corrupt[0] ^= 1;
            File.WriteAllBytes(binary, corrupt);
            Reject(() => LabInstallation.EnsureAt(fixture.Package, fixture.Installs));
            Check(File.ReadAllBytes(binary).AsSpan().SequenceEqual(corrupt), "Installation replaced contradictory data.");
            File.WriteAllBytes(binary, original);
            string extra = Path.Combine(Path.GetDirectoryName(first)!, "unknown.txt");
            File.WriteAllText(extra, "must be preserved");
            Reject(() => LabInstallation.EnsureAt(fixture.Package, fixture.Installs));
            File.Delete(extra);
            File.WriteAllText(Path.Combine(fixture.Package, "docs", "Guía ñ.txt"), "new distribution");
            fixture.WriteManifest();
            string second = LabInstallation.EnsureAt(fixture.Package, fixture.Installs);
            Check(first != second && File.ReadAllBytes(binary).AsSpan().SequenceEqual(original),
                "Updating a package rewrote an earlier version.");
            fixture.Verify(second);
            byte[] packageBinary = File.ReadAllBytes(Path.Combine(fixture.Package, "CopyLab.dll"));
            File.WriteAllBytes(Path.Combine(fixture.Package, "CopyLab.dll"), [1]);
            Reject(() => LabInstallation.EnsureAt(fixture.Package, fixture.Installs));
            File.WriteAllBytes(Path.Combine(fixture.Package, "CopyLab.dll"), packageBinary);
            Check(File.ReadAllBytes(binary).AsSpan().SequenceEqual(original), "A corrupt update changed the earlier installation.");
        }
        Console.Error.WriteLine("PASS installation resumes exact file and metadata prefixes, reuses its verified package, rejects corruption and foreign files, and updates beside the prior version without overwriting it.");
        CheckRemoval();
    }

    private static void CheckRemoval()
    {
        foreach (var boundary in Enum.GetValues<InstallRemovalStep>())
        {
            using var fixture = new Fixture();
            string executable = LabInstallation.EnsureAt(fixture.Package, fixture.Installs);
            string fingerprint = Path.GetFileName(Path.GetDirectoryName(executable)!);
            var review = LabInstallation.ReviewRemovalAt(fixture.Installs, fingerprint);
            string preserved = Path.Combine(fixture.Workspace.Root, "preserved-backup.txt");
            File.WriteAllText(preserved, "must remain");
            bool reached = false;
            Reject(() => LabInstallation.RemoveAt(fixture.Installs, review, step =>
            {
                if (step != boundary) return;
                reached = true;
                throw new IOException("Synthetic interrupted package removal.");
            }));
            Check(reached, "The requested removal boundary was not reached: " + boundary);
            var reopened = LabInstallation.ReviewRemovalAt(fixture.Installs, fingerprint);
            LabInstallation.RemoveAt(fixture.Installs, reopened);
            LabInstallation.RemoveAt(fixture.Installs, reopened);
            Check(!Directory.Exists(Path.GetDirectoryName(executable)) && File.ReadAllText(preserved) == "must remain",
                "Package removal did not resume or touched data outside its installation.");
            string reinstalled = LabInstallation.EnsureAt(fixture.Package, fixture.Installs);
            Check(reinstalled == executable && !LabInstallation.ReviewRemovalAt(fixture.Installs, fingerprint).Identity.AsSpan().SequenceEqual(review.Identity),
                "A completed removal could not be reinstalled with a new directory identity.");
            fixture.Verify(reinstalled);
            Reject(() => LabInstallation.RemoveAt(fixture.Installs, review));
            fixture.Verify(reinstalled);
        }
        using (var fixture = new Fixture())
        {
            string executable = LabInstallation.EnsureAt(fixture.Package, fixture.Installs);
            string root = Path.GetDirectoryName(executable)!;
            var review = LabInstallation.ReviewRemovalAt(fixture.Installs, Path.GetFileName(root));
            using (var busy = new FileStream(Path.Combine(root, "CopyLab.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Reject(() => LabInstallation.RemoveAt(fixture.Installs, review));
            fixture.Verify(executable);
            string foreign = Path.Combine(root, "foreign.txt");
            File.WriteAllText(foreign, "preserve foreign data");
            Reject(() => LabInstallation.RemoveAt(fixture.Installs, review));
            Check(File.ReadAllText(foreign) == "preserve foreign data", "Removal deleted foreign data.");
            fixture.Verify(executable);
            File.Delete(foreign);
            File.WriteAllText(Path.Combine(root, "docs", "Guía ñ.txt"), "changed installed guide");
            Reject(() => LabInstallation.RemoveAt(fixture.Installs, review));
            Check(File.Exists(executable), "A contradictory installation was partly deleted.");
        }
        CheckLateForeignFiles();
        Console.Error.WriteLine("PASS package removal checks all handles and hashes before deleting, rejects busy/foreign/changed files, resumes every journal boundary, preserves other data, and permits reinstall without reusing old removal authority.");
    }

    private static void CheckLateForeignFiles()
    {
        foreach (string relative in new[] { "late-foreign.txt", Path.Combine("docs", "late-foreign.txt") })
        {
            using var fixture = new Fixture();
            string executable = LabInstallation.EnsureAt(fixture.Package, fixture.Installs);
            string root = Path.GetDirectoryName(executable)!;
            string fingerprint = Path.GetFileName(root);
            var review = LabInstallation.ReviewRemovalAt(fixture.Installs, fingerprint);
            string foreign = Path.Combine(root, relative);
            string preserved = Path.Combine(fixture.Workspace.Root, "independent-synthetic-backup.txt");
            byte[] sentinel = "late synthetic foreign file must survive"u8.ToArray();
            File.WriteAllText(preserved, "independent fixture data");
            bool inserted = false;
            int removed = 0;
            Reject(() => LabInstallation.RemoveAt(fixture.Installs, review, step =>
            {
                if (step == InstallRemovalStep.Planned)
                {
                    // Visit has checked and retained all known file handles; no delete has run yet.
                    File.WriteAllBytes(foreign, sentinel);
                    inserted = true;
                }
                if (step == InstallRemovalStep.FileRemoved) removed++;
            }));
            Check(inserted && removed > 0 && Directory.Exists(root) && File.ReadAllBytes(foreign).AsSpan().SequenceEqual(sentinel),
                "A foreign file inserted after inventory was deleted or did not exercise an interrupted removal.");
            string journal = Path.Combine(fixture.Installs, ".removals", fingerprint);
            Check(File.Exists(Path.Combine(journal, "plan")) && !File.Exists(Path.Combine(journal, "complete")),
                "The incomplete removal lost its durable authority or published a false completion.");
            var choice = LabInstallation.ListRemovalsAt(fixture.Installs).Single();
            Check(choice.InProgress && choice.Review.Identity.AsSpan().SequenceEqual(review.Identity),
                "A late foreign file hid the resumable removal from the application.");
            string[] snapshot = Snapshot(root);
            var reopened = LabInstallation.ReviewRemovalAt(fixture.Installs, fingerprint);
            Reject(() => LabInstallation.RemoveAt(fixture.Installs, reopened));
            Check(Snapshot(root).SequenceEqual(snapshot, StringComparer.Ordinal) && File.ReadAllText(preserved) == "independent fixture data",
                "Retrying while a foreign file remained changed the partial installation or independent data.");
            File.Delete(foreign); // Only this probe's explicitly inserted synthetic file.
            LabInstallation.RemoveAt(fixture.Installs, reopened);
            Check(!Directory.Exists(root) && File.Exists(Path.Combine(journal, "complete")) &&
                File.ReadAllText(preserved) == "independent fixture data",
                "Removal could not resume after its own late foreign fixture file was withdrawn.");
        }
        Console.Error.WriteLine("PASS late foreign files inserted in the root and nested directory after removal inventory survive; incomplete authority stays visible and resumable.");

        static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))
            .Concat(Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(root, path) + "/"))
            .Order(StringComparer.Ordinal).ToArray();
    }

    internal sealed class Fixture : IDisposable
    {
        internal LabWorkspace Workspace { get; } = LabWorkspace.Create();
        internal string Package { get; }
        internal string Installs { get; }
        internal Fixture()
        {
            Package = Directory.CreateDirectory(Path.Combine(Workspace.Root, "package")).FullName;
            Installs = Directory.CreateDirectory(Path.Combine(Workspace.Root, "installs")).FullName;
            Directory.CreateDirectory(Path.Combine(Package, "docs"));
            foreach (string file in new[] { "CopyLab.exe", "CopyProbe.dll", "coreclr.dll", "PresentationFramework.dll" })
                File.WriteAllText(Path.Combine(Package, file), "Synthetic bytes for file-copy testing only: " + file);
            File.WriteAllBytes(Path.Combine(Package, "CopyLab.dll"), Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray());
            File.WriteAllText(Path.Combine(Package, "docs", "Guía ñ.txt"), "synthetic guide");
            WriteManifest();
        }
        internal void WriteManifest()
        {
            string[] names = Directory.GetFiles(Package, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) != "SHA256SUMS.txt").Select(path => Path.GetRelativePath(Package, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal).ToArray();
            File.WriteAllText(Path.Combine(Package, "SHA256SUMS.txt"), string.Concat(names.Select(name =>
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Package, name)))) + "  " + name + "\n")), new UTF8Encoding(false));
        }
        internal void Verify(string installed)
        {
            string root = Path.GetDirectoryName(installed)!;
            foreach (string file in Directory.GetFiles(Package, "*", SearchOption.AllDirectories))
                Check(File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, Path.GetRelativePath(Package, file)))),
                    "An installed file differs from the synthetic package.");
        }
        public void Dispose()
        {
            using (Workspace.OpenRegisteredRoot()) { }
            Directory.Delete(Workspace.Root, recursive: true);
        }
    }
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidOperationException("The contradictory or interrupted installation did not stop.");
    }
}
