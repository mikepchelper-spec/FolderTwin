using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CopyLab;

internal static class LabInstallation
{
    private const string ManifestName = "SHA256SUMS.txt";
    private const string MarkerName = ".foldertwin-install-v1";
    private static string AppsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderTwin-Lab", "app");

    internal static string EnsureWorker()
    {
        string source = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        using var sourceRoot = new DirectoryFence(source);
        if (!WindowsProbeNative.PlainFileExists(sourceRoot.Root, "coreclr.dll") ||
            !WindowsProbeNative.PlainFileExists(sourceRoot.Root, "PresentationFramework.dll"))
            throw new InvalidDataException("Para programar, abre la compilación portable con su runtime incluido.");
        using var manifest = WindowsProbeNative.OpenPlainFile(sourceRoot.Root, ManifestName, FileAccess.Read, FileShare.Read);
        byte[] document = ReadManifestBytes(manifest);
        byte[] fingerprint = SHA256.HashData(document);
        var files = ParseManifest(document);
        foreach (string required in new[] { "CopyLab.exe", "CopyLab.dll", "CopyProbe.dll", "coreclr.dll", "PresentationFramework.dll" })
            if (!files.ContainsKey(required))
                throw new InvalidDataException("El paquete no contiene todos los archivos del ejecutor.");
        string name = Convert.ToHexStringLower(fingerprint);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        using var localRoot = new DirectoryFence(local);
        if (new DriveInfo(Path.GetPathRoot(local)!).DriveType != DriveType.Fixed)
            throw new InvalidDataException("La instalación requiere almacenamiento fijo local.");
        using var application = WindowsProbeNative.OpenOrCreatePlainDirectory(localRoot.Root, "FolderTwin-Lab");
        using var apps = WindowsProbeNative.OpenOrCreatePlainDirectory(application, "app");
        using var installLock = WindowsProbeNative.OpenPlainFile(apps, name + ".lock", FileAccess.ReadWrite,
            FileShare.None, FileMode.OpenOrCreate);
        using var directory = WindowsProbeNative.OpenOrCreatePlainDirectory(apps, name);
        string target = Path.Combine(AppsRoot, name);
        var allowed = files.Keys.Append(ManifestName).Append(MarkerName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files.Keys)
            for (string? parent = Path.GetDirectoryName(file); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                allowed.Add(parent);
        var inventory = FolderInventory.Scan(target);
        if (!inventory.Complete || inventory.Entries.Any(entry => !allowed.Contains(entry.RelativePath)))
            throw new InvalidDataException("La carpeta de instalación contiene datos ajenos o requiere revisión.");
        foreach (var (relative, hash) in files)
        {
            string parent = Path.GetDirectoryName(relative) ?? "";
            FolderBatch.WithDirectories(directory, parent, () =>
            {
                string input = Path.Combine(source, relative);
                string output = Path.Combine(target, relative);
                using var origin = new DirectoryFence(source, input);
                using var destination = new DirectoryFence(target, output);
                if (!WindowsProbeNative.PlainFileExists(destination.Parent(output), Path.GetFileName(output)))
                {
                    using var from = WindowsProbeNative.OpenPlainFile(origin.Parent(input), input, FileAccess.Read, FileShare.Read);
                    using var to = WindowsProbeNative.OpenPlainFile(destination.Parent(output), output, FileAccess.Write,
                        FileShare.None, FileMode.CreateNew);
                    from.CopyTo(to);
                    to.Flush(flushToDisk: true);
                }
                using var verified = WindowsProbeNative.OpenPlainFile(destination.Parent(output), output, FileAccess.Read, FileShare.Read);
                if (!SHA256.HashData(verified).AsSpan().SequenceEqual(hash))
                    throw new InvalidDataException("Un archivo instalado no coincide con el manifiesto. Se conservó para revisión.");
                return true;
            });
        }
        WriteOrVerify(ManifestName, document);
        WriteOrVerify(MarkerName, fingerprint);
        return Path.Combine(target, "CopyLab.exe");

        void WriteOrVerify(string fileName, byte[] expected)
        {
            if (!WindowsProbeNative.PlainFileExists(directory, fileName))
            {
                using var output = WindowsProbeNative.OpenPlainFile(directory, fileName, FileAccess.Write, FileShare.None, FileMode.CreateNew);
                output.Write(expected);
                output.Flush(flushToDisk: true);
            }
            using var input = WindowsProbeNative.OpenPlainFile(directory, fileName, FileAccess.Read, FileShare.Read);
            if (input.Length != expected.Length)
                throw new InvalidDataException("El registro de instalación requiere revisión.");
            byte[] actual = new byte[expected.Length];
            input.ReadExactly(actual);
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("El registro de instalación no coincide con el paquete.");
        }
    }

    internal static bool IsInstalledWorker(string executable)
    {
        string? directory = Path.GetDirectoryName(executable);
        if (Path.GetFileName(executable) != "CopyLab.exe" || directory is null || Path.GetDirectoryName(directory) != AppsRoot)
            return false;
        string name = Path.GetFileName(directory);
        if (name.Length != 64 || name.AsSpan().ContainsAnyExcept("0123456789abcdef"))
            return false;
        using var root = new DirectoryFence(directory);
        using var manifest = WindowsProbeNative.OpenPlainFile(root.Root, ManifestName, FileAccess.Read, FileShare.Read);
        byte[] hash = SHA256.HashData(ReadManifestBytes(manifest));
        using var marker = WindowsProbeNative.OpenPlainFile(root.Root, MarkerName, FileAccess.Read, FileShare.Read);
        Span<byte> bytes = stackalloc byte[32];
        return name == Convert.ToHexStringLower(hash) &&
            marker.ReadAtLeast(bytes, bytes.Length, false) == bytes.Length && marker.ReadByte() == -1 && bytes.SequenceEqual(hash);
    }

    private static byte[] ReadManifestBytes(Stream input)
    {
        if (input.Length is < 1 or > 1024 * 1024)
            throw new InvalidDataException("El manifiesto del paquete tiene un tamaño no válido.");
        byte[] bytes = new byte[(int)input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static Dictionary<string, byte[]> ParseManifest(byte[] bytes)
    {
        string text = new UTF8Encoding(false, true).GetString(bytes);
        if (!text.EndsWith('\n') || text.Contains('\r'))
            throw new InvalidDataException("El manifiesto del paquete está incompleto.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text[..^1].Split('\n'))
        {
            if (line.Length < 67 || line[64..66] != "  " || line.AsSpan(0, 64).ContainsAnyExcept("0123456789abcdef"))
                throw new InvalidDataException("Una entrada del manifiesto no es válida.");
            string relative = line[66..].Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative))
                throw new InvalidDataException("El paquete contiene una ruta absoluta.");
            foreach (string part in relative.Split(Path.DirectorySeparatorChar))
            {
                DirectoryFence.CheckName(part);
                if (part is "." or ".." || part.StartsWith(FolderBatch.ControlPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("El paquete contiene una ruta reservada.");
            }
            if (relative.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
                !files.TryAdd(relative, Convert.FromHexString(line.AsSpan(0, 64))) || files.Count > 4096)
                throw new InvalidDataException("El paquete contiene entradas repetidas o excesivas.");
        }
        return files;
    }
}
