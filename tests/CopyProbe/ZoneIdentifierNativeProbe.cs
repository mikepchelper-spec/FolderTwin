using System.Security.Cryptography;

internal static class ZoneIdentifierNativeProbe
{
    internal static int Run()
    {
        var fixture = LabWorkspace.Create();
        try
        {
            byte[] body = RandomNumberGenerator.GetBytes(192 * 1024 + 13);
            foreach (byte[]? zone in new byte[]?[] { null, [], [0, 255, 13, 10, 127], RandomNumberGenerator.GetBytes(ZoneIdentifierStream.MaximumBytes) })
            {
                string source = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N") + ".bin");
                File.WriteAllBytes(source, body);
                if (zone is not null) File.WriteAllBytes(source + ":Zone.Identifier", zone);
                using (var fence = new DirectoryFence(fixture.Root, source))
                using (var input = WindowsProbeNative.OpenPlainFile(fence.Parent(source), source, FileAccess.Read, FileShare.Read))
                {
                    input.Position = 19;
                    byte[]? observed = ZoneIdentifierStream.Read(input.SafeFileHandle);
                    Check((zone is null && observed is null) || zone is not null && observed is not null && zone.AsSpan().SequenceEqual(observed),
                        "Read did not preserve the opaque stream.");
                    Check(input.Position == 19 && input.ReadByte() == body[19], "Read moved the primary data position.");
                }
                Check(File.ReadAllBytes(source).AsSpan().SequenceEqual(body), "Read changed principal data.");
                if (zone is null) continue;
                string output = Path.Combine(fixture.Root, Guid.NewGuid().ToString("N") + ".bin");
                using (var fence = new DirectoryFence(fixture.Root, output))
                using (var file = WindowsProbeNative.OpenPlainFile(fence.Parent(output), output, FileAccess.Write, FileShare.None, FileMode.CreateNew))
                {
                    file.Write(body);
                    file.Flush();
                    file.Position = 23;
                    ZoneIdentifierStream.Write(file.SafeFileHandle, zone);
                    Check(file.Position == 23, "Write moved the principal data position.");
                    file.Flush(flushToDisk: true);
                }
                Check(File.ReadAllBytes(output).AsSpan().SequenceEqual(body) && File.ReadAllBytes(output + ":Zone.Identifier").AsSpan().SequenceEqual(zone),
                    "Write changed principal data or did not preserve Zone.Identifier.");
            }
            string invalid = Path.Combine(fixture.Root, "invalid.bin");
            File.WriteAllBytes(invalid, body);
            File.WriteAllBytes(invalid + ":Zone.Identifier", new byte[ZoneIdentifierStream.MaximumBytes + 1]);
            using (var fence = new DirectoryFence(fixture.Root, invalid))
            using (var file = WindowsProbeNative.OpenPlainFile(fence.Parent(invalid), invalid, FileAccess.Read, FileShare.Read))
            {
                for (int index = 0; index < 8; index++)
                {
                    file.Position = 31;
                    Reject(() => ZoneIdentifierStream.Read(file.SafeFileHandle));
                    Check(file.Position == 31 && file.ReadByte() == body[31], "Rejected read lost the data position.");
                }
                Reject(() => ZoneIdentifierStream.Write(file.SafeFileHandle, [1]));
            }
            Check(new FileInfo(invalid + ":Zone.Identifier").Length == ZoneIdentifierStream.MaximumBytes + 1,
                "A rejected write changed the read-only source mark.");
            File.Delete(invalid + ":Zone.Identifier");
            File.WriteAllBytes(invalid + ":other", [1]);
            using (var fence = new DirectoryFence(fixture.Root, invalid))
            using (var file = WindowsProbeNative.OpenPlainFile(fence.Parent(invalid), invalid, FileAccess.Read, FileShare.Read))
                Reject(() => ZoneIdentifierStream.Read(file.SafeFileHandle));
            File.Delete(invalid + ":other");
            File.WriteAllBytes(invalid + ":Zone.Identifier", [4, 5]);
            using (var fence = new DirectoryFence(fixture.Root, invalid))
            using (var file = WindowsProbeNative.OpenPlainFile(fence.Parent(invalid), invalid, FileAccess.Read, FileShare.Read))
                Check(ZoneIdentifierStream.Read(file.SafeFileHandle)!.AsSpan().SequenceEqual(new byte[] { 4, 5 }), "Read did not recover after rejected streams.");
            using (var fence = new DirectoryFence(fixture.Root, invalid))
            using (var file = WindowsProbeNative.OpenPlainFile(fence.Parent(invalid), invalid, FileAccess.ReadWrite, FileShare.None))
                foreach (byte[] mark in new byte[][] { new byte[ZoneIdentifierStream.MaximumBytes], [6], [] })
                {
                    ZoneIdentifierStream.Write(file.SafeFileHandle, mark);
                    Check(ZoneIdentifierStream.Read(file.SafeFileHandle)!.AsSpan().SequenceEqual(mark),
                        "Replacing a longer Zone.Identifier retained previous bytes.");
                }
            Console.WriteLine("PASS Zone.Identifier handle read/write: absent, empty, opaque bytes, 16 KiB, oversized/unknown rejection, data and file-position preservation, repeated failures, read-only source and exclusive write-only output.");
            return 0;
        }
        finally
        {
            using (fixture.OpenRegisteredRoot()) { }
            if (Path.GetDirectoryName(fixture.Root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
                throw new IOException("Refusing cleanup outside synthetic temp parent.");
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void Reject(Action operation)
    {
        try { operation(); }
        catch (Exception error) when (VersionJob.Expected(error)) { return; }
        throw new InvalidDataException("Unsupported stream operation was accepted.");
    }
}
