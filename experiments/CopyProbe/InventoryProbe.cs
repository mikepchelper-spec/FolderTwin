using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class InventoryProbe
{
    internal static int Run()
    {
        string fixture = Directory.CreateTempSubdirectory("FolderTwin-Inventory-").FullName;
        string root = Path.Combine(fixture, "root");
        string nested = Path.Combine(root, "carpeta á漢字🧪");
        string empty = Path.Combine(root, "empty");
        string outside = Path.Combine(fixture, "outside");
        string junction = Path.Combine(root, "junction");
        string cycle = Path.Combine(root, "cycle");
        var files = new List<string>();
        try
        {
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(empty);
            Directory.CreateDirectory(outside);
            string source = Write(Path.Combine(root, "hidden.bin"), new byte[17]);
            File.SetAttributes(source, FileAttributes.Hidden);
            Write(Path.Combine(nested, "archivo ñ.bin"), new byte[65_537]);
            Write(Path.Combine(outside, "sentinel.bin"), "untouched sentinel"u8.ToArray());
            for (int index = 0; index < 700; index++)
                Write(Path.Combine(root, $"{index:D4}-nombre-sintetico-que-ocupa-espacio-en-la-pagina-del-directorio.bin"), []);

            byte[] originalHash = SHA256.HashData(File.ReadAllBytes(source));
            using (var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var result = FolderInventory.Scan(root);
                Check(result.Complete && result.FileCount == 702 && result.Bytes == 65_554 &&
                    result.Entries.Count(entry => entry.IsDirectory) == 2, "The complete inventory has incorrect totals.");
                Check(result.Entries.Any(entry => entry.RelativePath == Path.Combine(Path.GetFileName(nested), "archivo ñ.bin")),
                    "The Unicode relative path changed.");
            }
            Check(SHA256.HashData(File.ReadAllBytes(source)).AsSpan().SequenceEqual(originalHash), "Inventory changed the source.");
            Console.WriteLine("PASS multipage metadata inventory includes hidden files, Unicode, empty directories, and an exclusively open file.");

            var emptyResult = FolderInventory.Scan(empty);
            var missingResult = FolderInventory.Scan(Path.Combine(root, "missing"));
            Check(emptyResult.Complete && emptyResult.Entries.Length == 0 && !missingResult.Complete &&
                missingResult.Issues.Any(issue => issue.Kind == InventoryIssueKind.ReadFailure), "An absent root was mistaken for an empty source.");
            Console.WriteLine("PASS an empty source is distinguished from an unavailable source.");

            FileAttributes emptyAttributes = File.GetAttributes(empty);
            try
            {
                File.SetAttributes(empty, emptyAttributes | FileAttributes.Offline);
                var offline = FolderInventory.Scan(empty);
                Check(!offline.Complete && offline.Entries.Length == 0, "An offline root was reported as empty.");
                Check(!FolderInventory.Scan(root).Complete, "An offline subtree was reported as complete.");
            }
            finally { File.SetAttributes(empty, emptyAttributes); }
            Console.WriteLine("PASS an offline root and an offline subtree cannot produce a complete inventory.");

            using (var blocked = WindowsProbeNative.CreateFile(nested, 0x01 | 0x80, FileShare.None, 0, 3, 0x02200000, 0))
            {
                if (blocked.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                var partial = FolderInventory.Scan(root);
                Check(!partial.Complete && partial.Issues.Any(issue => issue.RelativePath == Path.GetFileName(nested) &&
                    issue.Kind == InventoryIssueKind.ReadFailure), "An unreadable subtree was silently omitted.");
            }
            var injected = FolderInventory.Scan(root, beforeDirectory: relative =>
            {
                if (relative == Path.GetFileName(nested))
                    throw new IOException("Injected enumeration failure.");
            });
            Check(!injected.Complete && injected.FileCount == 701, "An enumeration failure was reported as complete.");
            Console.WriteLine("PASS an inaccessible subtree and an injected enumeration failure leave an explicit partial inventory.");

            var limited = FolderInventory.Scan(root, maximumEntries: 5);
            var shallow = FolderInventory.Scan(root, maximumDepth: 0);
            using var cancellation = new CancellationTokenSource();
            var cancelled = FolderInventory.Scan(root, cancellationToken: cancellation.Token, beforeDirectory: relative =>
            {
                if (relative == Path.GetFileName(nested))
                    cancellation.Cancel();
            });
            var alreadyCancelled = FolderInventory.Scan(root, cancellationToken: cancellation.Token);
            Check(!limited.Complete && limited.Entries.Length <= 5 && limited.Issues.Any(issue => issue.Kind == InventoryIssueKind.EntryLimit) &&
                !shallow.Complete && shallow.Issues.Any(issue => issue.Kind == InventoryIssueKind.DepthLimit) &&
                !cancelled.Complete && cancelled.Issues.Any(issue => issue.Kind == InventoryIssueKind.Cancelled) &&
                !alreadyCancelled.Complete && alreadyCancelled.Entries.Length == 0, "A bounded or cancelled inventory was reported as complete.");
            Console.WriteLine("PASS entry/depth limits and cancellation cannot become a complete inventory.");

            PathProbe.CreateJunction(junction, outside);
            PathProbe.CreateJunction(cycle, root);
            var linked = FolderInventory.Scan(root);
            Check(!linked.Complete && linked.FileCount == 702 && linked.Issues.Count(issue => issue.Kind == InventoryIssueKind.ReparsePoint) == 2 &&
                !linked.Entries.Any(entry => entry.RelativePath.Contains("sentinel", StringComparison.Ordinal)), "Inventory followed a junction or cycle.");
            Console.WriteLine("PASS junctions and a directory cycle are reported without following their targets.");

            var converted = FolderInventory.Scan(empty, beforeDirectory: _ => PathProbe.CopyJunctionData(junction, empty));
            Check(!converted.Complete && converted.Entries.Length == 0 &&
                converted.Issues.Any(issue => issue.Kind == InventoryIssueKind.ReadFailure),
                "A directory converted to a junction after opening redirected enumeration.");
            Directory.Delete(empty);
            Directory.CreateDirectory(empty);
            Console.WriteLine("PASS a directory converted to a junction after opening cannot redirect inventory.");

            string unsupported = @"\\?\" + Path.Combine(root, "unsupported.");
            Write(unsupported, []);
            var special = FolderInventory.Scan(root);
            Check(!special.Complete && special.Issues.Any(issue => issue.RelativePath == "unsupported." &&
                issue.Kind == InventoryIssueKind.UnsupportedName), "An unsupported filename was normalized or silently omitted.");
            foreach (FileAttributes attributes in new[] { FileAttributes.Offline, (FileAttributes)0x00040000, (FileAttributes)0x00400000 })
                Check(FolderInventory.Omission(attributes) == InventoryIssueKind.Unavailable, "Recall/offline metadata was accepted.");
            Check(FolderInventory.Omission(FileAttributes.Encrypted) == InventoryIssueKind.Encrypted,
                "Encrypted metadata was accepted.");
            Console.WriteLine("PASS unsupported names are explicit; synthetic offline, recall, and encrypted attribute policies reject those entries.");
            string invalidUnicode = Path.Combine(root, "invalid-\ud800.bin");
            files.Add(invalidUnicode);
            using (var directory = new DirectoryFence(root))
            using (var file = WindowsProbeNative.OpenPlainFile(directory.Root, invalidUnicode, FileAccess.Write,
                FileShare.None, FileMode.CreateNew))
            {
            }
            var invalidName = FolderInventory.Scan(root);
            Check(!invalidName.Complete && invalidName.Issues.Any(issue => issue.Kind == InventoryIssueKind.ReadFailure),
                "An invalid UTF-16 name was silently replaced during decoding.");
            Console.WriteLine("PASS an invalid UTF-16 name leaves the directory inventory incomplete instead of changing its name.");
            Check(File.ReadAllBytes(Path.Combine(outside, "sentinel.bin")).AsSpan().SequenceEqual("untouched sentinel"u8),
                "The outside sentinel changed.");
            return 0;
        }
        finally
        {
            foreach (string link in new[] { junction, cycle })
                if (Directory.Exists(link))
                    Directory.Delete(link);
            foreach (string file in files)
            {
                if (File.Exists(file))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
            }
            foreach (string directory in new[] { nested, empty, root, outside, fixture })
                if (Directory.Exists(directory))
                    Directory.Delete(directory);
        }

        string Write(string file, byte[] bytes)
        {
            files.Add(file);
            File.WriteAllBytes(file, bytes);
            return file;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
