using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

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
            var plan = fixture.Analyze();
            string relative = BatchFixture.SampleNames[0];
            string pending = FolderBatch.TemporaryPath(fixture.Destination, relative);
            FileAttributes original = File.GetAttributes(fixture.SourceFile);
            bool changed = false;
            try
            {
                var result = fixture.Copy(checkpoint: (path, step, _) =>
                {
                    if (path == relative && step == CopyStep.Written)
                    {
                        File.SetAttributes(fixture.SourceFile, original | FileAttributes.Offline);
                        changed = true;
                    }
                });
                Check(changed && !result.Complete && result.Entries.Any(entry => entry.RelativePath == relative &&
                    entry.Detail?.Contains("no disponibles localmente", StringComparison.Ordinal) == true) &&
                    File.Exists(pending) && !File.Exists(CopyRecovery.ReceiptPath(pending)) && !File.Exists(fixture.CopyFile) &&
                    !File.Exists(Path.Combine(fixture.Destination, BatchCatalog.CommitName)),
                    "An offline source observed after the read authorized publication.");
            }
            finally { File.SetAttributes(fixture.SourceFile, original); }
            Pass("an offline attribute set during copying leaves no verified receipt or complete point");
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
        using (var fixture = new BatchFixture())
        {
            var plan = fixture.Analyze();
            string alias = Path.Combine(fixture.Root, "alias.bin");
            Link(alias, fixture.SourceFile);
            try
            {
                var partial = fixture.Copy();
                Check(!partial.Complete && partial.Entries.Any(entry => entry.RelativePath == BatchFixture.SampleNames[0] &&
                    entry.Detail?.Contains("enlaces duros", StringComparison.Ordinal) == true) && !File.Exists(fixture.CopyFile),
                    "A linked source file was copied after analysis.");
            }
            finally { File.Delete(alias); }
            string relative = BatchFixture.SampleNames[0];
            var interrupted = fixture.Copy(checkpoint: (path, step, _) =>
            {
                if (path == relative && step == CopyStep.Verified)
                    throw new IOException("Synthetic interruption before publication.");
            });
            string temporary = FolderBatch.TemporaryPath(fixture.Destination, relative);
            Check(!interrupted.Complete && File.Exists(CopyRecovery.ReceiptPath(temporary)), "A verified temporary was not left for retry.");
            Link(alias, temporary);
            try
            {
                Check(!fixture.Copy().Complete && File.Exists(temporary) && !File.Exists(fixture.CopyFile),
                    "Recovery published a linked temporary.");
            }
            finally { File.Delete(alias); }
            Check(fixture.Copy().Complete, "Removing the synthetic alias did not allow recovery.");
            Link(alias, fixture.CopyFile);
            try
            {
                var result = BatchCatalog.Verify(fixture.Destination, plan.Target);
                Check(!result.Complete && result.VerifiedFiles == 2 &&
                    !fixture.Restore().Complete && !File.Exists(fixture.RestoredFile) &&
                    SHA256.HashData(File.ReadAllBytes(alias)).AsSpan().SequenceEqual(
                        plan.Files.Single(file => file.RelativePath == BatchFixture.SampleNames[0]).Hash),
                    "A linked backup was accepted or its alias was altered.");
            }
            finally { File.Delete(alias); }
            Pass("hard links block source copying, temporary recovery, and confirmed backup verification without modifying the alias");
        }
        using (var fixture = new BatchFixture())
        {
            foreach (string path in new[] { fixture.Source, Path.Combine(fixture.Source, BatchFixture.DirectoryNames[0]), fixture.SourceFile })
            {
                SetExtendedAttribute(path, present: true);
                try
                {
                    bool rejected = false;
                    try { fixture.Analyze(); }
                    catch (InvalidDataException error) when (error.Message.Contains("atributos extendidos de NTFS", StringComparison.Ordinal))
                    {
                        rejected = true;
                    }
                    Check(rejected && !File.Exists(fixture.CopyFile), "Analysis ignored a real NTFS extended attribute.");
                }
                finally { SetExtendedAttribute(path, present: false); }
            }
            var plan = fixture.Analyze();
            Check(fixture.Copy().Complete, "Removing the synthetic attributes did not allow copying.");
            SetExtendedAttribute(fixture.CopyFile, present: true);
            try
            {
                var result = BatchCatalog.Verify(fixture.Destination, plan.Target);
                Check(!result.Complete && result.Entries.Any(entry => entry.RelativePath == BatchFixture.SampleNames[0] &&
                    entry.Detail?.Contains("atributos extendidos de NTFS", StringComparison.Ordinal) == true) &&
                    !fixture.Restore().Complete && !File.Exists(fixture.RestoredFile),
                    "Verification or restoration accepted a backup with an extended attribute.");
            }
            finally { SetExtendedAttribute(fixture.CopyFile, present: false); }
            Pass("extended attributes on files and directories block analysis and are also detected in a confirmed backup");
        }
        using (var fixture = new BatchFixture())
        {
            const uint setCompression = 0x0009c040;
            const uint setSparse = 0x000900c4;
            const int sparseOffset = 1024 * 1024;
            byte[] compressible = new byte[128 * 1024];
            compressible[0] = 42;
            File.WriteAllBytes(fixture.SourceFile, compressible);
            using (var file = File.Open(fixture.SourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                SetFileLayout(file, setCompression, [1, 0]); // COMPRESSION_FORMAT_DEFAULT.
            string sparse = Path.Combine(fixture.Source, BatchFixture.SampleNames[2]);
            using (var file = File.Open(sparse, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                SetFileLayout(file, setSparse, null);
                file.Position = sparseOffset;
                file.WriteByte(73);
            }
            Check(File.GetAttributes(fixture.SourceFile).HasFlag(FileAttributes.Compressed) &&
                File.GetAttributes(sparse).HasFlag(FileAttributes.SparseFile), "The synthetic layouts were not created.");
            var plan = FolderBatch.Prepare(fixture.Root, fixture.Source, fixture.Destination, fixture.DestinationBinding);
            Check(plan.Bytes == compressible.Length + sparseOffset + 1 + 65_537, "The preview did not count logical bytes.");
            Check(PendingBatch.Start(plan).Complete && fixture.Restore().Complete, "The logical bytes did not copy and restore.");
            byte[] expectedSparse = new byte[sparseOffset + 1];
            expectedSparse[^1] = 73;
            foreach (string root in new[] { fixture.Destination, fixture.Restored })
            {
                Check(File.ReadAllBytes(Path.Combine(root, BatchFixture.SampleNames[0])).AsSpan().SequenceEqual(compressible) &&
                    File.ReadAllBytes(Path.Combine(root, BatchFixture.SampleNames[2])).AsSpan().SequenceEqual(expectedSparse),
                    "Copy or restore changed logical bytes in a compressed or sparse file.");
            }
            Pass("compressed files and sparse holes contribute their logical length and restore with identical bytes");
        }
        return 0;
    }

    private static void SetFileLayout(FileStream file, uint control, byte[]? input)
    {
        if (!PathProbe.DeviceIoControl(file.SafeFileHandle, control, input, input?.Length ?? 0, null, 0, out _, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Setting the synthetic NTFS file layout failed.");
    }

    private static void SetExtendedAttribute(string path, bool present)
    {
        const uint writeEa = 0x10;
        const uint openReparsePointAndBackupSemantics = 0x02200000;
        using var handle = WindowsProbeNative.CreateFile(path, writeEa, FileShare.ReadWrite, 0, 3,
            openReparsePointAndBackupSemantics, 0);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Opening the synthetic extended attribute failed.");
        // FILE_FULL_EA_INFORMATION with one ASCII name, a NUL, and a one-byte value (empty to remove).
        byte[] data = [0, 0, 0, 0, 0, 2, present ? (byte)1 : (byte)0, 0, (byte)'F', (byte)'T', 0, 42];
        int status = NtSetEaFile(handle, out _, data, present ? data.Length : data.Length - 1);
        if (status != 0)
            throw new IOException($"Setting the synthetic extended attribute failed with NTSTATUS 0x{status:X8}.");
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetEaFile(SafeFileHandle file, out WindowsProbeNative.IoStatusBlock status, byte[] data, int size);

    private static void Link(string alias, string target)
    {
        if (!CreateHardLink(alias, target, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Creating the synthetic hard link failed.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string alias, string target, nint security);

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
    private static void Pass(string message) => Console.WriteLine("PASS " + message + ".");
}
