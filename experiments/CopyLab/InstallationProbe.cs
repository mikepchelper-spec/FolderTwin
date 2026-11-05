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
    }

    private sealed class Fixture : IDisposable
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
