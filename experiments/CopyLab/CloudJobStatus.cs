namespace CopyLab;

internal sealed record CloudJobStatus(bool Configured, bool Paused, DateTimeOffset? LastConfirmed,
    int Outstanding, bool RequiresReview, bool RecordUnavailable, CloudTransferIssue Issue)
{
    private static readonly CloudJobStatus Unconfigured = new(false, false, null, 0, false, false, CloudTransferIssue.None);

    internal static CloudJobStatus Read(ProductJobRegistry registry, Guid job, ProductRunHistory history)
    {
        try
        {
            bool paused = registry.IsCloudPaused(job);
            if (registry.ReadCloudConnection(job) is not { } connection)
                return Unconfigured;
            var transfers = registry.CloudTransfers(job, connection.Repository);
            var last = transfers.LastOrDefault(item => item.State == CloudTransferState.Available);
            var current = history.Runs.FirstOrDefault(run => run.Intent.Operation == ProductRunOperation.Backup &&
                run.Result is { State: ProductRunState.Confirmed, Generation: { } generation } &&
                !VersionStore.IsPartialPoint(generation));
            var review = transfers.FirstOrDefault(item => item.State == CloudTransferState.Review);
            var problem = review ?? transfers.FirstOrDefault(item => item.State == CloudTransferState.Retry);
            int outstanding = transfers.Count(item => item.State is CloudTransferState.Pending or CloudTransferState.Retry or
                CloudTransferState.Review or CloudTransferState.Paused);
            if (current?.Generation is { } latest && !transfers.Any(item => item.Ticket.Generation == latest))
                outstanding++;
            bool latestCancelled = current?.Generation is { } generationId && transfers.Any(item =>
                item.Ticket.Generation == generationId && item.State == CloudTransferState.Cancelled);
            return new(true, paused, last?.Point?.Finished, outstanding,
                review is not null || latestCancelled, false, problem?.Issue ?? CloudTransferIssue.None);
        }
        catch (Exception error) when (VersionJob.Expected(error))
        {
            return new(true, false, null, 0, !VersionJob.SharingViolation(error), true, CloudTransferIssue.None);
        }
    }

    internal string Label => RecordUnavailable ? RequiresReview ? "Registro requiere revisión" : "Estado temporalmente ocupado" :
        RequiresReview ? "Requiere atención" : Paused ? "Pausada" : Outstanding > 0 ? "Hay envíos pendientes" :
        LastConfirmed is null ? "Aún sin copia confirmada" : "Copia confirmada";

    internal string Guidance => RecordUnavailable && RequiresReview ?
        "Abre Copia en nube para revisar la configuración conservada." : Issue switch
        {
            CloudTransferIssue.Credentials => "Vuelve a conectar con credenciales válidas.",
            CloudTransferIssue.Quota => "Revisa la cuota del proveedor y reintenta.",
            CloudTransferIssue.Space => "Libera espacio para preparar el envío.",
            CloudTransferIssue.DestinationMissing => "Reconecta el disco del respaldo local.",
            CloudTransferIssue.LocalPoint => "Comprueba que el punto local siga disponible.",
            CloudTransferIssue.Repository => "Revisa el destino remoto y la conexión.",
            _ => RequiresReview ? "Abre Copia en nube para revisar los envíos." : ""
        };
}
