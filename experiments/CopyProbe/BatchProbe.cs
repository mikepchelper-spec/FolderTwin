using System.Security.Cryptography;

internal static class BatchProbe
{
    internal static int Run()
    {
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            Check(plan.Files.Length == 3 && plan.Directories.Length == 2, "Incorrect synthetic batch plan.");
            Check(fixture.Copy().Complete && fixture.Restore().Complete, "The batch did not copy and restore.");
            fixture.CheckRestored();
            Check(FolderBatch.Run(plan).Entries.Where(entry => !entry.IsDirectory)
                .All(entry => entry.Status == BatchStatus.AlreadyVerified), "A repeated batch did not recognize verified files.");
            foreach (var file in plan.Files)
                Check(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Source, file.RelativePath))).AsSpan().SequenceEqual(file.Hash),
                    "The source changed.");
            Pass("copy, restoration, empty directories, Unicode, and repeated execution preserve the analyzed content");
        }
        using (var fixture = new BatchFixture())
        {
            fixture.Analyze();
            using (var writer = new FileStream(fixture.SourceFile, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                var partial = fixture.Copy();
                Check(!partial.Complete && partial.VerifiedFiles == 2 && !File.Exists(fixture.CopyFile) &&
                    partial.Entries.Any(entry => entry.RelativePath == BatchFixture.SampleNames[0] && entry.Status == BatchStatus.Failed),
                    "An open writer did not leave an explicit partial batch.");
            }
            Check(fixture.Copy().Complete && fixture.Restore().Complete, "Retry after closing a writer failed.");
            fixture.CheckRestored();
            Pass("an open writer leaves a partial batch and retry completes the remaining file");
        }
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            string added = Path.Combine(fixture.Source, "added-after-preview.bin");
            File.WriteAllBytes(added, [1]);
            try
            {
                var stale = FolderBatch.Run(plan);
                Check(!stale.Complete && stale.Problem is not null && stale.VerifiedFiles == 0 && !File.Exists(fixture.CopyFile),
                    "A changed inventory was accepted.");
            }
            finally { File.Delete(added); }
            byte[] changed = File.ReadAllBytes(fixture.SourceFile);
            changed[0] ^= 1;
            File.WriteAllBytes(fixture.SourceFile, changed);
            var partial = FolderBatch.Run(plan);
            Check(!partial.Complete && partial.VerifiedFiles == 2 && !File.Exists(fixture.CopyFile),
                "Same-size source changes passed the preview hash.");
            Pass("inventory changes and same-size content changes invalidate the analyzed operation");
        }
        using (var fixture = new BatchFixture())
        {
            fixture.Analyze();
            File.WriteAllText(fixture.CopyFile, "preexisting sentinel");
            var partial = fixture.Copy();
            Check(!partial.Complete && partial.VerifiedFiles == 2 && File.ReadAllText(fixture.CopyFile) == "preexisting sentinel",
                "A batch overwrote a late destination conflict.");
            Pass("a destination created after analysis remains intact and the batch stays partial");
        }
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            var partial = FolderBatch.Run(plan, checkpoint: (relative, step, _) =>
            {
                if (relative == BatchFixture.SampleNames[0] && step == CopyStep.Verified)
                    throw new IOException("Injected interruption after the receipt was published.");
            });
            Check(!partial.Complete && partial.VerifiedFiles == 2 && !File.Exists(fixture.CopyFile), "The interruption was hidden.");
            var recovered = fixture.Copy();
            Check(recovered.Complete && recovered.Entries.Any(entry => entry.RelativePath == BatchFixture.SampleNames[0] &&
                entry.Status == BatchStatus.Recovered), "A verified pending file was not recovered on retry.");
            Pass("a verified pending file recovers on retry while the other completed files are reverified");
        }
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            using var cancellation = new CancellationTokenSource();
            var cancelled = FolderBatch.Run(plan, cancellation.Token, (_, step, _) =>
            {
                if (step == CopyStep.Published)
                    cancellation.Cancel();
            });
            Check(!cancelled.Complete && cancelled.VerifiedFiles == 1 &&
                cancelled.Entries.Any(entry => entry.Status == BatchStatus.Cancelled), "Cancellation between files was hidden.");
            Check(fixture.Copy().Complete, "A batch cancelled between files did not resume.");
            Pass("cancellation between files preserves the completed file and permits a verified retry");
        }
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            using var cancellation = new CancellationTokenSource();
            var cancelled = FolderBatch.Run(plan, cancellation.Token, (_, step, _) =>
            {
                if (step == CopyStep.Writing)
                    cancellation.Cancel();
            });
            string pending = FolderBatch.TemporaryPath(fixture.Destination, plan.Files[0].RelativePath);
            Check(!cancelled.Complete && File.Exists(pending), "Cancellation lost the partial temporary.");
            Check(!fixture.Copy().Complete && File.Exists(pending), "Retry silently removed an unverified temporary.");
            Pass("cancellation during a file retains its unverified temporary for review");
        }
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            Check(fixture.Copy().Complete, "The corruption test setup failed.");
            byte[] corrupted = File.ReadAllBytes(fixture.CopyFile);
            corrupted[0] ^= 1;
            File.WriteAllBytes(fixture.CopyFile, corrupted);
            var partial = fixture.Restore();
            Check(!partial.Complete && partial.VerifiedFiles == 2 && !File.Exists(fixture.RestoredFile),
                "A corrupt backup was restored.");
            Pass("restoration checks each file against its analyzed hash and rejects corrupt content");
        }
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            string pending = FolderBatch.TemporaryPath(fixture.Destination, BatchFixture.SampleNames[0]);
            try
            {
                VerifiedCopy.Run(Path.Combine(fixture.Source, BatchFixture.SampleNames[2]), fixture.CopyFile, pending,
                    (step, _) => { if (step == CopyStep.Verified) throw new IOException("Injected old receipt."); }, recoveryRoot: fixture.Root);
            }
            catch (IOException) { }
            Check(File.Exists(CopyRecovery.ReceiptPath(pending)), "The foreign receipt was not created.");
            Check(!fixture.Copy().Complete && !File.Exists(fixture.CopyFile) && File.Exists(pending),
                "A receipt for different expected content authorized publication.");
            Pass("a valid receipt for a different expected hash cannot authorize publication");
        }
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            string added = Path.Combine(fixture.Source, "added-during-copy.bin");
            try
            {
                var changed = FolderBatch.Run(plan, checkpoint: (_, step, _) =>
                {
                    if (step == CopyStep.Published && !File.Exists(added))
                        File.WriteAllBytes(added, [1]);
                });
                Check(!changed.Complete && changed.Problem is not null && changed.VerifiedFiles == 3,
                    "An inventory change during copying was reported as complete.");
            }
            finally { File.Delete(added); }
            Pass("a file added during the batch prevents a complete result even when all planned files verified");
        }
        using (var fixture = new BatchFixture())
        {
            fixture.Analyze();
            string outside = Path.Combine(fixture.Root, "outside");
            string sentinel = Path.Combine(outside, "sentinel.bin");
            string junction = Path.Combine(fixture.Destination, BatchFixture.DirectoryNames[0]);
            Directory.CreateDirectory(outside);
            File.WriteAllText(sentinel, "outside sentinel");
            try
            {
                PathProbe.CreateJunction(junction, outside);
                var partial = fixture.Copy();
                Check(!partial.Complete && partial.VerifiedFiles == 2 && File.ReadAllText(sentinel) == "outside sentinel" &&
                    Directory.EnumerateFileSystemEntries(outside).Count() == 1, "Directory creation followed a substituted junction.");
            }
            finally
            {
                if (Directory.Exists(junction))
                    Directory.Delete(junction);
                File.Delete(sentinel);
                Directory.Delete(outside);
            }
            Pass("a substituted destination junction cannot redirect directory creation or file publication");
        }
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            var tampered = FolderBatch.Run(plan, checkpoint: (relative, step, _) =>
            {
                if (step == CopyStep.Published && relative == plan.Files[^1].RelativePath)
                {
                    string output = Path.Combine(fixture.Destination, plan.Files[0].RelativePath);
                    byte[] bytes = File.ReadAllBytes(output);
                    bytes[0] ^= 1;
                    File.WriteAllBytes(output, bytes);
                }
            });
            Check(!tampered.Complete && tampered.VerifiedFiles == 2 &&
                tampered.Entries.Any(entry => entry.RelativePath == plan.Files[0].RelativePath && entry.Status == BatchStatus.Failed),
                "A destination changed during the batch was reported as complete.");
            Pass("the final verification detects a previously published file changed before the batch finishes");
        }
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            using (var operation = new FileStream(Path.Combine(fixture.Destination, FolderBatch.LockName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None))
                Check(!fixture.Copy().Complete && !File.Exists(fixture.CopyFile), "Concurrent batch access was accepted.");
            var escaped = plan with { Files = [plan.Files[0] with { RelativePath = @"..\escaped.bin" }] };
            Check(!FolderBatch.Run(escaped).Complete && !File.Exists(Path.Combine(fixture.Root, "escaped.bin")),
                "An escaped batch path was accepted.");
            string marker = Path.Combine(fixture.Destination, DestinationBinding.MarkerName);
            byte[] original = File.ReadAllBytes(marker);
            File.WriteAllBytes(marker, Guid.NewGuid().ToByteArray());
            try
            {
                Check(!fixture.Copy().Complete && !File.Exists(fixture.CopyFile), "A changed destination marker was accepted.");
            }
            finally { File.WriteAllBytes(marker, original); }
            Pass("the destination lock excludes concurrent batches and malformed paths are rejected before writing");
        }
        using (var fixture = new BatchFixture())
        {
            foreach (string owner in new[] { fixture.SourceFile, fixture.Source, Path.Combine(fixture.Source, BatchFixture.DirectoryNames[0]) })
            {
                string stream = owner + ":contenido-sintético";
                File.WriteAllText(stream, "contenido adicional");
                try
                {
                    bool rejected = false;
                    try { fixture.Analyze(); }
                    catch (InvalidDataException error) when (error.Message.Contains("flujos de datos alternativos", StringComparison.Ordinal))
                    {
                        rejected = true;
                    }
                    Check(rejected && !File.Exists(fixture.CopyFile) && File.ReadAllText(stream) == "contenido adicional",
                        "Analysis failed to reject or preserve a named stream.");
                }
                finally { File.Delete(stream); }
            }
            var plan = fixture.Analyze();
            string lateStream = fixture.SourceFile + ":agregado-después";
            File.WriteAllBytes(lateStream, []);
            try
            {
                var partial = fixture.Copy();
                Check(!partial.Complete && partial.Entries.Any(entry => entry.RelativePath == BatchFixture.SampleNames[0] &&
                    entry.Detail?.Contains("flujos de datos alternativos", StringComparison.Ordinal) == true) &&
                    !File.Exists(fixture.CopyFile) && !File.Exists(Path.Combine(fixture.Destination, BatchCatalog.CommitName)),
                    "A named stream added after analysis did not block confirmation.");
            }
            finally { File.Delete(lateStream); }
            Check(fixture.Copy().Complete, "Removing the synthetic stream did not allow retry.");
            string backupStream = fixture.CopyFile + ":inesperado";
            File.WriteAllText(backupStream, "dato adicional");
            try
            {
                var result = BatchCatalog.Verify(fixture.Destination, plan.Target);
                Check(!result.Complete && result.VerifiedFiles == 2 &&
                    result.Entries.Any(entry => entry.RelativePath == BatchFixture.SampleNames[0] &&
                        entry.Detail?.Contains("flujos de datos alternativos", StringComparison.Ordinal) == true) &&
                    File.ReadAllText(backupStream) == "dato adicional", "Verification ignored or changed an unexpected stream.");
                var restored = fixture.Restore();
                Check(!restored.Complete && !File.Exists(fixture.RestoredFile), "Restoration silently omitted the stream.");
            }
            finally { File.Delete(backupStream); }
            Pass("named streams on files and directories block analysis, late streams block copying, and unexpected backup streams are reported");
        }
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            var first = plan.Files[0];
            string stream = Path.Combine(fixture.Source, first.RelativePath) + ":durante-copia";
            try
            {
                bool added = false;
                var result = FolderBatch.Run(plan, checkpoint: (path, step, _) =>
                {
                    if (path == first.RelativePath && step == CopyStep.Written)
                    {
                        File.WriteAllText(stream, "dato durante la copia");
                        added = true;
                    }
                });
                string pending = FolderBatch.TemporaryPath(fixture.Destination, first.RelativePath);
                Check(added && !result.Complete && !File.Exists(CopyRecovery.ReceiptPath(pending)) &&
                    !File.Exists(Path.Combine(fixture.Destination, first.RelativePath)),
                    "A named stream added during copying was missed.");
            }
            finally { File.Delete(stream); }
            Pass("an alternate stream created during a write prevents the file receipt and publication");
        }
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            var first = plan.Files[0];
            string temporary = FolderBatch.TemporaryPath(fixture.Destination, first.RelativePath);
            var interrupted = FolderBatch.Run(plan, checkpoint: (path, step, _) =>
            {
                if (path == first.RelativePath && step == CopyStep.Verified)
                    throw new IOException("Synthetic interruption after verifying the first file.");
            });
            Check(!interrupted.Complete && File.Exists(CopyRecovery.ReceiptPath(temporary)), "Recovery setup did not leave a receipt.");
            string stream = temporary + ":agregado";
            File.WriteAllText(stream, "preservar");
            try
            {
                var partial = fixture.Copy();
                Check(!partial.Complete && File.ReadAllText(stream) == "preservar" &&
                    !File.Exists(Path.Combine(fixture.Destination, first.RelativePath)) &&
                    !File.Exists(Path.Combine(fixture.Destination, BatchCatalog.CommitName)),
                    "Recovery published a temporary after a named stream was added.");
            }
            finally { File.Delete(stream); }
            Pass("recovery refuses a named stream on a verified temporary without deleting it");
        }
        return 0;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
    private static void Pass(string message) => Console.WriteLine("PASS " + message + ".");
}
