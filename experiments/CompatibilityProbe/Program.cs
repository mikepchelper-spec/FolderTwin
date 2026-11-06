using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

// Compile this identical source against each real core. The old core, not this
// harness or a hand-written serializer, produces the settings and backup formats.
var clock = Stopwatch.StartNew();
try
{
    Require(new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace >= 256L * 1024 * 1024,
        "Compatibility audit requires 256 MiB free; payload is below 64 MiB.");
    if (args is ["--create"])
    {
        var workspace = LabWorkspace.Create();
        using var registry = new ProductJobRegistry(Folder(workspace.Root, "settings"));
        var entries = new List<Entry>();
        foreach (var mode in Enum.GetValues<ProductBackupMode>())
        {
            Deadline();
            string source = Folder(workspace.Root, "source-" + mode);
            foreach (var (relative, bytes) in Expected())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, relative))!);
                File.WriteAllBytes(Path.Combine(source, relative), bytes);
            }
            Directory.CreateDirectory(Path.Combine(source, "Empty"));
            var job = registry.Create(new("Compatibility " + mode, mode, null),
                LocalJobRoots.ReviewDedicatedDestination(source, Folder(workspace.Root, "target-" + mode)));
            var result = ProductRunner.Run(registry, job.Id);
            Require(result.State == ProductRunState.Confirmed && result.Generation is not null,
                "Previous core did not produce a confirmed " + mode + " backup: " + result.Detail);
            registry.ExportConfiguration(job.Id, Path.Combine(workspace.Root, "export-" + mode + ".json"));
            entries.Add(new(job.Id, mode, result.Generation!));
            Directory.Move(source, Path.Combine(workspace.Root, "offline-" + mode));
        }
        File.WriteAllText(Path.Combine(workspace.Root, "compatibility.json"),
            JsonSerializer.Serialize(new CompatibilityFixture(1, CoreHash(), entries.ToArray())));
        Console.WriteLine("CREATOR_CORE_SHA256=" + CoreHash());
        Console.WriteLine("FIXTURE=" + workspace.Root);
    }
    else if (args is ["--check", var path])
    {
        var workspace = LabWorkspace.Open(Path.GetFullPath(path));
        using (workspace.OpenRegisteredRoot()) { }
        var fixture = ReadFixture(workspace.Root);
        using var registry = new ProductJobRegistry(Path.Combine(workspace.Root, "settings"));
        Require(registry.List().Jobs.Length == 3, "Previous registry did not reopen all three jobs.");
        foreach (var entry in fixture.Entries)
        {
            Deadline();
            var job = registry.Get(entry.Id);
            Require(job.Options.Mode == entry.Mode && !Directory.Exists(Path.Combine(workspace.Root, "source-" + entry.Mode)),
                "Mode changed or original source is still available.");
            var exported = ProductJobRegistry.ReadConfiguration(Path.Combine(workspace.Root, "export-" + entry.Mode + ".json"));
            Require(exported.OriginalJob == entry.Id && exported.Options.Mode == entry.Mode,
                "Previous configuration export did not reopen.");
            var history = registry.RunHistory(entry.Id);
            Require(history.Unreadable == 0 && history.Runs.Length == 1 && history.Runs[0].Result is
                { State: ProductRunState.Confirmed } result && result.Generation == entry.Generation && !history.Runs[0].Running,
                "Previous durable backup history did not reopen.");
            var verified = ProductRunner.Verify(registry, entry.Id, entry.Generation);
            Require(verified.State == ProductRunState.Confirmed && !verified.Degraded, "Old backup failed current-core verification.");
            string output = Folder(workspace.Root, "restore-" + entry.Mode);
            Require(!VersionStore.OpenProduct(job).Restore(entry.Generation, output).Degraded, "Old backup restored with a degraded catalog.");
            var actual = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Where(file => file != Path.Combine(output, DestinationBinding.MarkerName) &&
                    !Regex.IsMatch(Path.GetFileName(file), @"^\.foldertwin-[0-9A-F]{64}\.tmp\.(lock|ready)$"))
                .ToDictionary(file => Path.GetRelativePath(output, file), File.ReadAllBytes, StringComparer.Ordinal);
            var wanted = Expected();
            Require(actual.Count == wanted.Count && wanted.All(file => actual.TryGetValue(file.Key, out var bytes) &&
                file.Value.AsSpan().SequenceEqual(bytes)), "Independent restored bytes/tree differ.");
            Require(Directory.EnumerateDirectories(output, "*", SearchOption.AllDirectories)
                .Select(directory => Path.GetRelativePath(output, directory)).ToHashSet(StringComparer.Ordinal)
                .SetEquals(new[] { "Nested", "Empty" }), "Independent restored directory tree differs.");
            var after = registry.RunHistory(entry.Id);
            Require(after.Unreadable == 0 && after.Runs.Length == 2 && after.Runs[0].Intent.Operation == ProductRunOperation.Verify &&
                after.Runs[1].Intent.Operation == ProductRunOperation.Backup, "New history lost the old operation.");
            Console.WriteLine("PASS previous-core " + entry.Mode + " registration, export, history, offline verify and independent byte/tree restore.");
        }
        Console.WriteLine($"PASS compatibility: previousCore={fixture.CreatorCoreSha256}; currentCore={CoreHash()}; jobs=3; seconds={clock.Elapsed.TotalSeconds:F3}.");
    }
    else if (args is ["--clean", var cleanupPath])
    {
        string absolute = Path.GetFullPath(cleanupPath);
        var workspace = LabWorkspace.Open(absolute);
        using (workspace.OpenRegisteredRoot()) { }
        _ = ReadFixture(absolute);
        Require(Path.GetDirectoryName(absolute) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) &&
            Path.GetFileName(absolute).StartsWith(LabWorkspace.Prefix, StringComparison.Ordinal), "Cleanup outside owned temporary fixture refused.");
        Directory.Delete(absolute, recursive: true);
        Console.WriteLine("PASS owned compatibility fixture removed.");
    }
    else throw new ArgumentException("Use --create, --check <owned-fixture>, or --clean <owned-fixture>.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL compatibility: " + error.GetType().Name + ": " + error.Message);
    return 1;
}

