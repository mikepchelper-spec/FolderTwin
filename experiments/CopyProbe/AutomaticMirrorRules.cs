using System.Security.Cryptography;
using System.Text;

internal static class AutomaticMirrorRules
{
    internal const int Version = 1;
    internal const int MaximumBatch = 256;
    private const int MaximumHour = 512;

    internal static bool ValidSetting(int version, ProductBackupMode mode, ProductWatchMode watching) =>
        version == 0 || version == Version && mode == ProductBackupMode.ProtectedMirror && watching != ProductWatchMode.Off;

    internal static string[] Check(MirrorPreview preview, ProductRunIntent intent, Func<ProductRunHistory> readHistory)
    {
        if (preview.RequiresEmptyReview)
            throw new InvalidDataException("El origen vacío requiere una revisión del espejo.");
        string[] affected = preview.AffectedPaths ?? throw new InvalidDataException("Falta el inventario de cambios del espejo.");
        int batchLimit = Math.Clamp(preview.PreviousEntries / 4, 4, MaximumBatch);
        if (affected.Length > batchLimit)
            throw new InvalidDataException($"La actualización afecta a más de {batchLimit} rutas existentes. Revisa los cambios del espejo.");
        if (affected.Length == 0) return [];
        var history = readHistory();
        if (history.Unreadable != 0)
            throw new InvalidDataException("El historial contiene intentos ilegibles. Revisa el espejo antes de retirar o reemplazar archivos.");

        string[] hashes = affected.Select(path =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        int hourLimit = Math.Clamp(preview.PreviousEntries * 2 / 5, 8, MaximumHour);
        var used = new HashSet<string>(hashes, StringComparer.Ordinal);
        foreach (var previous in history.Runs)
        {
            // Include future-dated records after a clock rollback.
            if (previous.MirrorCharge is not { } charge || intent.StartedUtc - previous.Intent.StartedUtc >= TimeSpan.FromHours(1))
                continue;
            used.UnionWith(charge.Paths);
            if (used.Count > hourLimit)
                throw new InvalidDataException($"Los cambios recientes superan {hourLimit} rutas distintas. Revisa el espejo antes de continuar.");
        }
        return hashes;
    }
}
