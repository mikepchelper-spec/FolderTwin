using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

internal static class ObjectLayoutProbe
{
    internal static int Run(int count)
    {
        var workspace = LabWorkspace.Create();
        using var process = Process.GetCurrentProcess();
        try
        {
            using (var scope = workspace.OpenRegisteredRoot())
                LabWorkspace.RequireSpace(256L * 1024 * 1024 + 16L * 1024 * count,
                    LabWorkspace.AvailableSpace(scope.Root));
            Console.WriteLine($"Windows {Environment.OSVersion.Version}; .NET {Environment.Version}; {count} objects of eight bytes.");
            foreach (int levels in new[] { 1, 2 })
            {
                string root = Directory.CreateDirectory(Path.Combine(workspace.Root, $"layout-{levels}")).FullName;
                var buckets = new Dictionary<string, int>(StringComparer.Ordinal);
                Measure("create", () =>
                {
                    for (int index = 0; index < count; index++)
                    {
                        byte[] payload = Payload(index);
                        string digest = Convert.ToHexStringLower(SHA256.HashData(payload));
                        string bucket = Bucket(digest, levels);
                        if (!buckets.TryAdd(bucket, 1))
                            buckets[bucket]++;
                        else
                            Directory.CreateDirectory(Path.Combine(root, bucket));
                        using var file = new FileStream(Path.Combine(root, bucket, digest), FileMode.CreateNew,
                            FileAccess.Write, FileShare.None);
                        file.Write(payload);
                        if ((index + 1) % 100_000 == 0)
                            Console.WriteLine($"layout={levels}; created={index + 1}");
                    }
                });
                Measure("lookup", () =>
                {
                    int samples = Math.Min(4096, count);
                    for (int sample = 0; sample < samples; sample++)
                    {
                        int index = (int)((long)sample * count / samples);
                        byte[] payload = Payload(index);
                        string digest = Convert.ToHexStringLower(SHA256.HashData(payload));
                        if (!File.ReadAllBytes(Path.Combine(root, Bucket(digest, levels), digest)).AsSpan().SequenceEqual(payload))
                            throw new InvalidOperationException("The object lookup did not return its expected content.");
                    }
                });
                Measure("enumerate", () =>
                {
                    long observed = 0;
                    foreach (string bucket in buckets.Keys)
                    {
                        int found = Directory.EnumerateFiles(Path.Combine(root, bucket)).Count();
                        if (found != buckets[bucket])
                            throw new InvalidOperationException("The observed object distribution differed from the fixture.");
                        observed += found;
                    }
                    if (observed != count)
                        throw new InvalidOperationException("The layout lost objects during enumeration.");
                });
                int[] distribution = buckets.Values.Order().ToArray();
                Console.WriteLine($"PASS layout={levels}; occupied_buckets={buckets.Count}; median={distribution[distribution.Length / 2]}; max={distribution[^1]}.");
                // Each layout is an immediate child of this freshly created registered workspace.
                using (workspace.OpenRegisteredRoot()) { }
                workspace.CheckChild(root);
                Directory.Delete(root, recursive: true);

                void Measure(string phase, Action operation)
                {
                    var timer = Stopwatch.StartNew();
                    operation();
                    timer.Stop();
                    process.Refresh();
                    Console.WriteLine(FormattableString.Invariant(
                        $"layout={levels}; {phase}; seconds={timer.Elapsed.TotalSeconds:F3}; peak_working_set_bytes={process.PeakWorkingSet64}."));
                }
            }
            return 0;
        }
        finally
        {
            using (workspace.OpenRegisteredRoot()) { }
            Directory.Delete(workspace.Root, recursive: true);
        }
    }

    private static byte[] Payload(int index)
    {
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, index);
        return bytes;
    }

    private static string Bucket(string digest, int levels) =>
        levels == 1 ? digest[..2] : Path.Combine(digest[..2], digest[2..4]);
}
