using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal enum ProductRunState { Confirmed, Cancelled, WaitingForSource, WaitingForDestination, Busy, Review }
internal enum ProductRunAction { Pause, Resume, Cancel }
internal enum ProductRunOperation { Backup, Verify }
internal enum WorkPhase { Analyzing, Copying, Verifying, Publishing, Restoring }
internal sealed record WorkProgress(WorkPhase Phase, int Processed, int Total, string? RelativePath = null);
internal sealed record ProductRunIntent(int Version, Guid Job, Guid Id, long Sequence, byte[] JobStamp, DateTimeOffset StartedUtc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] ProductRunOperation Operation = ProductRunOperation.Backup);
internal sealed record ProductRunResult(int Version, byte[] IntentHash, ProductRunState State, DateTimeOffset FinishedUtc,
    string? Generation, string? RelativePath, int? ErrorCode, string? Detail = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Degraded = false);
internal sealed record ProductRunProgress(int Version, byte[] IntentHash, long Revision, WorkProgress Work, bool Paused);
internal sealed record ProductRunCommand(int Version, byte[] IntentHash, long Number, ProductRunAction Action);
internal sealed record ProductRunView(ProductRunIntent Intent, ProductRunResult? Result, string? Generation,
    ProductRunProgress? Progress, bool Running, ProductRetry? Retry = null, bool RetryRunning = false);
internal sealed record ProductRetry(int Version, byte[] IntentHash, int Number, DateTimeOffset NextUtc);
internal sealed record ProductRunHistory(ProductRunView[] Runs, int Unreadable);

internal sealed partial class ProductJobRegistry
{
    internal ProductJob Get(Guid id)
    {
        using var operation = OpenLock();
        using var job = OpenJob(id);
        return ReadConfirmed(job, ReadIntent(job, id));
    }

