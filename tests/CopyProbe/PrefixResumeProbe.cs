using System.Security.Cryptography;

internal static class PrefixResumeProbe
{
    internal static int Run()
    {
        foreach (string scenario in new[] { "empty", "one", "block", "unaligned", "whole", "interrupted",
            "wrong-prefix", "oversized", "changed-source", "receipt-draft", "final-exists", "default-rejects" })
            CheckScenario(scenario);
        Console.WriteLine("PASS explicit bound prefix continuation validates the complete source and every existing byte, appends without truncation, survives another interruption and preserves conflicting data and receipt drafts; default copying still refuses existing files.");
        return 0;
    }

    private static void CheckScenario(string scenario)
    {
        var fixture = LabWorkspace.Create();
        try
        {
            string source = Directory.CreateDirectory(Path.Combine(fixture.Root, "source")).FullName;
            string target = Directory.CreateDirectory(Path.Combine(fixture.Root, "target")).FullName;
            byte[] content = new byte[256 * 1024 + 23];
            new Random(53).NextBytes(content);
            byte[] expected = SHA256.HashData(content);
            File.WriteAllBytes(Path.Combine(source, "data.bin"), content);
            using var roots = new LocalJobRoots(source, target);
            var binding = DestinationBinding.Register(roots.DestinationHandle);
            int length = scenario switch { "empty" => 0, "one" => 1, "block" => 64 * 1024,
                "whole" => content.Length, "oversized" => content.Length + 1, _ => 100003 };
            byte[] original = new byte[length];
            content.AsSpan(0, Math.Min(length, content.Length)).CopyTo(original);
            if (scenario == "wrong-prefix") original[^1] ^= 1;
            string temporary = Path.Combine(target, "data.tmp"), final = Path.Combine(target, "data.bin");
            File.WriteAllBytes(temporary, original);
            if (scenario == "changed-source") File.WriteAllText(Path.Combine(source, "data.bin"), "different source");
            if (scenario == "receipt-draft") File.WriteAllBytes(CopyRecovery.ReceiptDraftPath(temporary), [1, 2, 3]);
            if (scenario == "final-exists") File.WriteAllText(final, "conserve this destination");
            bool reject = scenario is "wrong-prefix" or "oversized" or "changed-source" or "receipt-draft" or "final-exists" or "default-rejects";
            bool interrupted = false;
            try
            {
                int writes = 0;
                VerifiedCopy.RunBound(roots, binding, "data.bin", "data.bin", "data.tmp", expected,
                    checkpoint: (step, _) =>
                    {
                        if (scenario == "interrupted" && step == CopyStep.Writing && ++writes == 2)
                        { interrupted = true; throw new IOException("Synthetic second capacity failure."); }
                    }, resumeUnverifiedPrefix: scenario != "default-rejects");
                if (reject) throw new InvalidOperationException("A conflicting prefix was accepted: " + scenario);
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                if (!reject && !interrupted) throw;
                if (reject)
                {
                    Check(File.ReadAllBytes(temporary).AsSpan().SequenceEqual(original), "A rejected partial was modified: " + scenario);
                    Check(!File.Exists(CopyRecovery.ReceiptPath(temporary)), "A rejected partial was marked verified.");
                    if (scenario == "receipt-draft")
                        Check(File.ReadAllBytes(CopyRecovery.ReceiptDraftPath(temporary)).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }),
                            "A receipt draft was changed.");
                    if (scenario == "final-exists") Check(File.ReadAllText(final) == "conserve this destination", "An existing destination was overwritten.");
                    else Check(!File.Exists(final), "A rejected partial was published.");
                    return;
                }
                Check(new FileInfo(temporary).Length > original.Length && !File.Exists(final), "The second interruption was not preserved.");
                VerifiedCopy.RunBound(roots, binding, "data.bin", "data.bin", "data.tmp", expected, resumeUnverifiedPrefix: true);
            }
            Check(!File.Exists(temporary) && File.ReadAllBytes(final).AsSpan().SequenceEqual(content) &&
                CopyRecovery.Recover(target, temporary, final, binding, expected) == RecoveryResult.AlreadyPublished,
                "Prefix continuation did not produce a verified complete copy: " + scenario);
        }
        finally
        {
            using (fixture.OpenRegisteredRoot()) { }
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
