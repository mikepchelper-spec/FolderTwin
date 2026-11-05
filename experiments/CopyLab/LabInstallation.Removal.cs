using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace CopyLab;

internal enum InstallRemovalStep { PlanPartial, Planned, FileRemoved, DirectoryRemoved, RootRemoved, CompletionPartial, Completed }
internal sealed record InstallRemovalReview(int Version, string Fingerprint, byte[] AppsIdentity, byte[] Identity, byte[] Manifest);

internal static partial class LabInstallation
{
    private const string RemovalsName = ".removals";
    private static readonly JsonSerializerOptions RemovalJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 8
    };

    internal static InstallRemovalReview ReviewRemovalAt(string installations, string fingerprint)
    {
        CheckFingerprint(fingerprint);
        using var apps = new DirectoryFence(installations);
        using var operation = WindowsProbeNative.OpenPlainFile(apps.Root, fingerprint + ".lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
        try
        {
            using var removals = WindowsProbeNative.OpenPlainDirectory(apps.Root, RemovalsName);
            using var journal = WindowsProbeNative.OpenPlainDirectory(removals, fingerprint);
            if (WindowsProbeNative.PlainFileExists(journal, "plan")) return ReadRemoval(journal, fingerprint, apps.Root);
        }
        catch (FileNotFoundException) { }
        using var root = WindowsProbeNative.OpenPlainDirectory(apps.Root, fingerprint);
        using var manifest = WindowsProbeNative.OpenPlainFile(root, ManifestName, FileAccess.Read, FileShare.Read);
        byte[] document = ReadManifestBytes(manifest);
        RequireMarker(root, fingerprint, document);
        return new(1, fingerprint, WindowsProbeNative.DirectoryIdentity(apps.Root), WindowsProbeNative.DirectoryIdentity(root), document);
    }

    internal static void RemoveAt(string installations, InstallRemovalReview review, Action<InstallRemovalStep>? checkpoint = null)
    {
        CheckRemoval(review);
        using var apps = new DirectoryFence(installations);
        if (!WindowsProbeNative.DirectoryIdentity(apps.Root).AsSpan().SequenceEqual(review.AppsIdentity))
            throw new InvalidDataException("La carpeta de instalaciones cambió de identidad.");
        using var current = new DirectoryFence(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        if (WindowsProbeNative.DirectoryIdentity(current.Root).AsSpan().SequenceEqual(review.Identity))
            throw new InvalidDataException("Esta versión está abierta. Ejecuta su retirada desde otra copia portable de FolderTwin.");
        using var operation = WindowsProbeNative.OpenPlainFile(apps.Root, review.Fingerprint + ".lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
        using var removals = WindowsProbeNative.OpenOrCreatePlainDirectory(apps.Root, RemovalsName);
        using var journal = WindowsProbeNative.OpenOrCreatePlainDirectory(removals, review.Fingerprint);
        if (WindowsProbeNative.ReadDirectory(journal).Any(entry => entry.Name is not ("." or ".." or "plan" or "plan.tmp" or "complete" or "complete.tmp")))
            throw new InvalidDataException("El diario de retirada contiene datos ajenos.");
        bool started = WindowsProbeNative.PlainFileExists(journal, "plan");
        byte[] encoded = EncodeRemoval(review);
        if (started && !EncodeRemoval(ReadRemoval(journal, review.Fingerprint, apps.Root)).AsSpan().SequenceEqual(encoded))
            throw new InvalidDataException("La retirada pendiente pertenece a otra instalación.");
        SafeFileHandle root;
        try { root = WindowsProbeNative.OpenDirectoryForDeletion(apps.Root, review.Fingerprint); }
        catch (FileNotFoundException) when (started)
        {
            WriteRemovalRecord(journal, "complete", SHA256.HashData(encoded), () => checkpoint?.Invoke(InstallRemovalStep.CompletionPartial));
            checkpoint?.Invoke(InstallRemovalStep.Completed);
            return;
        }
        using (root)
        {
            if (!WindowsProbeNative.DirectoryIdentity(root).AsSpan().SequenceEqual(review.Identity))
                throw new InvalidDataException("La versión instalada fue sustituida. No se retira.");
            var files = ParseManifest(review.Manifest);
            var expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in files.Keys)
                for (string? parent = Path.GetDirectoryName(name); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                    expectedDirectories.Add(parent);
            var openFiles = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
            var openDirectories = new List<SafeFileHandle>();
            try
            {
                Visit(root, "");
                if (!started && !openFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(files.Keys.Append(ManifestName).Append(MarkerName)))
                    throw new InvalidDataException("La instalación está incompleta. No se inicia su retirada.");
                // The full deterministic plan is durable before the first deletion.
                WriteRemovalRecord(journal, "plan", encoded, () => checkpoint?.Invoke(InstallRemovalStep.PlanPartial));
                checkpoint?.Invoke(InstallRemovalStep.Planned);
                foreach (string name in openFiles.Keys.Where(name => name != ManifestName && name != MarkerName).ToArray()) RemoveFile(name);
                foreach (var directory in openDirectories.AsEnumerable().Reverse())
                {
                    WindowsProbeNative.DeleteEmptyDirectory(directory);
                    directory.Dispose();
                    checkpoint?.Invoke(InstallRemovalStep.DirectoryRemoved);
                }
                if (openFiles.ContainsKey(ManifestName)) RemoveFile(ManifestName);
                if (openFiles.ContainsKey(MarkerName)) RemoveFile(MarkerName);
                WindowsProbeNative.DeleteEmptyDirectory(root);
                checkpoint?.Invoke(InstallRemovalStep.RootRemoved);
            }
            finally
            {
                foreach (var file in openFiles.Values) file.Dispose();
                foreach (var directory in openDirectories) directory.Dispose();
            }

            void RemoveFile(string name)
            {
                WindowsProbeNative.DeletePlainFile(openFiles[name].SafeFileHandle);
                openFiles[name].Dispose();
                openFiles.Remove(name);
                checkpoint?.Invoke(InstallRemovalStep.FileRemoved);
            }
            void Visit(SafeFileHandle directory, string relative)
            {
                if (relative.Count(character => character == Path.DirectorySeparatorChar) >= 64)
                    throw new InvalidDataException("La instalación supera la profundidad admitida para una retirada controlada.");
                WindowsProbeNative.RequireSupportedData(directory, relative.Length == 0 ? "instalación" : relative);
                foreach (var entry in WindowsProbeNative.ReadDirectory(directory).Where(entry => entry.Name is not ("." or "..")).ToArray())
                {
                    DirectoryFence.CheckName(entry.Name);
                    string path = relative.Length == 0 ? entry.Name : Path.Combine(relative, entry.Name);
                    if (entry.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (!expectedDirectories.Contains(path)) throw new InvalidDataException("La instalación contiene una carpeta ajena. Se conserva.");
                        var child = WindowsProbeNative.OpenDirectoryForDeletion(directory, entry.Name);
                        openDirectories.Add(child);
                        Visit(child, path);
                    }
                    else
                    {
                        if (!files.ContainsKey(path) && path != ManifestName && path != MarkerName)
                            throw new InvalidDataException("La instalación contiene archivos ajenos. Se conservan.");
                        var file = WindowsProbeNative.OpenPlainFile(directory, entry.Name, FileAccess.Read, FileShare.None, rename: true);
                        openFiles.Add(path, file);
                        WindowsProbeNative.RequireSupportedData(file.SafeFileHandle, path);
                        byte[] expected = path == ManifestName ? SHA256.HashData(review.Manifest) :
                            path == MarkerName ? SHA256.HashData(SHA256.HashData(review.Manifest)) : files[path];
                        if (!SHA256.HashData(file).AsSpan().SequenceEqual(expected))
                            throw new InvalidDataException("Un archivo instalado cambió. Se conserva toda la versión para revisión.");
                    }
                }
            }
        }
        WriteRemovalRecord(journal, "complete", SHA256.HashData(encoded), () => checkpoint?.Invoke(InstallRemovalStep.CompletionPartial));
        checkpoint?.Invoke(InstallRemovalStep.Completed);
    }

    private static void RequireMarker(SafeFileHandle root, string fingerprint, byte[] document)
    {
        if (Convert.ToHexStringLower(SHA256.HashData(document)) != fingerprint)
            throw new InvalidDataException("El manifiesto no corresponde a la versión instalada.");
        _ = ParseManifest(document);
        using var marker = WindowsProbeNative.OpenPlainFile(root, MarkerName, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[32]; marker.ReadExactly(bytes);
        if (marker.ReadByte() != -1 || !bytes.AsSpan().SequenceEqual(SHA256.HashData(document)))
            throw new InvalidDataException("El marcador no confirma esta instalación.");
    }
    private static void CheckFingerprint(string value)
    {
        if (value.Length != 64 || value.AsSpan().ContainsAnyExcept("0123456789abcdef"))
            throw new InvalidDataException("Identificador de instalación no válido.");
    }
    private static void CheckRemoval(InstallRemovalReview review)
    {
        CheckFingerprint(review.Fingerprint);
        if (review.Version != 1 || review.AppsIdentity is not { Length: 24 } || review.Identity is not { Length: 24 } ||
            review.Manifest.Length is < 1 or > 1024 * 1024 || Convert.ToHexStringLower(SHA256.HashData(review.Manifest)) != review.Fingerprint)
            throw new InvalidDataException("La revisión de retirada está dañada.");
        _ = ParseManifest(review.Manifest);
    }
    private static byte[] EncodeRemoval(InstallRemovalReview review)
    {
        CheckRemoval(review);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(review, RemovalJson);
        return [.. SHA256.HashData(body), .. body];
    }
    private static InstallRemovalReview ReadRemoval(SafeFileHandle removals, string name, SafeFileHandle apps)
    {
        using var file = WindowsProbeNative.OpenPlainFile(removals, "plan", FileAccess.Read, FileShare.Read);
        if (file.Length is < 33 or > 2 * 1024 * 1024) throw new InvalidDataException("El diario de retirada está incompleto.");
        byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        if (!SHA256.HashData(bytes.AsSpan(32)).AsSpan().SequenceEqual(bytes.AsSpan(0, 32))) throw new InvalidDataException("El diario de retirada está dañado.");
        var review = JsonSerializer.Deserialize<InstallRemovalReview>(bytes.AsSpan(32), RemovalJson) ?? throw new InvalidDataException("Falta el diario de retirada.");
        CheckRemoval(review);
        if (review.Fingerprint != name || !WindowsProbeNative.DirectoryIdentity(apps).AsSpan().SequenceEqual(review.AppsIdentity))
            throw new InvalidDataException("El diario de retirada corresponde a otra carpeta.");
        return review;
    }
    private static void RequireNoRemoval(SafeFileHandle apps, string fingerprint)
    {
        SafeFileHandle removals;
        try { removals = WindowsProbeNative.OpenPlainDirectory(apps, RemovalsName); }
        catch (FileNotFoundException) { return; }
        using (removals)
        {
            SafeFileHandle journal;
            try { journal = WindowsProbeNative.OpenDirectoryForDeletion(removals, fingerprint); }
            catch (FileNotFoundException) { return; }
            using (journal)
            {
                if (!WindowsProbeNative.PlainFileExists(journal, "plan"))
                {
                    if (WindowsProbeNative.ReadDirectory(journal).Any(entry => entry.Name is not ("." or ".." or "plan.tmp")))
                        throw new InvalidDataException("Hay datos desconocidos en un diario incompleto de retirada.");
                    return; // No deletion is authorized before the full plan is published.
                }
                var review = ReadRemoval(journal, fingerprint, apps);
                if (!WindowsProbeNative.ReadDirectory(journal).Where(entry => entry.Name is not ("." or "..")).Select(entry => entry.Name)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "plan", "complete" }))
                    throw new InvalidDataException("La retirada tiene estados incompletos o datos ajenos. Reanúdala antes de reinstalar.");
                using var complete = WindowsProbeNative.OpenPlainFile(journal, "complete", FileAccess.Read, FileShare.Read);
                byte[] receipt = new byte[32]; complete.ReadExactly(receipt);
                if (complete.ReadByte() != -1 || !receipt.AsSpan().SequenceEqual(SHA256.HashData(EncodeRemoval(review))))
                    throw new InvalidDataException("La retirada de esta versión está incompleta o dañada.");
                try
                {
                    using var remains = WindowsProbeNative.OpenPlainDirectory(apps, fingerprint);
                    throw new InvalidDataException("Queda una carpeta para una retirada confirmada. Se conserva para revisión.");
                }
                catch (FileNotFoundException) { }
                complete.Dispose();
                using var history = WindowsProbeNative.OpenOrCreatePlainDirectory(removals, "history");
                WindowsProbeNative.RenameDirectoryNew(journal, history, fingerprint + "-" + Guid.NewGuid().ToString("N"));
            }
        }
    }

    private static void WriteRemovalRecord(SafeFileHandle journal, string name, byte[] bytes, Action? partial = null)
    {
        if (!WindowsProbeNative.PlainFileExists(journal, name))
        {
            WriteOrVerifyRecord(journal, name + ".tmp", bytes, partial);
            using var temporary = WindowsProbeNative.OpenPlainFile(journal, name + ".tmp", FileAccess.Read, FileShare.None, rename: true);
            WindowsProbeNative.RenameNew(temporary.SafeFileHandle, journal, name);
        }
        using var file = WindowsProbeNative.OpenPlainFile(journal, name, FileAccess.Read, FileShare.Read);
        byte[] observed = new byte[bytes.Length]; file.ReadExactly(observed);
        if (file.ReadByte() != -1 || !observed.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("El diario no corresponde a la retirada revisada.");
    }
}
