using System.Security.Cryptography;
using System.Text;

// This experiment only creates its own synthetic files; it accepts no paths.
if (args.Length != 0)
{
    Console.Error.WriteLine("This experiment accepts no arguments.");
    return 2;
}

string root = Path.Combine(Path.GetTempPath(), "FolderTwin-Probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string source = Path.Combine(root, "source.bin");
    string temporary = Path.Combine(root, "backup.tmp");
    string backup = Path.Combine(root, "backup.bin");
    string restored = Path.Combine(root, "restored.bin");
    File.WriteAllBytes(source, Encoding.UTF8.GetBytes("FolderTwin: datos sintéticos — recuperación\n"));

    byte[] sourceHash;
    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
        input.Position = 0;
        sourceHash = SHA256.HashData(input);
    }

    using (var verify = File.OpenRead(temporary))
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, SHA256.HashData(verify)))
            throw new InvalidDataException("Backup verification failed.");

    File.Move(temporary, backup);
    File.Copy(backup, restored);
    using (var verify = File.OpenRead(restored))
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, SHA256.HashData(verify)))
            throw new InvalidDataException("Restore verification failed.");

    Console.WriteLine("Synthetic copy and restore verified with SHA-256.");
    return 0;
}
finally
{
    // Only files created with CreateNew in this uniquely named experiment are removed.
    foreach (string name in new[] { "source.bin", "backup.tmp", "backup.bin", "restored.bin" })
        File.Delete(Path.Combine(root, name));
    Directory.Delete(root);
}
