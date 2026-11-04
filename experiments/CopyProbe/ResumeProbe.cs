using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ResumeProbe
{
    internal static int Run()
    {
        foreach (string boundary in new[] { "verified", "published" })
        {
            using var fixture = Prepared();
            KillAtCheckpoint(fixture.Root, boundary);
            fixture.ForgetPlan();
            string absent = Path.Combine(fixture.Root, "Origen ausente");
            Directory.Move(fixture.Source, absent);
            try
            {
                RunChild(fixture.Root, "partial", 1);
                Check(!File.Exists(Path.Combine(fixture.Destination, BatchCatalog.CommitName)), "A partial batch was confirmed.");
            }
            finally { Directory.Move(absent, fixture.Source); }
            RunChild(fixture.Root, "complete", 3);
            Check(fixture.Restore().Complete, "A resumed batch could not restore.");
            fixture.CheckRestored();
        }
        Pass("after killing the worker, another process recovers verified data without the source and completes the remaining files when it returns");

        using (var fixture = Prepared())
        {
            var plan = PendingBatch.Read(fixture.Root, fixture.Destination);
            KillAtCheckpoint(fixture.Root, "unverified");
            string temporary = FolderBatch.TemporaryPath(fixture.Destination, plan.Files[0].RelativePath);
            byte[] before = File.ReadAllBytes(temporary);
            string absent = Path.Combine(fixture.Root, "Origen ausente");
            Directory.Move(fixture.Source, absent);
            try
            {
                var missingSource = PendingBatch.Resume(fixture.Root, fixture.Destination);
                Check(!missingSource.Complete && missingSource.VerifiedFiles == 0 &&
                    missingSource.Entries.Any(entry => entry.RelativePath == plan.Files[0].RelativePath &&
                        entry.Detail?.Contains("sin verificar", StringComparison.Ordinal) == true),
                    "Unverified data was incorrectly reported as only needing the source.");
            }
            finally { Directory.Move(absent, fixture.Source); }
            RunChild(fixture.Root, "partial", 2);
            Check(File.ReadAllBytes(temporary).AsSpan().SequenceEqual(before) &&
                !File.Exists(Path.Combine(fixture.Destination, plan.Files[0].RelativePath)) &&
                !File.Exists(Path.Combine(fixture.Destination, BatchCatalog.CommitName)), "An unverified temporary was discarded or published.");
            Pass("a real process termination during writing preserves the unverified temporary for review");
        }
        using (var fixture = Prepared())
        {
            Check(FolderBatch.Run(fixture.Plan!).Complete, "The stored-data setup failed.");
            fixture.ForgetPlan();
            string absent = Path.Combine(fixture.Root, "Origen ausente");
            Directory.Move(fixture.Source, absent);
            try
            {
                RunChild(fixture.Root, "complete", 3);
                Check(fixture.Restore().Complete, "Stored data did not confirm without the source.");
                fixture.CheckRestored();
            }
            finally { Directory.Move(absent, fixture.Source); }
            Pass("all previously stored files can be reverified and confirmed without opening the original source");
        }
        using (var fixture = Prepared())
        {
            KillAtCheckpoint(fixture.Root, "published");
            string original = Path.Combine(fixture.Root, "Origen original");
            Directory.Move(fixture.Source, original);
            try
            {
                Directory.CreateDirectory(fixture.Source);
                foreach (string relative in BatchFixture.DirectoryNames)
                    Directory.CreateDirectory(Path.Combine(fixture.Source, relative));
                foreach (string relative in BatchFixture.SampleNames)
                    File.Copy(Path.Combine(original, relative), Path.Combine(fixture.Source, relative));
                var partial = PendingBatch.Resume(fixture.Root, fixture.Destination);
                Check(!partial.Complete && partial.VerifiedFiles == 1 && partial.Entries.Where(entry => !entry.Succeeded)
                    .Any(entry => entry.Detail?.Contains("identidad", StringComparison.Ordinal) == true),
                    "A substituted source with identical contents was accepted.");
            }
            finally
            {
                foreach (string relative in BatchFixture.SampleNames)
                    File.Delete(Path.Combine(fixture.Source, relative));
                foreach (string relative in BatchFixture.DirectoryNames.Reverse())
                    Directory.Delete(Path.Combine(fixture.Source, relative));
                Directory.Delete(fixture.Source);
                Directory.Move(original, fixture.Source);
            }
            Pass("an identical replacement source cannot authorize copying the remaining files");
        }
        foreach (PendingStep boundary in Enum.GetValues<PendingStep>())
        {
            using var fixture = new BatchFixture();
            var plan = fixture.Analyze();
            bool reached = false;
            try
            {
                PendingBatch.Save(plan, step =>
                {
                    if (step == boundary) { reached = true; throw new IOException("Injected pending save interruption."); }
                });
                throw new InvalidOperationException("The pending checkpoint was not reached.");
            }
            catch (IOException) when (reached) { }
            if (boundary == PendingStep.LocationPublished)
                _ = PendingBatch.Read(fixture.Root, fixture.Destination);
            else
                Rejected(() => PendingBatch.Read(fixture.Root, fixture.Destination));
            Check(!File.Exists(fixture.CopyFile), "Saving intent copied data before both files published.");
            PendingBatch.Save(plan);
            Check(PendingBatch.Resume(fixture.Root, fixture.Destination).Complete, "The pending save did not recover.");
        }
        Pass("interruption at every descriptor transition leaves execution unselected until both files validate; retry is idempotent");

        using (var fixture = Prepared())
        {
            string location = Path.Combine(fixture.Destination, PendingBatch.LocationName);
            byte[] original = File.ReadAllBytes(location);
            foreach (Action<JsonObject> mutation in new Action<JsonObject>[]
            {
                payload => payload["version"] = 2,
                payload => payload["sourceRelative"] = @"..\outside",
                payload => payload["sourceRelative"] = @"C:\outside",
                payload => payload["sourceRelative"] = "Destino",
                payload => payload["sourceIdentity"] = Convert.ToBase64String([1]),
                payload => payload["fixtureIdentity"] = Convert.ToBase64String(new byte[24]),
                payload => payload["metadataHash"] = Convert.ToBase64String(new byte[32])
            })
            {
                var envelope = JsonNode.Parse(original)!.AsObject();
                mutation(envelope["payload"]!.AsObject());
                envelope["checksum"] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(envelope["payload"]!.ToJsonString())));
                File.WriteAllText(location, envelope.ToJsonString());
                Rejected(() => PendingBatch.Read(fixture.Root, fixture.Destination));
            }
            File.WriteAllBytes(location, original[..^1]);
            var bad = PendingBatch.Resume(fixture.Root, fixture.Destination);
            Check(!bad.Complete && !File.Exists(fixture.CopyFile), "A damaged descriptor started writing data.");
            File.WriteAllBytes(location, original);
            string metadata = Path.Combine(fixture.Destination, PendingBatch.MetadataName);
            byte[] data = File.ReadAllBytes(metadata);
            File.WriteAllBytes(metadata, data[..^1]);
            Rejected(() => PendingBatch.Read(fixture.Root, fixture.Destination));
            Check(!PendingBatch.Start(fixture.Plan!).Complete && File.ReadAllBytes(metadata).AsSpan().SequenceEqual(data.AsSpan(0, data.Length - 1)),
                "A damaged descriptor was silently replaced.");
            Pass("malformed, altered, escaped, mismatched, and foreign-scope descriptors fail before data writes and remain intact");
        }
        using (var fixture = Prepared())
        {
            string marker = Path.Combine(fixture.Restored, DestinationBinding.MarkerName);
            byte[] original = File.ReadAllBytes(marker);
            File.WriteAllBytes(marker, fixture.DestinationBinding.Marker.ToByteArray());
            try
            {
                foreach (string name in new[] { PendingBatch.MetadataName, PendingBatch.LocationName, FolderBatch.LockName })
                    File.Copy(Path.Combine(fixture.Destination, name), Path.Combine(fixture.Restored, name));
                Rejected(() => PendingBatch.Read(fixture.Root, fixture.Restored));
                Check(!File.Exists(fixture.RestoredFile), "A copied pending descriptor authorized a different destination.");
            }
            finally { File.WriteAllBytes(marker, original); }
            Pass("copying the pending descriptor and marker cannot authorize another destination directory");
        }
        return 0;
    }

    internal static int InterruptChild(string root, string boundary)
    {
        BatchFixture.ValidateRoot(root);
        CopyStep checkpoint = boundary switch { "verified" => CopyStep.Verified, "published" => CopyStep.Published,
            "unverified" => CopyStep.Writing, _ => throw new ArgumentException("Unknown interruption checkpoint.") };
        var plan = PendingBatch.Read(root, Path.Combine(root, "Destino"));
        var result = FolderBatch.Run(plan, checkpoint: (relative, step, _) =>
        {
            if (relative == plan.Files[0].RelativePath && step == checkpoint)
            {
                Console.WriteLine("READY");
                Console.Out.Flush();
                Console.ReadLine();
            }
        }, resume: true);
        return result.Complete ? 0 : 1;
    }

    internal static int ResumeChild(string root, string outcome, string count)
    {
        BatchFixture.ValidateRoot(root);
        if (outcome is not ("complete" or "partial") || !int.TryParse(count, out int expected) || expected is < 0 or > 3)
            throw new ArgumentException("Invalid expected test outcome.");
        var result = PendingBatch.Resume(root, Path.Combine(root, "Destino"));
        Check(result.Complete == (outcome == "complete") && result.VerifiedFiles == expected,
            "Independent resume did not match the expected state.");
        Console.WriteLine("PASS independent pending resume.");
        return 0;
    }

    private static BatchFixture Prepared()
    {
        var fixture = new BatchFixture();
        try { PendingBatch.Save(fixture.Analyze()); return fixture; }
        catch { fixture.Dispose(); throw; }
    }
    private static ProcessStartInfo Start(string flag, string root, params string[] arguments)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(flag);
        start.ArgumentList.Add(root);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
    private static void KillAtCheckpoint(string root, string boundary)
    {
        using var process = Process.Start(Start("--interrupt-pending-child", root, boundary)) ?? throw new InvalidOperationException("Could not start worker.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            string? ready = process.StandardOutput.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
            Check(ready == "READY", "The worker did not reach its checkpoint.");
            process.Kill();
            Check(process.WaitForExit(15_000), "The terminated worker did not exit.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(15_000); }
        }
    }
    private static void RunChild(string root, string outcome, int count)
    {
        using var process = Process.Start(Start("--resume-pending-child", root, outcome, count.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            ?? throw new InvalidOperationException("Could not start resume worker.");
        if (!process.WaitForExit(15_000))
        {
            process.Kill(); process.WaitForExit(15_000);
            throw new TimeoutException("Independent resume timed out.");
        }
        Check(process.ExitCode == 0 && process.StandardOutput.ReadToEnd().Contains("PASS independent pending resume.", StringComparison.Ordinal),
            "Independent resume failed. " + process.StandardError.ReadToEnd());
    }
    private static void Rejected(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or JsonException or System.ComponentModel.Win32Exception) { return; }
        throw new InvalidOperationException("An invalid pending descriptor was accepted.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
    private static void Pass(string message) => Console.WriteLine("PASS " + message + ".");
}
