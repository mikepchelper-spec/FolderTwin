using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal enum AbandonStep { PlanPartial, Planned, Registered, Archived, Abandoned, CompletionPartial, Completed }
internal sealed record AbandonArtifact(string Area, string Name, byte[] Identity, long Length, byte[] Hash);
internal sealed record VersionAbandonReview(string Generation, byte[] DocumentHash, byte[] RegistryHash, byte[] SourceIdentity,
    int Files, AbandonArtifact[] Artifacts);
internal sealed record VersionAbandonPlan(int Version, Guid Marker, byte[] Identity, VersionAbandonReview Review);

internal sealed partial class VersionStore
{
    private const string AbandonDirectory = "abandoned";
    private static readonly JsonSerializerOptions AbandonJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 12
    };

    internal VersionAbandonReview PreviewAbandonVersion(string generation)
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var root = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(root.Root);
        using var operation = OpenLock(root.Root, FileAccess.ReadWrite);
        RequireNoAbandonInProgress(root.Root);
        return BuildAbandonReview(scope.Root, root.Root, generation);
    }

    private VersionAbandonReview BuildAbandonReview(SafeFileHandle scope, SafeFileHandle root, string generation)
    {
        _ = GenerationTime(generation);
        RequireVersionAbandonMode(root);
        RequireNoRetentionInProgress(root);
        if (IsCommitted(root, generation)) throw new InvalidDataException("El punto ya está confirmado. Reinténtalo para verificar su copia.");
        using var pending = WindowsProbeNative.OpenPlainDirectory(root, PendingDirectory);
        var plan = ReadIntent(scope, root, pending, generation);
        byte[] document = BatchCatalog.WriteMetadata(Stream.Null, plan, generation);
        var prepared = GenerationRegistry.PreviewRegistration(root, binding, generation, document);
        var artifacts = new List<AbandonArtifact>();
        foreach (string area in new[] { PrimaryDirectory, ReplicaDirectory, CommitDirectory })
        {
            using var directory = WindowsProbeNative.OpenPlainDirectory(root, area);
            foreach (string name in area == CommitDirectory ? new[] { generation + ".commit.tmp" } : new[] { generation + ".jsonl", generation + ".jsonl.tmp" })
            {
                if (!WindowsProbeNative.PlainFileExists(directory, name)) continue;
                using var file = WindowsProbeNative.OpenPlainFile(directory, name, FileAccess.Read, FileShare.None);
                WindowsProbeNative.RequireSupportedData(file.SafeFileHandle, name);
                if (file.Length > BatchCatalog.VersionMaximumBytes) throw new InvalidDataException("Un catálogo excede su tamaño permitido.");
                artifacts.Add(new(area, name, WindowsProbeNative.DirectoryIdentity(file.SafeFileHandle), file.Length, SHA256.HashData(file)));
            }
        }
        return new(generation, document, prepared.LastHash, plan.SourceIdentity, plan.Files.Length, artifacts.ToArray());
    }

    internal void BeginAbandonVersion(VersionAbandonReview review, Action<AbandonStep>? checkpoint = null,
        Action<RegistryStep>? registryCheckpoint = null)
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var root = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(root.Root);
        using var operation = OpenLock(root.Root, FileAccess.ReadWrite);
        RequireNoAbandonInProgress(root.Root);
        var current = BuildAbandonReview(scope.Root, root.Root, review.Generation);
        if (!JsonSerializer.SerializeToUtf8Bytes(current, AbandonJson).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(review, AbandonJson)))
            throw new InvalidDataException("El estado pendiente cambió. Repite la revisión antes de conservarlo y comenzar otro análisis.");
        var plan = new VersionAbandonPlan(1, binding.Marker, binding.Identity, review);
        using var all = WindowsProbeNative.OpenOrCreatePlainDirectory(root.Root, AbandonDirectory);
        using var journal = WindowsProbeNative.CreatePlainDirectory(all, review.Generation);
        WriteAbandonPlan(journal, plan, checkpoint);
        CompleteAbandon(scope.Root, root.Root, journal, plan, checkpoint, registryCheckpoint);
    }

    internal void ResumeAbandonVersion(string generation, Action<AbandonStep>? checkpoint = null,
        Action<RegistryStep>? registryCheckpoint = null)
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var root = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(root.Root);
        using var operation = OpenLock(root.Root, FileAccess.ReadWrite);
        _ = GenerationTime(generation);
        using var all = WindowsProbeNative.OpenPlainDirectory(root.Root, AbandonDirectory);
        using var journal = WindowsProbeNative.OpenPlainDirectory(all, generation);
        VersionAbandonPlan plan;
        if (WindowsProbeNative.PlainFileExists(journal, "plan")) plan = ReadAbandonPlan(journal, generation);
        else
        {
            // No file move or registry transition is allowed before this plan is published.
            if (WindowsProbeNative.ReadDirectory(journal).Any(entry => entry.Name is not ("." or ".." or "plan.tmp")))
                throw new InvalidDataException("El diario incompleto contiene datos desconocidos. Se conserva para revisión.");
            plan = new(1, binding.Marker, binding.Identity, BuildAbandonReview(scope.Root, root.Root, generation));
            WriteAbandonPlan(journal, plan, checkpoint);
        }
        CompleteAbandon(scope.Root, root.Root, journal, plan, checkpoint, registryCheckpoint);
    }

    private void CompleteAbandon(SafeFileHandle scope, SafeFileHandle root, SafeFileHandle journal, VersionAbandonPlan plan,
        Action<AbandonStep>? checkpoint, Action<RegistryStep>? registryCheckpoint)
    {
        if (AbandonComplete(journal, plan)) return;
        if (!workspace.IsProductVersion || IsCommitted(root, plan.Review.Generation))
            throw new InvalidDataException("El punto se confirmó o pertenece a otro modo. El abandono queda suspendido.");
        RequireVersionAbandonMode(root);
        RequireNoRetentionInProgress(root);
        using var pending = WindowsProbeNative.OpenPlainDirectory(root, PendingDirectory);
        var sourcePlan = ReadIntent(scope, root, pending, plan.Review.Generation);
        if (!sourcePlan.SourceIdentity.AsSpan().SequenceEqual(plan.Review.SourceIdentity) ||
            !BatchCatalog.WriteMetadata(Stream.Null, sourcePlan, plan.Review.Generation).AsSpan().SequenceEqual(plan.Review.DocumentHash))
            throw new InvalidDataException("El plan original cambió. Se conservan los datos y se suspende el abandono.");
        var state = GenerationRegistry.ReadCommonPrefix(root, binding);
        bool abandoned = state.Abandoned.TryGetValue(plan.Review.Generation, out var prior);
        if (abandoned && !prior.AsSpan().SequenceEqual(plan.Review.DocumentHash))
            throw new InvalidDataException("El registro abandonado corresponde a otro documento.");
        if (!abandoned)
        {
            if (!state.Generations.ContainsKey(plan.Review.Generation))
                GenerationRegistry.Register(root, binding, plan.Review.Generation, plan.Review.DocumentHash);
            state = GenerationRegistry.ReadCommonPrefix(root, binding);
            if (!state.LastHash.AsSpan().SequenceEqual(plan.Review.RegistryHash) ||
                !state.Generations.TryGetValue(plan.Review.Generation, out var expected) || !expected.AsSpan().SequenceEqual(plan.Review.DocumentHash))
                throw new InvalidDataException("Otra transición cambió el registro del punto pendiente.");
        }
        checkpoint?.Invoke(AbandonStep.Registered);
        foreach (var artifact in plan.Review.Artifacts)
        {
            using var parent = WindowsProbeNative.OpenPlainDirectory(root, artifact.Area);
            string archived = ArtifactArchiveName(artifact);
            bool original = WindowsProbeNative.PlainFileExists(parent, artifact.Name);
            bool saved = WindowsProbeNative.PlainFileExists(journal, archived);
            if (original == saved) throw new InvalidDataException("La ubicación de un catálogo pendiente es ambigua o falta.");
            using var file = WindowsProbeNative.OpenPlainFile(original ? parent : journal, original ? artifact.Name : archived,
                FileAccess.Read, FileShare.None, rename: original);
            RequireAbandonArtifact(file, artifact);
            if (original) WindowsProbeNative.RenameNew(file.SafeFileHandle, journal, archived);
            checkpoint?.Invoke(AbandonStep.Archived);
        }
        foreach (string area in new[] { PrimaryDirectory, ReplicaDirectory })
        {
            using var parent = WindowsProbeNative.OpenPlainDirectory(root, area);
            if (WindowsProbeNative.PlainFileExists(parent, plan.Review.Generation + ".jsonl") ||
                WindowsProbeNative.PlainFileExists(parent, plan.Review.Generation + ".jsonl.tmp"))
                throw new InvalidDataException("Aparecieron catálogos no revisados. Se suspende el abandono.");
        }
        using (var commits = WindowsProbeNative.OpenPlainDirectory(root, CommitDirectory))
            if (WindowsProbeNative.PlainFileExists(commits, plan.Review.Generation + ".commit.tmp"))
                throw new InvalidDataException("Apareció una confirmación temporal no revisada. Se conserva para revisión.");
        GenerationRegistry.Abandon(root, binding, plan.Review.Generation, plan.Review.DocumentHash, registryCheckpoint);
        checkpoint?.Invoke(AbandonStep.Abandoned);
        WriteAbandonRecord(journal, "complete", SHA256.HashData(EncodeAbandonPlan(plan)), () => checkpoint?.Invoke(AbandonStep.CompletionPartial));
        checkpoint?.Invoke(AbandonStep.Completed);
    }

    private static byte[] EncodeAbandonPlan(VersionAbandonPlan plan)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(plan, AbandonJson);
        if (body.Length > 16 * 1024) throw new InvalidDataException("El diario de abandono excede su tamaño permitido.");
        return [.. SHA256.HashData(body), .. body];
    }

    internal (string Generation, bool Complete)[] ListVersionAbandons()
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var root = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(root.Root);
        using var operation = OpenLock(root.Root, FileAccess.Read);
        SafeFileHandle all;
        try { all = WindowsProbeNative.OpenPlainDirectory(root.Root, AbandonDirectory); }
        catch (FileNotFoundException) { return []; }
        using (all)
            return WindowsProbeNative.ReadDirectory(all).Where(entry => entry.Name is not ("." or "..")).Select(entry =>
            {
                _ = GenerationTime(entry.Name);
                using var journal = WindowsProbeNative.OpenPlainDirectory(all, entry.Name);
                bool complete = WindowsProbeNative.PlainFileExists(journal, "plan") && AbandonComplete(journal, ReadAbandonPlan(journal, entry.Name));
                return (entry.Name, complete);
            }).ToArray();
    }

    internal bool IsAbandonedGeneration(string generation)
    {
        _ = GenerationTime(generation);
        using var scope = workspace.OpenRegisteredRoot();
        using var root = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(root.Root);
        using var operation = OpenLock(root.Root, FileAccess.Read);
        RequireNoAbandonInProgress(root.Root);
        return registeredGenerations && GenerationRegistry.ReadCommonPrefix(root.Root, binding).Abandoned.ContainsKey(generation);
    }

    private void RequireVersionAbandonMode(SafeFileHandle root)
    {
        if (!workspace.IsProductVersion || !registeredGenerations)
            throw new InvalidDataException("Esta operación requiere un trabajo de versionado de producto.");
        SafeFileHandle mirror;
        try { mirror = WindowsProbeNative.OpenPlainDirectory(root, MirrorJournal.DirectoryName); }
        catch (FileNotFoundException)
        {
            var prefix = GenerationRegistry.ReadCommonPrefix(root, binding);
            if (prefix.MirrorGeneration is not null || prefix.PendingMirrorGeneration is not null)
                throw new InvalidDataException("El almacén tiene referencias de espejo que requieren revisión.");
            return;
        }
        using (mirror) throw new InvalidDataException("Conserva la actualización del espejo mediante su propio diario.");
    }

    private static void WriteAbandonPlan(SafeFileHandle journal, VersionAbandonPlan plan, Action<AbandonStep>? checkpoint)
    {
        byte[] bytes = EncodeAbandonPlan(plan);
        WriteAbandonRecord(journal, "plan", bytes, () => checkpoint?.Invoke(AbandonStep.PlanPartial));
        checkpoint?.Invoke(AbandonStep.Planned);
    }

    private static void WriteAbandonRecord(SafeFileHandle journal, string name, byte[] bytes, Action partial)
    {
        if (WindowsProbeNative.PlainFileExists(journal, name))
        {
            using var final = WindowsProbeNative.OpenPlainFile(journal, name, FileAccess.Read, FileShare.Read);
            byte[] saved = new byte[bytes.Length];
            final.ReadExactly(saved);
            if (final.ReadByte() != -1 || !saved.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("El registro existente no corresponde al abandono.");
            return;
        }
        string temporary = name + ".tmp";
        using (var draft = WindowsProbeNative.OpenPlainFile(journal, temporary, FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate))
        {
            if (draft.Length > bytes.Length) throw new InvalidDataException("El temporal del abandono no corresponde al plan revisado.");
            byte[] existing = new byte[(int)draft.Length];
            draft.ReadExactly(existing);
            if (!bytes.AsSpan(0, existing.Length).SequenceEqual(existing)) throw new InvalidDataException("El temporal de abandono tiene otro contenido.");
            int middle = bytes.Length / 2;
            if (draft.Length < middle)
            {
                draft.Write(bytes.AsSpan((int)draft.Length, middle - (int)draft.Length));
                draft.Flush(flushToDisk: true);
                partial();
            }
            draft.Write(bytes.AsSpan((int)draft.Length));
            draft.Flush(flushToDisk: true);
            draft.Position = 0;
            byte[] observed = new byte[bytes.Length];
            draft.ReadExactly(observed);
            if (!observed.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("La relectura del diario de abandono no coincide.");
        }
        using var ready = WindowsProbeNative.OpenPlainFile(journal, temporary, FileAccess.Read, FileShare.None, rename: true);
        WindowsProbeNative.RenameNew(ready.SafeFileHandle, journal, name);
    }

    private VersionAbandonPlan ReadAbandonPlan(SafeFileHandle journal, string generation)
    {
        using var file = WindowsProbeNative.OpenPlainFile(journal, "plan", FileAccess.Read, FileShare.Read);
        if (file.Length is < 33 or > 16 * 1024 + 32) throw new InvalidDataException("El diario de abandono está incompleto o es excesivo.");
        byte[] bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (!SHA256.HashData(bytes.AsSpan(32)).AsSpan().SequenceEqual(bytes.AsSpan(0, 32)))
            throw new InvalidDataException("El diario de abandono está dañado.");
        var plan = JsonSerializer.Deserialize<VersionAbandonPlan>(bytes.AsSpan(32), AbandonJson)
            ?? throw new InvalidDataException("Falta el plan de abandono.");
        if (plan.Version != 1 || plan.Marker != binding.Marker || !plan.Identity.AsSpan().SequenceEqual(binding.Identity) ||
            plan.Review.Generation != generation || plan.Review.DocumentHash is not { Length: 32 } ||
            plan.Review.RegistryHash is not { Length: 32 } || plan.Review.SourceIdentity is not { Length: 24 } ||
            plan.Review.Files < 0 || plan.Review.Artifacts.Length > 5 ||
            plan.Review.Artifacts.Select(ArtifactArchiveName).Distinct(StringComparer.Ordinal).Count() != plan.Review.Artifacts.Length)
            throw new InvalidDataException("El abandono corresponde a otro almacén o tiene referencias inválidas.");
        _ = GenerationTime(generation);
        foreach (var artifact in plan.Review.Artifacts)
            if (artifact.Area is not (PrimaryDirectory or ReplicaDirectory or CommitDirectory) ||
                (artifact.Area == CommitDirectory ? artifact.Name != generation + ".commit.tmp" :
                    artifact.Name != generation + ".jsonl" && artifact.Name != generation + ".jsonl.tmp") ||
                artifact.Identity is not { Length: 24 } || artifact.Hash is not { Length: 32 } || artifact.Length is < 0 or > BatchCatalog.VersionMaximumBytes)
                throw new InvalidDataException("El catálogo conservado no corresponde al punto abandonado.");
        return plan;
    }

    private static string ArtifactArchiveName(AbandonArtifact artifact) => artifact.Area + (artifact.Name.EndsWith(".tmp", StringComparison.Ordinal) ? ".tmp" : ".jsonl");
    private static void RequireAbandonArtifact(FileStream file, AbandonArtifact artifact)
    {
        WindowsProbeNative.RequireSupportedData(file.SafeFileHandle, artifact.Name);
        if (!WindowsProbeNative.DirectoryIdentity(file.SafeFileHandle).AsSpan().SequenceEqual(artifact.Identity) ||
            file.Length != artifact.Length || !SHA256.HashData(file).AsSpan().SequenceEqual(artifact.Hash))
            throw new InvalidDataException("Un catálogo pendiente cambió desde la revisión.");
    }
    private static bool AbandonComplete(SafeFileHandle journal, VersionAbandonPlan plan)
    {
        if (!WindowsProbeNative.PlainFileExists(journal, "complete")) return false;
        using var file = WindowsProbeNative.OpenPlainFile(journal, "complete", FileAccess.Read, FileShare.Read);
        byte[] actual = new byte[32];
        file.ReadExactly(actual);
        if (file.ReadByte() != -1 || !actual.AsSpan().SequenceEqual(SHA256.HashData(EncodeAbandonPlan(plan))))
            throw new InvalidDataException("La confirmación del abandono está dañada.");
        return true;
    }
    private void RequireNoAbandonInProgress(SafeFileHandle root)
    {
        SafeFileHandle all;
        try { all = WindowsProbeNative.OpenPlainDirectory(root, AbandonDirectory); }
        catch (FileNotFoundException)
        {
            if (registeredGenerations && GenerationRegistry.ReadCommonPrefix(root, binding).Abandoned.Count != 0)
                throw new InvalidDataException("Faltan los diarios de abandonos registrados. Se conserva el almacén para revisión.");
            return;
        }
        RegistryState? registered = null;
        var observed = new HashSet<string>(StringComparer.Ordinal);
        using (all)
        foreach (var entry in WindowsProbeNative.ReadDirectory(all).Where(entry => entry.Name is not ("." or "..")))
        {
            using var journal = WindowsProbeNative.OpenPlainDirectory(all, entry.Name);
            if (!WindowsProbeNative.PlainFileExists(journal, "plan"))
                throw new InvalidDataException("Hay un abandono pendiente. Reanúdalo antes de copiar o limpiar.");
            var plan = ReadAbandonPlan(journal, entry.Name);
            if (!AbandonComplete(journal, plan))
                throw new InvalidDataException("Hay un abandono pendiente. Reanúdalo antes de copiar o limpiar.");
            registered ??= GenerationRegistry.ReadCommonPrefix(root, binding);
            if (!registered.Abandoned.TryGetValue(entry.Name, out var hash) || !hash.AsSpan().SequenceEqual(plan.Review.DocumentHash))
                throw new InvalidDataException("Falta la transición esperada de un abandono confirmado. Se conserva el almacén para revisión.");
            observed.Add(entry.Name);
        }
        registered ??= GenerationRegistry.ReadCommonPrefix(root, binding);
        if (!observed.SetEquals(registered.Abandoned.Keys))
            throw new InvalidDataException("Falta un diario de abandono esperado. Se conserva el almacén para revisión.");
    }

    private BatchPlan ReadAbandonedIntent(SafeFileHandle scope, SafeFileHandle root, SafeFileHandle pending, string generation, byte[] hash)
    {
        using var all = WindowsProbeNative.OpenPlainDirectory(root, AbandonDirectory);
        using var journal = WindowsProbeNative.OpenPlainDirectory(all, generation);
        var journalPlan = ReadAbandonPlan(journal, generation);
        if (!AbandonComplete(journal, journalPlan) || !journalPlan.Review.DocumentHash.AsSpan().SequenceEqual(hash) || IsCommitted(root, generation))
            throw new InvalidDataException("El abandono no conserva una prueba completa y coherente.");
        foreach (var artifact in journalPlan.Review.Artifacts)
        {
            using var file = WindowsProbeNative.OpenPlainFile(journal, ArtifactArchiveName(artifact), FileAccess.Read, FileShare.Read);
            RequireAbandonArtifact(file, artifact);
        }
        var original = ReadIntent(scope, root, pending, generation);
        if (!original.SourceIdentity.AsSpan().SequenceEqual(journalPlan.Review.SourceIdentity) || original.Files.Length != journalPlan.Review.Files ||
            !BatchCatalog.WriteMetadata(Stream.Null, original, generation).AsSpan().SequenceEqual(hash))
            throw new InvalidDataException("El inventario abandonado no coincide con sus referencias.");
        return original;
    }
}