void Deadline() => Require(clock.Elapsed < TimeSpan.FromSeconds(60), "Compatibility phase exceeded its 60-second cooperative budget.");
static void Require(bool condition, string detail) { if (!condition) throw new InvalidDataException(detail); }
static string Folder(string root, string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
static string CoreHash() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(ProductRunner).Assembly.Location)));
static Dictionary<string, byte[]> Expected() => new(StringComparer.Ordinal)
{
    ["data.bin"] = [0, 128, 255, 1, 2, 3],
    [Path.Combine("Nested", "Árbol.txt")] = System.Text.Encoding.UTF8.GetBytes("Synthetic RC1 compatibility\n"),
    ["zero.bin"] = []
};
static CompatibilityFixture ReadFixture(string root)
{
    var record = JsonSerializer.Deserialize<CompatibilityFixture>(File.ReadAllText(Path.Combine(root, "compatibility.json")))
        ?? throw new InvalidDataException("Missing compatibility fixture record.");
    Require(record.Version == 1 && Regex.IsMatch(record.CreatorCoreSha256, "^[0-9a-f]{64}$") && record.Entries.Length == 3 &&
        record.Entries.Select(entry => entry.Mode).ToHashSet().SetEquals(Enum.GetValues<ProductBackupMode>()), "Invalid fixture record.");
    return record;
}
internal sealed record Entry(Guid Id, ProductBackupMode Mode, string Generation);
internal sealed record CompatibilityFixture(int Version, string CreatorCoreSha256, Entry[] Entries);
