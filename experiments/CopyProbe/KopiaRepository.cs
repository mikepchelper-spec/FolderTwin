using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record KopiaPoint(string ManifestId, string RootId, DateTimeOffset Finished, long Bytes, int Files, int Directories);
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

internal sealed class KopiaRepository(string executable, string config, string password, KopiaS3Credentials? s3 = null)
{
    internal async Task<string> IdentityAsync(CancellationToken token)
    {
        using var status = JsonDocument.Parse(await RunAsync(token, "repository", "status", "--json"));
        if (!status.RootElement.TryGetProperty("uniqueIDHex", out var id) || id.ValueKind != JsonValueKind.String ||
            id.GetString() is not { } value || !RetentionJournal.IsDigest(value))
            throw new InvalidDataException("Kopia devolvió una identidad de repositorio no válida.");
        return value;
    }

    internal async Task<KopiaPoint?> FindAsync(Guid operation, CancellationToken token)
    {
        string tag = Tag(operation);
        string json = await RunAsync(token, "snapshot", "list", "--all", "--show-identical", "--json", "--tags", tag);
        using var result = JsonDocument.Parse(json);
        KopiaPoint? found = null;
        foreach (var entry in result.RootElement.EnumerateArray())
        {
            if (found is not null || entry.GetProperty("tags").GetProperty("tag:foldertwin").GetString() != operation.ToString("N"))
                throw new InvalidDataException("El repositorio devolvió puntos ambiguos o de otra operación.");
            found = ReadPoint(entry);
        }
        return found;
    }

    internal async Task<CloudRecoveryPoint[]> ListAsync(CancellationToken token)
    {
        using var result = JsonDocument.Parse(await RunAsync(token, "snapshot", "list", "--all", "--show-identical", "--json"));
        var points = new Dictionary<(Guid, string), CloudRecoveryPoint>();
        foreach (var entry in result.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("tags", out var tags) || !tags.TryGetProperty("tag:ft-job", out var jobTag)) continue;
            if (!Guid.TryParseExact(jobTag.GetString(), "N", out var job) || job == Guid.Empty ||
                !tags.TryGetProperty("tag:ft-generation", out var generationTag) || generationTag.GetString() is not { } generation)
                throw new InvalidDataException("El repositorio contiene una fecha de FolderTwin no válida.");
            _ = VersionStore.GenerationTime(generation);
            if (!points.TryAdd((job, generation), new(job, generation, ReadPoint(entry))))
                throw new InvalidDataException("El repositorio contiene dos instantáneas para la misma fecha.");
        }
        return points.Values.OrderByDescending(point => point.Generation, StringComparer.Ordinal).ToArray();
    }

    private static KopiaPoint ReadPoint(JsonElement entry)
    {
        var root = entry.GetProperty("rootEntry");
        var summary = root.GetProperty("summ");
        var stats = entry.GetProperty("stats");
        if (root.GetProperty("type").GetString() != "d" || summary.GetProperty("numFailed").GetInt32() != 0 ||
            summary.GetProperty("symlinks").GetInt32() != 0 || stats.GetProperty("errorCount").GetInt32() != 0 ||
            stats.GetProperty("ignoredErrorCount").GetInt32() != 0 || stats.GetProperty("excludedFileCount").GetInt32() != 0 ||
            stats.GetProperty("excludedDirCount").GetInt32() != 0)
            throw new InvalidDataException("Kopia devolvió un punto incompleto o con exclusiones.");
        return new(Identifier(entry.GetProperty("id").GetString(), ""), Identifier(root.GetProperty("obj").GetString(), "k"),
            entry.GetProperty("endTime").GetDateTimeOffset(), summary.GetProperty("size").GetInt64(),
            summary.GetProperty("files").GetInt32(), summary.GetProperty("dirs").GetInt32());
    }

    internal async Task<KopiaPoint> PublishAsync(Guid operation, CloudPoint point, string payload, CancellationToken token,
        Action? afterUpload = null)
    {
        var existing = await FindAsync(operation, token);
        if (existing is null)
        {
            await RunAsync(token, "snapshot", "create", "--json", "--pin", "foldertwin", "--force-hash", "100", "--fail-fast",
                "--force-disable-actions", "--no-send-snapshot-report", "--tags", Tag(operation),
                "--tags", $"ft-job:{point.Job:N}", "--tags", $"ft-generation:{point.Generation}", Path.GetFullPath(payload));
            afterUpload?.Invoke();
            existing = await FindAsync(operation, token) ?? throw new IOException("El punto enviado aún no aparece en el repositorio.");
        }
        RequireInventory(existing, point);
        return existing;
    }

    internal static void RequireInventory(KopiaPoint remote, CloudPoint local)
    {
        if (remote.Bytes != local.Bytes || remote.Files != local.Files || remote.Directories != checked(local.Directories + 1))
            throw new InvalidDataException("El inventario remoto difiere del punto local confirmado.");
    }

    internal async Task RestoreAsync(KopiaPoint point, string emptyOutput, CancellationToken token)
    {
        using var target = new DirectoryFence(emptyOutput);
        LabWorkspace.RequireEmpty(target.Root);
        await RunAsync(token, "snapshot", "restore", Identifier(point.RootId, "k"), Path.GetFullPath(emptyOutput),
            "--no-overwrite-files", "--no-overwrite-directories", "--no-overwrite-symlinks",
            "--no-ignore-permission-errors", "--write-files-atomically", "--flush-files");
        var inventory = FolderInventory.Scan(emptyOutput, BatchCatalog.VersionMaximumEntries, cancellationToken: token);
        token.ThrowIfCancellationRequested();
        if (!inventory.Complete || inventory.FileCount != point.Files || inventory.Bytes != point.Bytes ||
            inventory.Entries.Length - inventory.FileCount + 1 != point.Directories)
            throw new InvalidDataException("La carpeta recuperada difiere del inventario remoto. Se conserva para revisión.");
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
            var output = process.StandardOutput.ReadToEndAsync(token);
            var errors = ReadIssueAsync(process.StandardError, token);
            await Task.WhenAll(process.WaitForExitAsync(token), output, errors);
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

    private static async Task<CloudTransferIssue> ReadIssueAsync(StreamReader errors, CancellationToken token)
    {
        var issue = CloudTransferIssue.Repository;
        while (await errors.ReadLineAsync(token) is { } line)
        {
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
        }
        return issue;
    }

    private static string Tag(Guid operation) => $"foldertwin:{operation:N}";

    private static string Identifier(string? value, string prefix) =>
        value is not null && Regex.IsMatch(value, $"\\A{prefix}[a-f0-9]{{32}}\\z", RegexOptions.CultureInvariant)
            ? value : throw new InvalidDataException("Kopia devolvió un identificador no admitido.");
}
