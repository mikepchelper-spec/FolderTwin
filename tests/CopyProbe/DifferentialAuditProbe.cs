using System.Diagnostics;
using System.Security.Cryptography;

// Independent byte/tree oracle. No expected tree is computed by FolderTwin's planner/catalog reader.
internal static class DifferentialAuditProbe
{
    internal static int Run()
    {
        if (new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace < 256L * 1024 * 1024)
            throw new IOException("The bounded audit requires 256 MiB free; it never fills the host disk.");
        var elapsed = Stopwatch.StartNew();
        var failures = new List<string>();
        int cases = 0, points = 0;
        foreach (var mode in Enum.GetValues<ProductBackupMode>())
        {
            foreach (int seed in new[] { 1337, 7331 }) RunCase(mode, seed, caseRename: false);
            RunCase(mode, 42, caseRename: true);
        }
        Console.WriteLine($"Differential audit: cases={cases}; points={points}; failures={failures.Count}; seconds={elapsed.Elapsed.TotalSeconds:F3}; peakResident={Process.GetCurrentProcess().PeakWorkingSet64}. Budget: <64 MiB synthetic payload, 120 seconds, 256 MiB free reserve.");
        foreach (string failure in failures) Console.Error.WriteLine("FAIL differential audit: " + failure);
        return failures.Count == 0 ? 0 : 1;

        void RunCase(ProductBackupMode mode, int seed, bool caseRename)
        {
            var workspace = LabWorkspace.Create();
            bool passed = false;
            cases++;
            try
            {
                string source = Folder("source"), parent = Folder("targets");
                using var registry = new ProductJobRegistry(Folder("settings"));
                var job = registry.Create(new($"Oracle {mode} {seed}", mode, null),
                    LocalJobRoots.ReviewDedicatedDestination(source, parent));
                var store = VersionStore.OpenProduct(job, initialize: true);
                if (mode != ProductBackupMode.Versions) store.InitializeMirror(resumeInitialization: true);
                var model = new Tree();
                var current = new Tree();
                var history = new List<(string Id, Tree Expected)>();
                Put("igual.txt", [1, 2, 3, 4, 5, 6, 7, 8]);
                Put("retirado.txt", [8, 7, 6]);
                Put("case.txt", [9, 8, 7]);
                Put(Path.Combine("Documentos", "Árbol.txt"), [0, 128, 255]);
                Put("vacío.bin", []);
                AddDirectory("Carpeta vacía");
                Publish("initial");
                if (caseRename)
                {
                    // Two moves make the source spelling deterministic on NTFS without changing its root.
                    File.Move(Path.Combine(source, "case.txt"), Path.Combine(source, "case-intermediate.tmp"));
                    File.Move(Path.Combine(source, "case-intermediate.tmp"), Path.Combine(source, "CASE.txt"));
                    byte[] bytes = model.Files["case.txt"];
                    model.Files.Remove("case.txt"); model.Files.Add("CASE.txt", bytes);
                    Publish("case-only source rename");
                    if (mode == ProductBackupMode.Accumulative)
                    {
                        string exclusive = Path.Combine("Documentos", "solo-destino.txt");
                        File.WriteAllBytes(Path.Combine(store.MirrorCurrentPath, exclusive), [41, 42]);
                        current.Files.Add(exclusive, [41, 42]);
                        Directory.Move(Path.Combine(source, "Documentos"), Path.Combine(source, "parent-intermediate"));
                        Directory.Move(Path.Combine(source, "parent-intermediate"), Path.Combine(source, "DOCUMENTOS"));
                        model.Directories.Remove("Documentos"); model.Directories.Add("DOCUMENTOS");
                        string old = Path.Combine("Documentos", "Árbol.txt"), renamed = Path.Combine("DOCUMENTOS", "Árbol.txt");
                        model.Files.Remove(old); model.Files.Add(renamed, [0, 128, 255]);
                        Put(Path.Combine("DOCUMENTOS", "nuevo.txt"), [51, 52, 53]);
                        Publish("case-only parent, exclusive child and new source file; planned interruption", interruptPlanned: true);

                        // A differently-cased source name must not bypass external-edit detection.
                        string external = Path.Combine(store.MirrorCurrentPath, "case.txt");
                        DateTime externalStamp = File.GetLastWriteTimeUtc(external);
                        File.WriteAllBytes(external, [61, 62, 63]);
                        RequireRejected("external edit under case-only source rename", requireConflict: true);
                        File.WriteAllBytes(external, current.Files["case.txt"]);
                        File.SetLastWriteTimeUtc(external, externalStamp);

                        // Accumulation must not discard a current file or exclusive descendants
                        // merely because the source changes the type of the same NTFS path.
                        string caseFile = Path.Combine(source, "CASE.txt");
                        var beforeSourceDeletion = store.PreviewMirror(job.Source.VolumePath);
                        File.Delete(caseFile);
                        // The desired union is unchanged by this deletion; only the raw source
                        // inventory comparison can reject the stale review.
                        RequireRejected("source deletion after review leaves the same union", requireConflict: false,
                            staleReview: beforeSourceDeletion);
                        File.WriteAllBytes(caseFile, model.Files["CASE.txt"]);
                        File.Delete(caseFile); Directory.CreateDirectory(caseFile);
                        File.WriteAllBytes(Path.Combine(caseFile, "child.bin"), [71]);
                        RequireRejected("file to directory collision", requireConflict: false);
                        Directory.Delete(caseFile, recursive: true); File.WriteAllBytes(caseFile, model.Files["CASE.txt"]);
                        string parentPath = Path.Combine(source, "DOCUMENTOS");
                        Directory.Delete(parentPath, recursive: true); File.WriteAllBytes(parentPath, [72]);
                        RequireRejected("directory to file collision with exclusive child", requireConflict: false);
                        File.Delete(parentPath);
                        foreach (string directory in model.Directories.Where(path => path.Equals("DOCUMENTOS", StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith("DOCUMENTOS" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                            Directory.CreateDirectory(Path.Combine(source, directory));
                        foreach (var file in model.Files.Where(file => file.Key.StartsWith("DOCUMENTOS" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                            File.WriteAllBytes(Path.Combine(source, file.Key), file.Value);
                        Publish("recovery after rejected external edit and type changes");
                    }
                }
                else
                {
                    var random = new Random(seed);
                    int[] actions = Enumerable.Range(0, 5).OrderBy(_ => random.Next()).ToArray();
                    foreach (int action in actions)
                    {
                        switch (action)
                        {
                            case 0:
                                DateTime stamp = File.GetLastWriteTimeUtc(Path.Combine(source, "igual.txt"));
                                Put("igual.txt", [8, 7, 6, 5, 4, 3, 2, 1]);
                                File.SetLastWriteTimeUtc(Path.Combine(source, "igual.txt"), stamp);
                                break;
                            case 1:
                                byte[] bytes = new byte[257]; random.NextBytes(bytes);
                                Put(Path.Combine("Nuevos", "niño.txt"), bytes);
                                break;
                            case 2:
                                File.Delete(Path.Combine(source, "retirado.txt")); model.Files.Remove("retirado.txt");
                                break;
                            case 3:
                                string old = Path.Combine("Documentos", "Árbol.txt"), renamed = Path.Combine("Documentos", "Árbol-renombrado.txt");
                                File.Move(Path.Combine(source, old), Path.Combine(source, renamed));
                                model.Files.Add(renamed, model.Files[old]); model.Files.Remove(old);
                                break;
                            case 4:
                                AddDirectory(Path.Combine("Nuevos", "Vacía")); Put("recreado.txt", []);
                                break;
                        }
                        Publish("seeded mutation " + action);
                    }
                    if (mode == ProductBackupMode.Accumulative)
                    {
                        const string extra = "exclusivo.txt";
                        File.WriteAllBytes(Path.Combine(store.MirrorCurrentPath, extra), [31, 32, 33]);
                        current.Files.Add(extra, [31, 32, 33]);
                        Publish("destination-exclusive input");
                    }
                    // Empty is explicit, not a failed enumeration. Accumulative must retain its union.
                    foreach (string file in model.Files.Keys) File.Delete(Path.Combine(source, file));
                    foreach (string directory in model.Directories.OrderByDescending(path => path.Count(c => c == Path.DirectorySeparatorChar)))
                        Directory.Delete(Path.Combine(source, directory));
                    model = new Tree();
                    Publish("legitimately empty source");
                }
                Directory.Move(source, Path.Combine(workspace.Root, "detached-source"));
                foreach (var (id, expected) in history)
                {
                    Deadline();
                    string output = Folder("restore-" + id);
                    _ = VersionStore.OpenProduct(job).Restore(id, output);
                    RequireTree(expected, ReadTree(output, restored: true), $"offline restore {mode} {seed} {id}");
                }
                passed = true;
                Console.WriteLine($"PASS differential {mode}, seed={seed}, caseRename={caseRename}, points={history.Count}: original bytes/tree and offline recovery agree with independent model.");

                void Publish(string operation, bool interruptPlanned = false)
                {
                    Deadline();
                    RequireTree(model, ReadTree(source), "fixture model before " + operation);
                    string before = SourceSignature(source);
                    var expected = mode == ProductBackupMode.Accumulative ? current.Merge(model) : model.Clone();
                    string generation;
                    try
                    {
                        if (mode == ProductBackupMode.Versions)
                        {
                            generation = VersionStore.NewGenerationId();
                            store.CreatePoint(job.Source.VolumePath, generation);
                        }
                        else
                        {
                            var review = store.PreviewMirror(job.Source.VolumePath);
                            if (interruptPlanned)
                            {
                                const string stopped = "Differential audit planned interruption";
                                try
                                {
                                    _ = store.BeginMirror(review, emptyReviewed: true, massReviewed: true, checkpoint: step =>
                                    { if (step == MirrorStep.Planned) throw new IOException(stopped); });
                                    throw new InvalidOperationException("Planned interruption was not reached.");
                                }
                                catch (IOException error) when (error.Message == stopped) { }
                                store.ResumeMirror(review.Generation);
                                generation = review.Generation;
                            }
                            else generation = store.BeginMirror(review, emptyReviewed: true, massReviewed: true);
                            RequireTree(expected, ReadTree(store.MirrorCurrentPath), "current after " + operation);
                        }
                    }
                    finally
                    {
                        if (SourceSignature(source) != before) throw new InvalidOperationException("Engine changed source bytes or supported file metadata.");
                    }
                    if (store.Verify(generation)) throw new InvalidOperationException("Oracle point has a degraded catalog.");
                    history.Add((generation, expected.Clone())); current = expected; points++;
                }
                void RequireRejected(string operation, bool requireConflict, MirrorPreview? staleReview = null)
                {
                    Deadline();
                    string beforeSource = SourceSignature(source), beforeCurrent = SourceSignature(store.MirrorCurrentPath);
                    int beforePoints = store.ListPoints().Length;
                    try
                    {
                        var review = staleReview ?? store.PreviewMirror(job.Source.VolumePath);
                        if (requireConflict && review.Conflicts.Length == 0)
                            throw new InvalidOperationException("Missing accumulated external-edit conflict.");
                        _ = store.BeginMirror(review, emptyReviewed: true, massReviewed: true);
                        throw new InvalidOperationException("Unsafe accumulation accepted: " + operation);
                    }
                    catch (InvalidDataException) { }
                    if (beforeSource != SourceSignature(source) || beforeCurrent != SourceSignature(store.MirrorCurrentPath) ||
                        beforePoints != store.ListPoints().Length)
                        throw new InvalidOperationException("Rejected accumulation changed files or confirmed points: " + operation);
                }
                void AddDirectory(string path)
                {
                    Directory.CreateDirectory(Path.Combine(source, path));
                    for (string? next = path; !string.IsNullOrEmpty(next); next = Path.GetDirectoryName(next)) model.Directories.Add(next);
                }
                void Put(string path, byte[] bytes)
                {
                    if (Path.GetDirectoryName(path) is { Length: > 0 } directory) AddDirectory(directory);
                    File.WriteAllBytes(Path.Combine(source, path), bytes); model.Files[path] = bytes;
                }
            }
            catch (Exception error)
            {
                failures.Add($"{mode}, seed={seed}, caseRename={caseRename}: {error.GetType().Name}: {error.Message}");
                Console.Error.WriteLine("Preserved owned failing fixture: " + workspace.Root);
            }
            finally
            {
                // An unexplained failure is evidence; retain only that owned fixture for investigation.
                if (passed)
                {
                    using (workspace.OpenRegisteredRoot()) { }
                    string absolute = Path.GetFullPath(workspace.Root);
                    if (Path.GetDirectoryName(absolute) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) ||
                        !Path.GetFileName(absolute).StartsWith(LabWorkspace.Prefix, StringComparison.Ordinal))
                        throw new InvalidOperationException("Refusing cleanup outside the owned temporary workspace.");
                    Directory.Delete(absolute, recursive: true);
                }
            }
            string Folder(string name) => Directory.CreateDirectory(Path.Combine(workspace.Root, name)).FullName;
        }
        void Deadline()
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(120)) throw new TimeoutException("Bounded differential audit deadline exceeded.");
        }
    }

    private sealed class Tree
    {
        internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Tree Clone() => new Tree().Merge(this);
        internal Tree Merge(Tree changes)
        {
            var result = new Tree();
            foreach (var (path, content) in Files) result.Files[path] = content;
            foreach (var (path, content) in changes.Files) result.Files[path] = content;
            result.Directories.UnionWith(Directories); result.Directories.UnionWith(changes.Directories);
            return result;
        }
    }
    private static Tree ReadTree(string root, bool restored = false)
    {
        var result = new Tree();
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            // Restores deliberately retain their marker and per-file durable receipts/locks.
            // Match only their documented names; unexpected user content still fails the oracle.
            string name = Path.GetFileName(path);
            if (restored && (path == Path.Combine(root, DestinationBinding.MarkerName) ||
                System.Text.RegularExpressions.Regex.IsMatch(name, @"^\.foldertwin-[0-9A-F]{64}\.tmp\.(lock|ready)$"))) continue;
            result.Files.Add(Path.GetRelativePath(root, path), File.ReadAllBytes(path));
        }
        foreach (string path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            result.Directories.Add(Path.GetRelativePath(root, path));
        return result;
    }
    private static void RequireTree(Tree expected, Tree actual, string context)
    {
        if (!expected.Directories.SetEquals(actual.Directories) || expected.Files.Count != actual.Files.Count ||
            expected.Files.Any(item => !actual.Files.TryGetValue(item.Key, out var bytes) || !item.Value.AsSpan().SequenceEqual(bytes)))
            throw new InvalidOperationException("Tree/bytes differ: " + context);
    }
    private static string SourceSignature(string root) => string.Join("\n",
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path =>
            Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) + ":" +
            File.GetCreationTimeUtc(path).Ticks + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + File.GetAttributes(path)));
}
