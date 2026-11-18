using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class CatalogProbe
{
    internal static int Run()
    {
        using (var fixture = new BatchFixture())
        {
            DateTime stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(123_456);
            DateTime creation = stamp.AddDays(-7).AddTicks(543);
            File.SetCreationTimeUtc(fixture.SourceFile, creation);
            File.SetLastWriteTimeUtc(fixture.SourceFile, stamp);
            File.SetAttributes(fixture.SourceFile, FileAttributes.Hidden);
            fixture.Analyze();
            Check(fixture.Copy().Complete && File.GetLastWriteTimeUtc(fixture.CopyFile) == stamp &&
                File.GetCreationTimeUtc(fixture.CopyFile) == creation && File.GetAttributes(fixture.CopyFile).HasFlag(FileAttributes.Hidden),
                "The original file metadata did not survive copying.");
            byte[] primary = File.ReadAllBytes(Path.Combine(fixture.Destination, BatchCatalog.PrimaryName));
            Check(primary.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(fixture.Destination, BatchCatalog.BackupName))),
                "The catalog replicas differ.");
            var catalog = BatchCatalog.Read(fixture.Destination);
            Check(!catalog.Degraded && catalog.Files.Length == 3 && catalog.Directories.Length == 2, "Incorrect catalog entries.");
            Check(catalog.Files.Single(file => file.RelativePath == BatchFixture.SampleNames[0]).Metadata ==
                new FileMetadata(stamp, creation, true), "The metadata did not survive catalog serialization.");
            File.SetLastWriteTimeUtc(fixture.CopyFile, stamp.AddDays(1));
            RequireMetadataIssue("fecha de modificación");
            File.SetLastWriteTimeUtc(fixture.CopyFile, stamp);
            File.SetCreationTimeUtc(fixture.CopyFile, creation.AddDays(1));
            RequireMetadataIssue("fecha de creación");
            File.SetCreationTimeUtc(fixture.CopyFile, creation);
            File.SetAttributes(fixture.CopyFile, FileAttributes.Normal);
            RequireMetadataIssue("atributo Oculto");
            File.SetCreationTimeUtc(fixture.CopyFile, creation.AddDays(1));
            fixture.ForgetPlan();
            File.WriteAllText(fixture.RestoredFile, "synthetic previous name");
            File.SetCreationTimeUtc(fixture.RestoredFile, creation.AddYears(-10));
            File.Delete(fixture.RestoredFile);
            string absent = Path.Combine(fixture.Root, "Origen ausente");
            string config = Path.Combine(fixture.Root, "restore-binding.json");
            fixture.RestoreBinding.SaveNew(config);
            Directory.Move(fixture.Source, absent);
            try
            {
                RestoreInNewProcess(fixture.Root);
                fixture.CheckRestored();
                Check(File.GetLastWriteTimeUtc(fixture.RestoredFile) == stamp && File.GetCreationTimeUtc(fixture.RestoredFile) == creation &&
                    File.GetAttributes(fixture.RestoredFile).HasFlag(FileAttributes.Hidden),
                    "Restoration did not reapply the metadata from the catalog.");
            }
            finally
            {
                Directory.Move(absent, fixture.Source);
                File.Delete(config);
            }
            Pass("a fresh process restores the recorded timestamps and hidden attribute without the source after metadata changes to the backup");

            void RequireMetadataIssue(string detail)
            {
                Check(BatchCatalog.Verify(fixture.Destination, fixture.DestinationBinding).Entries.Any(entry =>
                    entry.RelativePath == BatchFixture.SampleNames[0] && entry.Status == BatchStatus.Failed &&
                    entry.Detail?.Contains(detail, StringComparison.Ordinal) == true), "Verification missed a metadata-only change.");
            }
        }
        foreach (int version in new[] { 1, 2 })
        {
            using var fixture = new BatchFixture();
            var plan = fixture.Analyze();
            var legacy = plan with { Files = plan.Files.Select(file => file with
            {
                Metadata = version == 1 ? null : new FileMetadata(file.Metadata!.LastWriteUtc)
            }).ToArray() };
            PendingBatch.Save(legacy);
            string metadata = Path.Combine(fixture.Destination, PendingBatch.MetadataName);
            byte[] before = File.ReadAllBytes(metadata);
            string original = Encoding.UTF8.GetString(before);
            Check(original.Contains($"\"version\":{version}", StringComparison.Ordinal) &&
                original.Contains("lastWriteUtc", StringComparison.Ordinal) == (version == 2) &&
                !original.Contains("creationUtc", StringComparison.Ordinal) && !original.Contains("hidden", StringComparison.Ordinal),
                "The legacy writer changed the previous layout.");
            string relative = legacy.Files[0].RelativePath;
            var interrupted = FolderBatch.Run(legacy, checkpoint: (path, step, _) =>
            {
                if (path == relative && step == CopyStep.Verified)
                    throw new IOException("Injected interruption in a version 1 session.");
            });
            Check(!interrupted.Complete && File.Exists(CopyRecovery.ReceiptPath(FolderBatch.TemporaryPath(fixture.Destination, relative))),
                "The previous session did not leave a verified temporary.");
            fixture.ForgetPlan();
            var resumed = fixture.Copy();
            Check(resumed.Complete && resumed.Entries.Any(entry => entry.RelativePath == relative && entry.Status == BatchStatus.Recovered) &&
                File.ReadAllBytes(metadata).AsSpan().SequenceEqual(before) &&
                BatchCatalog.Read(fixture.Destination).Files.All(file => file.CatalogVersion == version) && fixture.Restore().Complete,
                "A previous pending session did not resume and restore without changing its descriptor.");
            fixture.CheckRestored();
            Pass($"a version {version} descriptor resumes and its confirmed catalog restores with only the recorded metadata");
        }
        if (OperatingSystem.IsWindows())
            CheckPermissionInheritance();
        using (var fixture = CopiedFixture())
        {
            string primary = Path.Combine(fixture.Destination, BatchCatalog.PrimaryName);
            byte[] original = File.ReadAllBytes(primary);
            File.WriteAllBytes(primary, original[..^17]);
            Check(BatchCatalog.Read(fixture.Destination).Degraded, "A truncated replica was not reported.");
            fixture.ForgetPlan();
            var result = fixture.Restore();
            Check(result.Complete && result.Note is not null, "Restoration did not report the use of a valid replica.");
            fixture.CheckRestored();
            File.WriteAllBytes(primary, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original)
                .Replace("\"size\":65537", "\"size\":65538", StringComparison.Ordinal)));
            Check(BatchCatalog.Read(fixture.Destination).Degraded, "A valid JSON mutation passed the catalog checksum.");
            File.Delete(primary);
            Check(BatchCatalog.Read(fixture.Destination).Degraded, "A missing replica was not reported.");
            string backup = Path.Combine(fixture.Destination, BatchCatalog.BackupName);
            File.WriteAllBytes(backup, original[..^1]);
            Rejected(() => BatchCatalog.Read(fixture.Destination));
            Pass("one intact confirmed replica permits restoration with a warning; losing both prevents import");
        }
        using (var fixture = CopiedFixture())
        {
            string primary = Path.Combine(fixture.Destination, BatchCatalog.PrimaryName);
            byte[] original = File.ReadAllBytes(primary);
            byte[] different = Rewrite(original, nodes => nodes.First(node => (string?)node["kind"] == "file")["size"] = 12);
            _ = BatchCatalog.Parse(new MemoryStream(different));
            File.WriteAllBytes(primary, different);
            Rejected(() => BatchCatalog.Read(fixture.Destination));
            Check(!File.Exists(fixture.RestoredFile), "Conflicting catalogs wrote restoration data.");
            string backup = Path.Combine(fixture.Destination, BatchCatalog.BackupName);
            File.WriteAllBytes(backup, different);
            Rejected(() => BatchCatalog.Read(fixture.Destination));
            File.WriteAllBytes(primary, original);
            File.WriteAllBytes(backup, original);
            string commit = Path.Combine(fixture.Destination, BatchCatalog.CommitName);
            byte[] confirmation = File.ReadAllBytes(commit);
            confirmation[0] ^= 1;
            File.WriteAllBytes(commit, confirmation);
            Rejected(() => BatchCatalog.Read(fixture.Destination));
            Pass("different valid replicas and an altered confirmation are rejected before restoration");
        }
        using (var fixture = CopiedFixture())
        {
            byte[] original = File.ReadAllBytes(Path.Combine(fixture.Destination, BatchCatalog.PrimaryName));
            foreach (Action<List<JsonObject>> mutation in new Action<List<JsonObject>>[]
            {
                nodes => nodes[0]["version"] = 4,
                nodes => nodes[0]["version"] = 2,
                nodes => nodes[0]["version"] = 1,
                nodes => nodes.First(node => (string?)node["kind"] == "file").Remove("lastWriteUtc"),
                nodes => nodes.First(node => (string?)node["kind"] == "file")["lastWriteUtc"] = null,
                nodes => nodes.First(node => (string?)node["kind"] == "file")["lastWriteUtc"] = "2001-02-03T04:05:06",
                nodes => nodes.First(node => (string?)node["kind"] == "file")["lastWriteUtc"] = "1601-01-01T00:00:00Z",
                nodes => nodes.First(node => (string?)node["kind"] == "file").Remove("creationUtc"),
                nodes => nodes.First(node => (string?)node["kind"] == "file")["creationUtc"] = "1601-01-01T00:00:00Z",
                nodes => nodes.First(node => (string?)node["kind"] == "file").Remove("hidden"),
                nodes => nodes.First(node => (string?)node["kind"] == "file")["hidden"] = "true",
                nodes => nodes[0]["unexpected"] = true,
                nodes => nodes[0].Remove("generationId"),
                nodes => nodes.First(node => (string?)node["kind"] == "file")["path"] = @"..\escaped.bin",
                nodes => nodes.First(node => (string?)node["kind"] == "file")["path"] = @"C:\outside.bin",
                nodes => nodes.First(node => (string?)node["kind"] == "file")["path"] = ".foldertwin-reserved.bin",
                nodes => nodes.First(node => (string?)node["kind"] == "file")["path"] = @"missing-parent\file.bin",
                nodes => nodes.First(node => (string?)node["kind"] == "file")["path"] = "trailing.",
                nodes => nodes.First(node => (string?)node["kind"] == "file")["path"] = "stream:alternate",
                nodes => nodes.First(node => (string?)node["kind"] == "file")["path"] = new string('x', 256),
                nodes => nodes.First(node => (string?)node["kind"] == "file")["path"] = BatchFixture.DirectoryNames[0],
                nodes =>
                {
                    var files = nodes.Where(node => (string?)node["kind"] == "file").ToArray();
                    files[0]["path"] = "ALPHA.bin";
                    files[1]["path"] = "alpha.bin";
                },
                nodes => nodes.First(node => (string?)node["kind"] == "file")["hash"] = Convert.ToBase64String([1]),
                nodes => nodes.First(node => (string?)node["kind"] == "file")["size"] = -1,
                nodes =>
                {
                    foreach (var file in nodes.Where(node => (string?)node["kind"] == "file"))
                        file["size"] = long.MaxValue;
                },
                nodes => nodes[^1]["files"] = 99,
                nodes => nodes.First(node => (string?)node["kind"] == "file")["kind"] = "unknown"
            })
                RejectBytes(Rewrite(original, mutation));
            byte[] duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original).Replace("\"version\":3", "\"version\":3,\"version\":3", StringComparison.Ordinal));
            RejectBytes(duplicate);
            RejectBytes(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original).Replace("muestra.txt", @"invalid-\ud800.bin", StringComparison.Ordinal)));
            RejectBytes(original[..^1]);
            RejectBytes(original.Concat("{}\n"u8.ToArray()).ToArray());
            RejectBytes(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original).Replace("}\n{", "}{", StringComparison.Ordinal)));
            RejectBytes(new byte[BatchCatalog.MaximumBytes + 1]);
            RejectBytes(Encoding.UTF8.GetBytes(new string(' ', BatchCatalog.MaximumLineBytes) + "\n"));
            byte[] malformedUtf8 = original.ToArray();
            malformedUtf8[1] = 255;
            RejectBytes(malformedUtf8);
            RejectBytes(Rewrite(original, nodes =>
            {
                nodes.RemoveAll(node => (string?)node["kind"] is "directory" or "file");
                for (int index = 0; index <= 10_000; index++)
                    nodes.Insert(nodes.Count - 1, new JsonObject { ["kind"] = "file", ["path"] = $"{index:D5}.bin",
                        ["size"] = 0, ["hash"] = Convert.ToBase64String(SHA256.HashData([])),
                        ["lastWriteUtc"] = "2001-02-03T04:05:06Z", ["creationUtc"] = "2001-02-03T04:05:06Z", ["hidden"] = false });
                nodes[^1]["files"] = 10_001;
                nodes[^1]["directories"] = 0;
            }));
            foreach (int depth in new[] { 64, 65 })
            {
                byte[] deep = Rewrite(original, nodes =>
                {
                    nodes.RemoveAll(node => (string?)node["kind"] is "directory" or "file");
                    for (int level = 1; level <= depth; level++)
                        nodes.Insert(nodes.Count - 1, new JsonObject { ["kind"] = "directory", ["path"] = string.Join("\\", Enumerable.Repeat("a", level)) });
                    nodes[^1]["files"] = 0;
                    nodes[^1]["directories"] = depth;
                });
                if (depth == 64)
                    Check(BatchCatalog.Parse(new MemoryStream(deep)).Directories.Length == 64, "A supported directory depth was rejected.");
                else
                    RejectBytes(deep);
            }
            Pass("strict streaming parsing rejects malicious paths, schema errors, duplicate fields, truncation, framing, encoding, and size violations");
        }
        foreach (CatalogStep boundary in Enum.GetValues<CatalogStep>())
        {
            using var fixture = new BatchFixture();
            var plan = fixture.Analyze();
            Check(FolderBatch.Run(plan).Complete, "The interrupted publication setup failed.");
            bool checkpointReached = false;
            try
            {
                BatchCatalog.Publish(plan, step =>
                {
                    if (step == boundary)
                    {
                        checkpointReached = true;
                        throw new IOException("Injected catalog interruption.");
                    }
                });
                throw new InvalidOperationException("The publication checkpoint was not reached.");
            }
            catch (IOException) when (checkpointReached) { }
            if (boundary == CatalogStep.Committed)
                Check(!BatchCatalog.Read(fixture.Destination).Degraded, "The committed catalog could not reopen.");
            else
                Rejected(() => BatchCatalog.Read(fixture.Destination));
            BatchCatalog.Publish(plan);
            Check(fixture.Restore().Complete, "Retry did not finish the interrupted catalog publication.");
            fixture.CheckRestored();
        }
        Pass("interruption at each publication transition preserves pending data and retry confirms both replicas");
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            Check(FolderBatch.Run(plan).Complete, "The changed data test setup failed.");
            Rejected(() => BatchCatalog.Publish(plan, step =>
            {
                if (step == CatalogStep.BackupPublished)
                {
                    byte[] bytes = File.ReadAllBytes(fixture.CopyFile);
                    bytes[0] ^= 1;
                    File.WriteAllBytes(fixture.CopyFile, bytes);
                }
            }));
            Check(!File.Exists(Path.Combine(fixture.Destination, BatchCatalog.CommitName)), "Changed data was confirmed.");
            Rejected(() => BatchCatalog.Read(fixture.Destination));
            Pass("data changed after both replicas publish prevents confirmation and normal restoration");
        }
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            Check(FolderBatch.Run(plan).Complete, "The pending catalog setup failed.");
            string draft = Path.Combine(fixture.Destination, BatchCatalog.PrimaryName + ".tmp");
            File.WriteAllText(draft, "unfinished\n");
            Rejected(() => BatchCatalog.Publish(plan));
            Check(File.ReadAllText(draft) == "unfinished\n", "Retry changed an incomplete catalog draft.");
            Check(!File.Exists(Path.Combine(fixture.Destination, BatchCatalog.CommitName)), "An invalid draft was confirmed.");
            Pass("an incomplete catalog draft is preserved and cannot be confirmed automatically");
        }
        using (var fixture = CopiedFixture())
        {
            string marker = Path.Combine(fixture.Restored, DestinationBinding.MarkerName);
            byte[] original = File.ReadAllBytes(marker);
            File.WriteAllBytes(marker, fixture.DestinationBinding.Marker.ToByteArray());
            try
            {
                foreach (string name in new[] { BatchCatalog.PrimaryName, BatchCatalog.BackupName, BatchCatalog.CommitName, FolderBatch.LockName })
                    File.Copy(Path.Combine(fixture.Destination, name), Path.Combine(fixture.Restored, name));
                Rejected(() => BatchCatalog.Read(fixture.Restored));
                Check(!File.Exists(fixture.RestoredFile), "A copied marker and catalog authorized a different directory.");
            }
            finally { File.WriteAllBytes(marker, original); }
            using var operation = new FileStream(Path.Combine(fixture.Destination, FolderBatch.LockName), FileMode.Open, FileAccess.Read, FileShare.None);
            Rejected(() => BatchCatalog.Read(fixture.Destination));
            Rejected(() => BatchCatalog.Publish(fixture.Plan!));
            Pass("copied catalogs cannot authorize another directory and the store lock excludes concurrent import or publication");
        }
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static void CheckPermissionInheritance()
    {
        using var fixture = new BatchFixture();
        var sourceSid = new SecurityIdentifier("S-1-5-21-111-222-333-1001");
        var backupSid = new SecurityIdentifier("S-1-5-21-111-222-333-1002");
        var restoredSid = new SecurityIdentifier("S-1-5-21-111-222-333-1003");
        var source = new FileInfo(fixture.SourceFile);
        var sourceAcl = source.GetAccessControl(AccessControlSections.Access);
        sourceAcl.AddAccessRule(new FileSystemAccessRule(sourceSid, FileSystemRights.ReadData, AccessControlType.Allow));
        source.SetAccessControl(sourceAcl);
        AddInheritableRule(fixture.Destination, backupSid);
        AddInheritableRule(fixture.Restored, restoredSid);

        fixture.Analyze();
        Check(fixture.Copy().Complete && fixture.Restore().Complete, "The ACL inheritance fixture did not copy and restore.");
        var origin = Rules(fixture.SourceFile);
        var backup = Rules(fixture.CopyFile);
        var restored = Rules(fixture.RestoredFile);
        Check(origin.Any(rule => rule.IdentityReference.Equals(sourceSid) && !rule.IsInherited) &&
            backup.Any(rule => rule.IdentityReference.Equals(backupSid) && rule.IsInherited) &&
            !backup.Any(rule => rule.IdentityReference.Equals(sourceSid)) &&
            restored.Any(rule => rule.IdentityReference.Equals(restoredSid) && rule.IsInherited) &&
            !restored.Any(rule => rule.IdentityReference.Equals(sourceSid) || rule.IdentityReference.Equals(backupSid)),
            "The copy or restoration imported a prior file ACL instead of inheriting the new parent rules.");
        var nested = Rules(Path.Combine(fixture.Restored, BatchFixture.SampleNames[1]));
        Check(nested.Any(rule => rule.IdentityReference.Equals(restoredSid) && rule.IsInherited),
            "A file in a restored subdirectory did not inherit the destination rule.");
        Pass("copy and restore inherit their own parent ACLs without importing source or backup rules");

        static void AddInheritableRule(string path, SecurityIdentifier sid)
        {
            var directory = new DirectoryInfo(path);
            var acl = directory.GetAccessControl(AccessControlSections.Access);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadData,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(acl);
        }

        static FileSystemAccessRule[] Rules(string path) => new FileInfo(path).GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
    }

    internal static int RestoreChild(string root)
    {
        BatchFixture.ValidateRoot(root);
        using var fence = new DirectoryFence(root);
        using var configHandle = WindowsProbeNative.OpenPlainFile(fence.Root, "restore-binding.json", FileAccess.Read, FileShare.Read);
        var target = DestinationBinding.Load(Path.Combine(root, "restore-binding.json"));
        string backup = Path.Combine(root, "Destino");
        var catalog = BatchCatalog.Read(backup);
        var result = FolderBatch.Run(catalog.RestoreTo(root, backup, Path.Combine(root, "Restaurado"), target));
        Check(result.Complete && result.VerifiedFiles == 3, "Independent restoration failed.");
        Console.WriteLine("PASS independent catalog restoration.");
        return 0;
    }

    private static void RestoreInNewProcess(string root)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--restore-catalog-child");
        start.ArgumentList.Add(root);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start restore process.");
        if (!process.WaitForExit(15_000))
        {
            process.Kill();
            process.WaitForExit();
            throw new TimeoutException("Independent restoration timed out.");
        }
        Check(process.ExitCode == 0 && process.StandardOutput.ReadToEnd().Contains("PASS independent catalog restoration.", StringComparison.Ordinal),
            "Independent restoration failed. " + process.StandardError.ReadToEnd());
    }

    private static BatchFixture CopiedFixture()
    {
        var fixture = new BatchFixture();
        try
        {
            fixture.Analyze();
            Check(fixture.Copy().Complete, "The catalog fixture did not copy and confirm.");
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }
    private static byte[] Rewrite(byte[] original, Action<List<JsonObject>> mutation)
    {
        var nodes = Encoding.UTF8.GetString(original).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
        mutation(nodes);
        string body = string.Join("\n", nodes.Take(nodes.Count - 1).Select(node => node.ToJsonString())) + "\n";
        nodes[^1]["hash"] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        return Encoding.UTF8.GetBytes(body + nodes[^1].ToJsonString() + "\n");
    }
    private static void RejectBytes(byte[] bytes) => Rejected(() => BatchCatalog.Parse(new MemoryStream(bytes)));
    private static void Rejected(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("The invalid catalog operation was accepted.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
    private static void Pass(string message) => Console.WriteLine("PASS " + message + ".");
}