    internal ProductRunLease StartRun(Guid id, Func<DateTimeOffset>? clock = null, ProductRunOperation operation = ProductRunOperation.Backup)
    {
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        var job = OpenJob(id);
        FileStream? execution = null;
        try
        {
            var definition = ReadConfirmed(job, ReadIntent(job, id));
            if (operation == ProductRunOperation.Backup && (!definition.Enabled || definition.Retired))
                throw new InvalidDataException("El trabajo está deshabilitado o retirado. Sus respaldos se conservan para recuperar.");
            execution = WindowsProbeNative.OpenPlainFile(job, "execution.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
            using var runs = WindowsProbeNative.OpenOrCreatePlainDirectory(job, "runs");
            var history = ReadRuns(job, runs, id);
            // Preserve unreadable records. Their directory sequence still reserves
            // the number, so a torn history write cannot cause sequence reuse.
            long sequence = checked(WindowsProbeNative.ReadDirectory(runs).Select(entry => DirectorySequence(entry.Name)).DefaultIfEmpty(0).Max() + 1);
            Guid runId = Guid.NewGuid();
            var intent = new ProductRunIntent(1, id, runId, sequence, JobStamp(job), (clock?.Invoke() ?? DateTimeOffset.UtcNow).ToUniversalTime(), operation);
            var directory = WindowsProbeNative.CreatePlainDirectory(runs, RunName(intent));
            try
            {
                RetentionJournal.WriteFile(directory, "intent", Encode(intent));
                var active = WindowsProbeNative.OpenPlainFile(directory, "active.lock", FileAccess.ReadWrite, FileShare.None, FileMode.CreateNew);
                return new ProductRunLease(definition, intent, history, job, directory, execution, active);
            }
            catch { directory.Dispose(); throw; }
        }
        catch { execution?.Dispose(); job.Dispose(); throw; }
    }

    internal ProductRunHistory RunHistory(Guid id)
    {
        using var job = OpenJob(id);
        _ = ReadConfirmed(job, ReadIntent(job, id));
        if (!DirectoryPresent(job, "runs")) return new([], 0);
        using var runs = WindowsProbeNative.OpenPlainDirectory(job, "runs");
        return ReadRuns(job, runs, id);
    }

    internal void RequestRun(Guid jobId, Guid runId, ProductRunAction action)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        using var job = OpenJob(jobId);
        _ = ReadConfirmed(job, ReadIntent(job, jobId));
        using var runs = WindowsProbeNative.OpenPlainDirectory(job, "runs");
        var selected = ReadRuns(job, runs, jobId).Runs.SingleOrDefault(run => run.Intent.Id == runId)
            ?? throw new InvalidDataException("El intento ya no está disponible.");
        if (!selected.Running || selected.Result is not null)
            throw new InvalidDataException("El intento terminó. Actualiza su estado antes de enviar otra acción.");
        using var run = WindowsProbeNative.OpenPlainDirectory(runs, RunName(selected.Intent));
        using var commandLock = WindowsProbeNative.OpenPlainFile(run, "control.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
        var commands = ReadCommands(run, selected.Intent);
        long number = commands.LastOrDefault()?.Number + 1 ?? 1;
        if (number > 10_000) throw new InvalidDataException("El intento alcanzó el límite de solicitudes de control.");
        RetentionJournal.WriteFile(run, CommandName(number), Encode(new ProductRunCommand(1, IntentHash(selected.Intent), number, action)));
    }

    private static ProductRunHistory ReadRuns(SafeFileHandle job, SafeFileHandle runs, Guid id)
    {
        var found = new List<ProductRunView>();
        int unreadable = 0;
        var sequences = new HashSet<long>();
        foreach (var entry in WindowsProbeNative.ReadDirectory(runs).Where(entry => entry.Name is not ("." or "..")))
        {
            try
            {
                using var run = WindowsProbeNative.OpenPlainDirectory(runs, entry.Name);
                var intent = Decode<ProductRunIntent>(run, "intent");
                if (intent.Version != 1 || intent.Job != id || intent.Id == Guid.Empty || intent.Sequence < 1 ||
                    entry.Name != RunName(intent) || !intent.JobStamp.AsSpan().SequenceEqual(JobStamp(job)) ||
                    intent.StartedUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(intent.Operation) || !sequences.Add(intent.Sequence))
                    throw new InvalidDataException("El intento pertenece a otro trabajo o tiene una secuencia inválida.");
                string? generation = ReadGeneration(run, intent);
                ProductRunResult? result = ReadResult(run, intent, generation);
                ProductRunProgress? progress = null;
                foreach (string name in new[] { "progress-a", "progress-b" })
                {
                    try
                    {
                        if (!WindowsProbeNative.PlainFileExists(run, name)) continue;
                        var candidate = Decode<ProductRunProgress>(run, name);
                        if (candidate.Version != 1 || candidate.Revision < 1 || !candidate.IntentHash.AsSpan().SequenceEqual(IntentHash(intent)) ||
                            !ValidWork(candidate.Work)) continue;
                        if (progress is null || candidate.Revision > progress.Revision) progress = candidate;
                    }
                    catch (Exception error) when (VersionJob.Expected(error)) { /* Progress is advisory; intent and result remain authoritative. */ }
                }
                bool running = false;
                if (result is null && WindowsProbeNative.PlainFileExists(run, "active.lock"))
                {
                    try { using var probe = WindowsProbeNative.OpenPlainFile(run, "active.lock", FileAccess.ReadWrite, FileShare.None); }
                    catch (Exception error) when (VersionJob.SharingViolation(error)) { running = true; }
                }
                // A result may have been published between the first read and
                // release of the worker's lock. Observe it before declaring an interruption.
                if (result is null && !running) result = ReadResult(run, intent, generation);
                if (result is not null) generation = result.Generation;
                ProductRetry? retry = null;
                bool retryRunning = false;
                if (WindowsProbeNative.PlainFileExists(run, "retry"))
                {
                    retry = Decode<ProductRetry>(run, "retry");
                    if (retry.Version != 1 || retry.Number is < 1 or > 3 || retry.NextUtc.Offset != TimeSpan.Zero ||
                        !retry.IntentHash.AsSpan().SequenceEqual(IntentHash(intent)) ||
                        result?.State is not (ProductRunState.Busy or ProductRunState.WaitingForSource or ProductRunState.WaitingForDestination))
                        throw new InvalidDataException("El reintento no corresponde al resultado registrado.");
                    retryRunning = IsLocked(run, "retry.active");
                }
                found.Add(new(intent, result, generation, progress, running, retry, retryRunning));
            }
            catch (Exception error) when (VersionJob.Expected(error)) { unreadable++; }
        }
        return new(found.OrderByDescending(run => run.Intent.Sequence).ToArray(), unreadable);
    }

    private sealed record RunGeneration(byte[] IntentHash, string Generation);
    private static bool IsLocked(SafeFileHandle directory, string name)
    {
        try { using var probe = WindowsProbeNative.OpenPlainFile(directory, name, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (FileNotFoundException) { return false; }
        catch (Exception error) when (VersionJob.SharingViolation(error)) { return true; }
    }

    internal FileStream AcquireScheduledLock(Guid id)
    {
        using var job = OpenJob(id);
        _ = ReadConfirmed(job, ReadIntent(job, id));
        return WindowsProbeNative.OpenPlainFile(job, "scheduled.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
    }

    internal IDisposable BeginRetry(Guid id, Guid runId, int number)
    {
        using var job = OpenJob(id);
        using var runs = WindowsProbeNative.OpenPlainDirectory(job, "runs");
        var previous = ReadRuns(job, runs, id).Runs.Single(item => item.Intent.Id == runId);
        if (number is < 1 or > 3 || previous.Result?.State is not
            (ProductRunState.Busy or ProductRunState.WaitingForSource or ProductRunState.WaitingForDestination))
            throw new InvalidDataException("Este resultado no admite un reintento automático.");
        using var run = WindowsProbeNative.OpenPlainDirectory(runs, RunName(previous.Intent));
        var retry = new ProductRetry(1, IntentHash(previous.Intent), number, DateTimeOffset.UtcNow.AddMinutes(5));
        RetentionJournal.WriteFile(run, "retry", Encode(retry));
        return WindowsProbeNative.OpenPlainFile(run, "retry.active", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate);
    }
    private static string? ReadGeneration(SafeFileHandle run, ProductRunIntent intent)
    {
        if (!WindowsProbeNative.PlainFileExists(run, "generation")) return null;
        var saved = Decode<RunGeneration>(run, "generation");
        if (!saved.IntentHash.AsSpan().SequenceEqual(IntentHash(intent)))
            throw new InvalidDataException("La generación del intento no coincide con su registro.");
        _ = VersionStore.GenerationTime(saved.Generation);
        return saved.Generation;
    }
    private static ProductRunResult? ReadResult(SafeFileHandle run, ProductRunIntent intent, string? generation)
    {
        if (!WindowsProbeNative.PlainFileExists(run, "result")) return null;
        var result = Decode<ProductRunResult>(run, "result");
        if (result.Generation != generation) generation = ReadGeneration(run, intent);
        if (result.Version != 1 || !result.IntentHash.AsSpan().SequenceEqual(IntentHash(intent)) ||
            !Enum.IsDefined(result.State) || result.Generation != generation || result.FinishedUtc.Offset != TimeSpan.Zero ||
            result.State == ProductRunState.Confirmed && generation is null || !ValidRelative(result.RelativePath) || result.Detail?.Length > 2000)
            throw new InvalidDataException("El resultado no corresponde al intento.");
        return result;
    }
    private static byte[] JobStamp(SafeFileHandle job)
    {
        using var confirmed = WindowsProbeNative.OpenPlainFile(job, CommitName, FileAccess.Read, FileShare.Read);
        byte[] stamp = new byte[32];
        confirmed.ReadExactly(stamp);
        if (confirmed.ReadByte() != -1) throw new InvalidDataException("La confirmación del trabajo tiene un tamaño inválido.");
        return stamp;
    }
    private static byte[] IntentHash(ProductRunIntent intent) => SHA256.HashData(Encode(intent));
    private static string RunName(ProductRunIntent intent) => intent.Sequence.ToString("D20", CultureInfo.InvariantCulture) + "-" + intent.Id.ToString("N");
    private static long DirectorySequence(string name) => name.Length == 53 && name[20] == '-' &&
        long.TryParse(name.AsSpan(0, 20), NumberStyles.None, CultureInfo.InvariantCulture, out long sequence) && sequence > 0 &&
        Guid.TryParseExact(name.AsSpan(21), "N", out _) ? sequence : 0;
    private static string CommandName(long number) => "command-" + number.ToString("D5", CultureInfo.InvariantCulture);
    private static bool DirectoryPresent(SafeFileHandle root, string name)
    {
        try { using var directory = WindowsProbeNative.OpenPlainDirectory(root, name); return true; }
        catch (FileNotFoundException) { return false; }
    }
    private static bool ValidWork(WorkProgress work) => Enum.IsDefined(work.Phase) && work.Processed >= 0 && work.Total >= work.Processed &&
        ValidRelative(work.RelativePath);
    private static bool ValidRelative(string? path)
    {
        if (path is null) return true;
        if (path.Length is 0 or > 32767 || Path.IsPathRooted(path)) return false;
        try
        {
            foreach (string part in path.Split(Path.DirectorySeparatorChar))
            {
                if (part is "." or "..") return false;
                DirectoryFence.CheckName(part);
            }
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private static ProductRunCommand[] ReadCommands(SafeFileHandle run, ProductRunIntent intent)
    {
        var commands = new List<ProductRunCommand>();
        foreach (var entry in WindowsProbeNative.ReadDirectory(run).Where(entry =>
            entry.Name.StartsWith("command-", StringComparison.Ordinal) && !entry.Name.EndsWith(".tmp", StringComparison.Ordinal)).OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            var command = Decode<ProductRunCommand>(run, entry.Name);
            if (command.Version != 1 || command.Number != commands.Count + 1 || entry.Name != CommandName(command.Number) ||
                !Enum.IsDefined(command.Action) || !command.IntentHash.AsSpan().SequenceEqual(IntentHash(intent)) || commands.Count >= 10_000)
                throw new InvalidDataException("Una solicitud de control no corresponde al intento activo.");
            commands.Add(command);
        }
        return commands.ToArray();
    }

    internal sealed class ProductRunLease : IDisposable
    {
        internal ProductJob Job { get; }
        internal ProductRunIntent Intent { get; }
        internal ProductRunHistory Previous { get; }
        internal string? Generation { get; private set; }
        private readonly SafeFileHandle job;
        private readonly SafeFileHandle run;
        private readonly FileStream execution;
        private readonly FileStream active;
        private long revision;

        internal ProductRunLease(ProductJob job, ProductRunIntent intent, ProductRunHistory previous,
            SafeFileHandle jobDirectory, SafeFileHandle run, FileStream execution, FileStream active)
        { Job = job; Intent = intent; Previous = previous; this.job = jobDirectory; this.run = run; this.execution = execution; this.active = active; }

        internal void SetGeneration(string generation)
        {
            _ = VersionStore.GenerationTime(generation);
            RetentionJournal.WriteFile(run, "generation", Encode(new RunGeneration(IntentHash(Intent), generation)));
            Generation = generation;
        }
        internal ProductRunAction? ReadAction() => ReadCommands(run, Intent).LastOrDefault()?.Action;
        internal void Progress(WorkProgress work, bool paused)
        {
            if (!ValidWork(work)) throw new InvalidDataException("El progreso contiene una ruta o un recuento inválido.");
            long next = checked(++revision);
            byte[] encoded = Encode(new ProductRunProgress(1, IntentHash(Intent), next, work, paused));
            using var file = WindowsProbeNative.OpenPlainFile(run, next % 2 == 0 ? "progress-a" : "progress-b", FileAccess.ReadWrite,
                FileShare.None, FileMode.OpenOrCreate);
            file.Position = 0;
            file.Write(encoded);
            file.SetLength(encoded.Length);
            file.Flush();
        }
        internal ProductRunResult Finish(ProductRunState state, string? path = null, int? errorCode = null, Func<DateTimeOffset>? clock = null,
            string? detail = null, bool degraded = false)
        {
            if (!ValidRelative(path) || !Enum.IsDefined(state) || state == ProductRunState.Confirmed && Generation is null)
                throw new InvalidDataException("El resultado necesita un estado, una generación y una ruta relativa válidos.");
            var result = new ProductRunResult(1, IntentHash(Intent), state, (clock?.Invoke() ?? DateTimeOffset.UtcNow).ToUniversalTime(),
                Generation, path, errorCode, detail is { Length: > 2000 } ? detail[..2000] : detail, degraded);
            RetentionJournal.WriteFile(run, "result", Encode(result));
            return result;
        }
        public void Dispose() { active.Dispose(); execution.Dispose(); run.Dispose(); job.Dispose(); }
    }
}
