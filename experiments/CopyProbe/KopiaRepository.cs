using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

internal sealed record KopiaPoint(string ManifestId, string RootId, DateTimeOffset Finished, long Bytes, int Files, int Directories,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Layout = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CatalogHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long CatalogBytes = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Generation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? Job = null)
{
    internal int UserFiles => Files - (Layout == 2 ? 1 : 0);
}
internal sealed record CloudRecoveryPoint(Guid Job, string Generation, KopiaPoint Point);
internal sealed class CloudProviderException(CloudTransferIssue issue, string message) : IOException(message)
{
    internal CloudTransferIssue Issue { get; } = issue;
}
internal sealed class KopiaS3Credentials(string accessKey, string secretKey, string? sessionToken = null)
{
    internal string AccessKey { get; } = accessKey;
    internal string SecretKey { get; } = secretKey;
    internal string? SessionToken { get; } = sessionToken;
}

internal sealed partial class KopiaRepository(string executable, string config, string password, KopiaS3Credentials? s3 = null)
{
    private const int MaximumOutputCharacters = 16 * 1024 * 1024;

    internal async Task<string> IdentityAsync(CancellationToken token)
    {
        string json = await RunAsync(token, "repository", "status", "--json");
        return ParseResponse(json, ReadIdentity);
    }

    private static string ReadIdentity(JsonElement root)
    {
        if (!root.TryGetProperty("uniqueIDHex", out var id) || id.ValueKind != JsonValueKind.String ||
            id.GetString() is not { } value || !RetentionJournal.IsDigest(value))
            throw new InvalidDataException("Kopia devolvió una identidad de repositorio no válida.");
        return value;
    }

    internal async Task<KopiaPoint?> FindAsync(Guid operation, CancellationToken token)
    {
        string tag = Tag(operation);
        string json = await RunAsync(token, "snapshot", "list", "--all", "--show-identical", "--json", "--tags", tag);
        return ParseResponse(json, root => ReadOperation(root, operation));
    }

    private static KopiaPoint? ReadOperation(JsonElement root, Guid operation)
    {
        KopiaPoint? found = null;
        foreach (var entry in root.EnumerateArray())
        {
            if (found is not null || entry.GetProperty("tags").GetProperty("tag:foldertwin").GetString() != operation.ToString("N"))
                throw new InvalidDataException("El repositorio devolvió puntos ambiguos o de otra operación.");
            found = ReadPoint(entry);
        }
        return found;
    }

    internal async Task<CloudRecoveryPoint[]> ListAsync(CancellationToken token)
    {
        string json = await RunAsync(token, "snapshot", "list", "--all", "--show-identical", "--json");
        return ParseResponse(json, ReadPoints);
    }

    private static CloudRecoveryPoint[] ReadPoints(JsonElement root)
    {
        var points = new Dictionary<(Guid, string), CloudRecoveryPoint>();
        foreach (var entry in root.EnumerateArray())
        {
            if (!entry.TryGetProperty("tags", out var tags) || tags.ValueKind == JsonValueKind.Null) continue;
            bool catalog = tags.TryGetProperty("tag:ft-v2-job", out var jobTag);
            if (catalog && tags.TryGetProperty("tag:ft-job", out _) || !catalog && !tags.TryGetProperty("tag:ft-job", out jobTag))
            {
                if (catalog) throw new InvalidDataException("Una fecha remota mezcla etiquetas de formatos distintos.");
                continue;
            }
            if (!Guid.TryParseExact(jobTag.GetString(), "N", out var job) || job == Guid.Empty ||
                !tags.TryGetProperty(catalog ? "tag:ft-v2-generation" : "tag:ft-generation", out var generationTag) || generationTag.GetString() is not { } generation)
                throw new InvalidDataException("El repositorio contiene una fecha de FolderTwin no válida.");
            _ = VersionStore.GenerationTime(generation);
            if (!points.TryAdd((job, generation), new(job, generation, ReadPoint(entry))))
                throw new InvalidDataException("El repositorio contiene dos instantáneas para la misma fecha.");
        }
        return points.Values.OrderByDescending(point => point.Generation, StringComparer.Ordinal).ToArray();
    }

