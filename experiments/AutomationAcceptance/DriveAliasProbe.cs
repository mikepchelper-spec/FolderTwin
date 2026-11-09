using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

internal static class DriveAliasProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, StringBuilder target, int capacity);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DefineDosDevice(uint flags, string name, string target);

    private const uint RawLocal = 0x1 | 0x8;
    private const uint RemoveExact = RawLocal | 0x2 | 0x4;

    internal static void Run()
    {
        var sourceWorkspace = LabWorkspace.Create();
        var destinationWorkspace = LabWorkspace.CreateOnFixedVolume(@"D:\");
        string[] letters = Enumerable.Range('R', 9).Reverse().Select(letter => $"{(char)letter}:")
            .Where(letter => Lookup(letter) is null).Take(2).ToArray();
        var aliases = new List<(string Letter, string Target)>();
        try
        {
            if (letters.Length != 2) throw new InvalidOperationException("The alias test needs two unused drive letters.");
            string source = Directory.CreateDirectory(Path.Combine(sourceWorkspace.Root, "source")).FullName;
            string settings = Directory.CreateDirectory(Path.Combine(sourceWorkspace.Root, "settings")).FullName;
            string wrong = Directory.CreateDirectory(Path.Combine(sourceWorkspace.Root, "wrong")).FullName;
            string parent = Directory.CreateDirectory(Path.Combine(destinationWorkspace.Root, "selected")).FullName;
            string file = Path.Combine(source, "synthetic.txt");
            File.WriteAllText(file, "original sample");
            using var registry = new ProductJobRegistry(settings);
            Mount(letters[0], destinationWorkspace.Root);
            var jobs = Enum.GetValues<ProductBackupMode>().Select(mode =>
            {
                string selected = Path.Combine(letters[0] + @"\", "selected");
                var job = registry.Create(new($"Synthetic alias {mode}", mode, null), LocalJobRoots.ReviewDedicatedDestination(source, selected));
                Check(Path.GetPathRoot(job.Source.VolumePath) != Path.GetPathRoot(job.Destination.LastVolumePath) &&
                    job.Destination.LastVolumePath.StartsWith(@"\\?\Volume{", StringComparison.Ordinal), "The saved binding did not resolve the real second NTFS volume.");
                registry.UpdateSettings(job.Id, job.Options.Name, null, true, false, ProductWatchMode.Watching,
                    authorizeMirror: mode == ProductBackupMode.ProtectedMirror);
                Check(ProductRunner.Run(registry, job.Id, automatic: true).State == ProductRunState.Confirmed,
                    $"Initial copy failed for {mode}.");
                return job;
            }).ToArray();

            Unmount(aliases[0]);
            aliases.Clear();
            Mount(letters[1], destinationWorkspace.Root);
            Mount(letters[0], wrong);
            File.WriteAllText(file, "edited after alias change");
            var generations = new Dictionary<Guid, string>();
            foreach (var job in jobs)
            {
                string name = Path.GetFileName(job.Destination.LastVolumePath);
                string root = Path.Combine(parent, name);
                string detached = root + "-detached";
                string newPath = Path.Combine(letters[1] + @"\", "selected", name);
                var actual = LocalJobRoots.InspectSelected(newPath, writable: false);
                Check(actual.Identity.AsSpan().SequenceEqual(job.Destination.Identity), "The new letter did not address the registered directory.");
                string impostor = Directory.CreateDirectory(Path.Combine(wrong, "selected", name)).FullName;
                byte[] marker = File.ReadAllBytes(Path.Combine(root, DestinationBinding.MarkerName));
                File.WriteAllBytes(Path.Combine(impostor, DestinationBinding.MarkerName), marker);
                Directory.Move(root, detached);
                try
                {
                    var unavailable = ProductRunner.Run(registry, job.Id, automatic: true);
                    Check(unavailable.State == ProductRunState.WaitingForDestination,
                        $"Expected the unavailable registered directory for {job.Options.Mode}, got {unavailable.State}.");
                }
                finally { Directory.Move(detached, root); }
                var confirmed = ProductRunner.Run(registry, job.Id, automatic: true);
                Check(confirmed.State == ProductRunState.Confirmed && confirmed.Generation is not null,
                    $"Recovery after alias change failed for {job.Options.Mode}: {confirmed.Detail}");
                generations.Add(job.Id, confirmed.Generation!);
                Check(Directory.GetFileSystemEntries(impostor).Length == 1 &&
                    File.ReadAllBytes(Path.Combine(impostor, DestinationBinding.MarkerName)).AsSpan().SequenceEqual(marker),
                    $"The product wrote into the reused alias for {job.Options.Mode}.");
            }
            Directory.Move(source, Path.Combine(sourceWorkspace.Root, "detached-source"));
            foreach (var job in jobs)
            {
                string output = Directory.CreateDirectory(Path.Combine(sourceWorkspace.Root, $"restored-{job.Options.Mode}")).FullName;
                VersionStore.OpenProduct(job).Restore(generations[job.Id], output);
                Check(File.ReadAllText(Path.Combine(output, "synthetic.txt")) == "edited after alias change", $"Offline restore failed for {job.Options.Mode}.");
                Console.WriteLine($"PASS {job.Options.Mode} bound GUID, reused drive alias, unavailable destination and offline restore.");
            }
            Console.WriteLine("PASS actual packaged core with per-session DOS aliases and two NTFS volumes. This did not reassign a partition or mount a VHDX.");
        }
        finally
        {
            foreach (var alias in aliases.AsEnumerable().Reverse()) Unmount(alias);
            using (destinationWorkspace.OpenRegisteredRoot()) { }
            Directory.Delete(destinationWorkspace.Root, recursive: true);
            using (sourceWorkspace.OpenRegisteredRoot()) { }
            Directory.Delete(sourceWorkspace.Root, recursive: true);
        }

        void Mount(string letter, string path)
        {
            string target = @"\??\" + path;
            if (Lookup(letter) is not null) throw new InvalidOperationException("The synthetic letter is already in use.");
            if (!DefineDosDevice(RawLocal, letter, target)) throw new Win32Exception(Marshal.GetLastWin32Error());
            aliases.Add((letter, target));
            Check(Lookup(letter) == target, "Unexpected synthetic alias target.");
        }
    }

    private static string? Lookup(string letter)
    {
        var value = new StringBuilder(4096);
        if (QueryDosDevice(letter, value, value.Capacity) != 0)
            return value.ToString().Split('\0')[0];
        int error = Marshal.GetLastWin32Error();
        return error == 2 ? null : throw new Win32Exception(error);
    }

    private static void Unmount((string Letter, string Target) alias)
    {
        if (!DefineDosDevice(RemoveExact, alias.Letter, alias.Target)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Check(Lookup(alias.Letter) != alias.Target, "The synthetic drive alias still exists.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