    private static T ParseResponse<T>(string json, Func<JsonElement, T> read) => ReadResponse(() =>
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        return read(document.RootElement);
    });

    private static T ReadResponse<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("La respuesta de Kopia no tiene el formato esperado.");
        }
    }

    internal static KopiaPoint ReadPoint(JsonElement entry) => ReadResponse(() => ReadPointFields(entry));

    private static KopiaPoint ReadPointFields(JsonElement entry)
    {
        var root = entry.GetProperty("rootEntry");
        var summary = root.GetProperty("summ");
        var stats = entry.GetProperty("stats");
        if (root.GetProperty("type").GetString() != "d" || summary.GetProperty("numFailed").GetInt32() != 0 ||
            summary.GetProperty("symlinks").GetInt32() != 0 || stats.GetProperty("errorCount").GetInt32() != 0 ||
            stats.GetProperty("ignoredErrorCount").GetInt32() != 0 || stats.GetProperty("excludedFileCount").GetInt32() != 0 ||
            stats.GetProperty("excludedDirCount").GetInt32() != 0)
            throw new InvalidDataException("Kopia devolvió un punto incompleto o con exclusiones.");
        bool catalog = entry.GetProperty("tags").TryGetProperty("tag:ft-v2-job", out _);
        var pointTags = entry.GetProperty("tags");
        if (catalog ? pointTags.TryGetProperty("tag:ft-job", out _) || pointTags.TryGetProperty("tag:ft-generation", out _) :
            pointTags.TryGetProperty("tag:ft-v2-generation", out _) || pointTags.TryGetProperty("tag:ft-catalog", out _) ||
            pointTags.TryGetProperty("tag:ft-catalog-bytes", out _))
            throw new InvalidDataException("El punto remoto mezcla etiquetas de formatos distintos.");
        string? generation = null, hash = null;
        Guid? job = null;
        long catalogBytes = 0;
        if (catalog)
        {
            var tags = entry.GetProperty("tags");
            if (!Guid.TryParseExact(tags.GetProperty("tag:ft-v2-job").GetString(), "N", out var parsedJob) || parsedJob == Guid.Empty)
                throw new InvalidDataException("Falta el trabajo del catálogo remoto.");
            job = parsedJob;
            generation = tags.GetProperty("tag:ft-v2-generation").GetString();
            if (generation is null) throw new InvalidDataException("Falta la fecha del catálogo remoto.");
            _ = VersionStore.GenerationTime(generation);
            hash = tags.GetProperty("tag:ft-catalog").GetString();
            if (!RetentionJournal.IsDigest(hash ?? "") || !long.TryParse(tags.GetProperty("tag:ft-catalog-bytes").GetString(),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out catalogBytes) ||
                catalogBytes is < 1 or > BatchCatalog.VersionMaximumBytes)
                throw new InvalidDataException("El catálogo remoto no tiene una huella y tamaño válidos.");
        }
        var point = new KopiaPoint(Identifier(entry.GetProperty("id").GetString(), ""), Identifier(root.GetProperty("obj").GetString(), "k"),
            entry.GetProperty("endTime").GetDateTimeOffset(), summary.GetProperty("size").GetInt64(),
            summary.GetProperty("files").GetInt32(), summary.GetProperty("dirs").GetInt32(), catalog ? 2 : 0, hash, catalogBytes, generation, job);
        if (point.Bytes < point.CatalogBytes || point.Files < (catalog ? 1 : 0) || point.Directories < (catalog ? 2 : 1) ||
            (long)point.Files + point.Directories > BatchCatalog.VersionMaximumEntries + (catalog ? 3L : 1L))
            throw new InvalidDataException("El inventario remoto supera los límites admitidos o contiene totales inválidos.");
        return point;
    }

    internal async Task<KopiaPoint> PublishAsync(Guid operation, CloudPoint point, string payload, CancellationToken token,
        Action? afterUpload = null)
    {
        var existing = await FindAsync(operation, token);
        if (existing is null)
        {
            string[] pointTags = point.Layout == 2 ?
                ["--tags", $"ft-v2-job:{point.Job:N}", "--tags", $"ft-v2-generation:{point.Generation}",
                 "--tags", $"ft-catalog:{point.CatalogHash}", "--tags", "ft-catalog-bytes:" + point.CatalogBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)] :
                ["--tags", $"ft-job:{point.Job:N}", "--tags", $"ft-generation:{point.Generation}"];
            await RunAsync(token, ["snapshot", "create", "--json", "--pin", "foldertwin", "--force-hash", "100", "--fail-fast",
                "--force-disable-actions", "--no-send-snapshot-report", "--tags", Tag(operation), .. pointTags, Path.GetFullPath(payload)]);
            afterUpload?.Invoke();
            existing = await FindAsync(operation, token) ?? throw new IOException("El punto enviado aún no aparece en el repositorio.");
        }
        RequireInventory(existing, point);
        return existing;
    }

    internal static void RequireInventory(KopiaPoint remote, CloudPoint local)
    {
        if (remote.Bytes != local.Bytes || remote.Files != local.Files || remote.Directories != checked(local.Directories + 1) ||
            remote.Layout != local.Layout || remote.CatalogHash != local.CatalogHash || remote.CatalogBytes != local.CatalogBytes ||
            remote.Layout == 2 && (remote.Generation != local.Generation || remote.Job != local.Job))
            throw new InvalidDataException("El inventario remoto difiere del punto local confirmado.");
    }

    internal async Task RestoreAsync(KopiaPoint point, string emptyOutput, CancellationToken token,
        Action<WorkProgress>? observe = null, CloudRecoverySelection? selection = null)
    {
        if (point.Layout == 2)
        {
            await RestoreCatalogPointAsync(point, emptyOutput, token, observe, selection);
            return;
        }
        if (point.Layout != 0 || selection is not null)
            throw new InvalidDataException("Esta fecha anterior admite recuperación completa con Kopia, sin selección por catálogo.");
        using var target = new DirectoryFence(emptyOutput);
        LabWorkspace.RequireEmpty(target.Root);
        LabWorkspace.RequireSpace(point.Bytes, LabWorkspace.AvailableSpace(target.Root));
        observe?.Invoke(new(WorkPhase.Restoring, 0, 0));
        await RunAsync(token, "snapshot", "restore", Identifier(point.RootId, "k"), Path.GetFullPath(emptyOutput),
            "--no-overwrite-files", "--no-overwrite-directories", "--no-overwrite-symlinks",
            "--no-ignore-permission-errors", "--write-files-atomically", "--flush-files");
        var inventory = FolderInventory.Scan(emptyOutput, BatchCatalog.VersionMaximumEntries, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (!inventory.Complete || inventory.Entries.Any(entry => FolderBatch.IsControl(entry.RelativePath)) ||
            inventory.FileCount != point.Files || inventory.Bytes != point.Bytes ||
            inventory.Entries.Length - inventory.FileCount + 1 != point.Directories)
            throw new InvalidDataException("La carpeta recuperada difiere del inventario remoto. Se conserva para revisión.");
        observe?.Invoke(new(WorkPhase.Restoring, point.Files, point.Files));
    }

    internal async Task<string> RunAsync(CancellationToken token, params string[] arguments)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (string name in start.Environment.Keys.Where(key => key.StartsWith("KOPIA_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        start.Environment["KOPIA_PASSWORD"] = password;
        start.Environment["KOPIA_CHECK_FOR_UPDATES"] = "false";
        if (s3 is not null)
        {
            start.Environment["AWS_ACCESS_KEY_ID"] = s3.AccessKey;
            start.Environment["AWS_SECRET_ACCESS_KEY"] = s3.SecretKey;
            if (s3.SessionToken is not null) start.Environment["AWS_SESSION_TOKEN"] = s3.SessionToken;
            start.Environment["AWS_EC2_METADATA_DISABLED"] = "true";
        }
        foreach (var argument in new[] { "--config-file", Path.GetFullPath(config), "--no-persist-credentials", "--no-auto-maintenance",
            "--no-use-credential-manager", "--disable-file-logging", "--disable-content-log", "--no-progress" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("No se pudo iniciar Kopia.");
        try
        {
            using var lifetime = CloudProcessLifetime.Attach(process);
            process.StandardInput.Close();
            var output = ReadOutputAsync(process.StandardOutput, token);
            var errors = ReadIssueAsync(process.StandardError, token);
            var exited = process.WaitForExitAsync(token);
            Task[] tasks = [exited, output, errors];
            try
            {
                // A reader can reject output while the child is still filling its
                // pipe. Observe that failure immediately so cleanup can stop it.
                var pending = tasks.ToList();
                while (pending.Count != 0)
                {
                    Task completed = await Task.WhenAny(pending);
                    await completed;
                    pending.Remove(completed);
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
                // Drain/observe every reader after termination without replacing
                // the original limit or cancellation error.
                try { await Task.WhenAll(tasks); } catch { }
            }
            if (process.ExitCode != 0)
            {
                var issue = await errors;
                throw new CloudProviderException(issue, issue switch
                {
                    CloudTransferIssue.Credentials => "El proveedor rechazó las credenciales. Revisa el acceso al repositorio.",
                    CloudTransferIssue.Quota => "El proveedor informó que alcanzó su cuota de almacenamiento.",
                    _ => $"Kopia terminó con código {process.ExitCode}. Revisa la conexión y vuelve a intentar."
                });
            }
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader output, CancellationToken token)
    {
        var text = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await output.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (count > MaximumOutputCharacters - text.Length)
                throw new InvalidDataException("La respuesta de Kopia supera el límite de lectura de FolderTwin. " +
                    "Consulta y recupera el historial completo con Kopia independiente.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static async Task<CloudTransferIssue> ReadIssueAsync(StreamReader errors, CancellationToken token)
    {
        var issue = CloudTransferIssue.Repository;
        char[] buffer = new char[4096];
        string suffix = "";
        int count;
        while ((count = await errors.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            string line = suffix + new string(buffer, 0, count);
            if (line.Contains("QuotaExceeded", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Insufficient Storage", StringComparison.OrdinalIgnoreCase))
                issue = CloudTransferIssue.Quota;
            else if (issue == CloudTransferIssue.Repository &&
                (line.Contains("InvalidAccessKeyId", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("SignatureDoesNotMatch", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("ExpiredToken", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("invalid credentials", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("wrong repository password", StringComparison.OrdinalIgnoreCase)))
                issue = CloudTransferIssue.Credentials;
            // Preserve enough overlap for every diagnostic token split across
            // reads, including a provider response with no newline at all.
            suffix = line.Length > 64 ? line[^64..] : line;
        }
        return issue;
    }

    private static string Tag(Guid operation) => $"foldertwin:{operation:N}";

    private static string Identifier(string? value, string prefix) =>
        value is not null && Regex.IsMatch(value, $"\\A{prefix}[a-f0-9]{{32}}\\z", RegexOptions.CultureInvariant)
            ? value : throw new InvalidDataException("Kopia devolvió un identificador no admitido.");
}
