using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

internal sealed partial class VersionStore
{
    internal string BeginRetention(RetentionPreview review, Action<RetentionStep>? checkpoint = null, DateTimeOffset? clock = null)
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.ReadWrite);
        RequireNoRetentionInProgress(store.Root);
        DateTimeOffset now = (clock ?? DateTimeOffset.UtcNow).ToUniversalTime();
        if (now < review.ReviewedUtc)
            throw new InvalidDataException("El reloj retrocedió desde la revisión de retención.");
        var inventory = AuditRetention(store.Root);
        var current = BuildRetentionPreview(inventory, review.Policy, review.ReviewedUtc);
        if (!SameReview(review, current) || review.Keep.Length == 0)
            throw new InvalidDataException("El almacén cambió desde la revisión. Vuelve a previsualizar la retención.");
        string id = Guid.NewGuid().ToString("N");
        using var quarantine = WindowsProbeNative.OpenOrCreatePlainDirectory(store.Root, RetentionJournal.DirectoryName);
        using var directory = WindowsProbeNative.OpenOrCreatePlainDirectory(quarantine, id);
        LabWorkspace.RequireEmpty(directory);
        var plan = new RetentionPlan(1, binding.Marker, binding.Identity, id, review with { ReviewedUtc = now });
        RetentionJournal.WriteFile(directory, "plan", RetentionJournal.Encode(plan));
        checkpoint?.Invoke(RetentionStep.Planned);
        ContinueRetention(store.Root, directory, plan, checkpoint, now);
        return id;
    }

    internal RetentionOperation[] ListRetention()
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.Read);
        return ReadRetentionOperations(store.Root);
    }

    internal void ResumeRetention(string id, Action<RetentionStep>? checkpoint = null, DateTimeOffset? clock = null)
    {
        WithRetention(id, (root, directory, plan) => ContinueRetention(root, directory, plan, checkpoint, clock ?? DateTimeOffset.UtcNow));
    }

    internal void UndoRetention(string id, Action<RetentionStep>? checkpoint = null)
    {
        WithRetention(id, (root, directory, plan) =>
        {
            string state = RetentionJournal.State(directory, plan);
            if (state == "undone")
                return;
            if (state is "purging" or "purged")
                throw new InvalidDataException("La purga ya comenzó. Esta operación no se puede deshacer.");
            // Finish an exact interrupted expiration event before interpreting the registry.
            if (state != "undoing")
                ApplyRetentionTransitions(root, plan, expired: true);
            else if (RetentionJournal.HasState(directory, plan, "returned"))
                ApplyRetentionTransitions(root, plan, expired: false);
            var inventory = AuditRetention(root);
            ValidatePlanReferences(plan, inventory);
            RetentionJournal.SetState(directory, plan, "undoing");
            checkpoint?.Invoke(RetentionStep.Undoing);
            foreach (string digest in plan.Review.Objects)
            {
                ReturnObject(root, directory, digest, inventory.Objects[digest]);
                checkpoint?.Invoke(RetentionStep.Returned);
            }
            RetentionJournal.SetState(directory, plan, "returned");
            VerifyObjects(root, plan.Review.Expire.SelectMany(generation => inventory.References[generation])
                .Distinct(StringComparer.Ordinal).Select(digest => inventory.Objects[digest]).ToArray());
            ApplyRetentionTransitions(root, plan, expired: false);
            checkpoint?.Invoke(RetentionStep.Retained);
            RetentionJournal.SetState(directory, plan, "undone");
            checkpoint?.Invoke(RetentionStep.Undone);
        });
    }

    internal void PurgeRetention(string id, Action<RetentionStep>? checkpoint = null, DateTimeOffset? clock = null)
    {
        WithRetention(id, (root, directory, plan) =>
        {
            string state = RetentionJournal.State(directory, plan);
            if (state == "purged")
                return;
            if (state is not ("quarantined" or "purging"))
                throw new InvalidDataException("Termina la cuarentena antes de purgarla.");
            DateTimeOffset now = (clock ?? DateTimeOffset.UtcNow).ToUniversalTime();
            DateTimeOffset completed = RetentionJournal.QuarantinedAt(directory, plan);
            if (completed < plan.Review.ReviewedUtc || now < completed.AddDays(plan.Review.Policy.GraceDays))
                throw new InvalidDataException("Aún no terminó el plazo de gracia. No se purgó ningún objeto.");
            var inventory = AuditRetention(root);
            if (inventory.Registry.Generations.Keys.Any(generation => GenerationTime(generation) > now))
                throw new InvalidDataException("El reloj actual precede a una generación. La purga queda suspendida.");
            ValidatePlanReferences(plan, inventory);
            var active = ActiveReferences(inventory);
            // Inspect every candidate before starting a new irreversible phase.
            foreach (string digest in plan.Review.Objects)
            {
                bool exists = WindowsProbeNative.PlainFileExists(directory, digest);
                if (active.Contains(digest))
                {
                    if (exists)
                        throw new InvalidDataException("Un objeto retenido sigue en cuarentena. Recupéralo antes de limpiar.");
                    continue;
                }
                if (!exists && state != "purging")
                    throw new InvalidDataException("Falta un objeto de cuarentena. Se suspende la purga.");
                if (exists)
                    VerifyObject(directory, digest, inventory.Objects[digest]);
            }
            RetentionJournal.SetState(directory, plan, "purging");
            checkpoint?.Invoke(RetentionStep.Purging);
            foreach (string digest in plan.Review.Objects.Where(digest => !active.Contains(digest)))
            {
                if (WindowsProbeNative.PlainFileExists(directory, digest))
                {
                    using (var file = WindowsProbeNative.OpenPlainFile(directory, digest, FileAccess.Read, FileShare.None, rename: true))
                    {
                        RequireObject(file, digest, inventory.Objects[digest]);
                        WindowsProbeNative.DeletePlainFile(file.SafeFileHandle);
                    }
                    checkpoint?.Invoke(RetentionStep.Deleted);
                }
            }
            RetentionJournal.SetState(directory, plan, "purged");
            checkpoint?.Invoke(RetentionStep.Purged);
        });
    }

    private void WithRetention(string id, Action<SafeFileHandle, SafeFileHandle, RetentionPlan> action)
    {
        RetentionJournal.CheckId(id);
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.ReadWrite);
        if (ReadRetentionOperations(store.Root).Any(item => item.Id != id && item.State is not ("undone" or "purged")))
            throw new InvalidDataException("Hay otra operación de limpieza pendiente.");
        using var quarantine = WindowsProbeNative.OpenPlainDirectory(store.Root, RetentionJournal.DirectoryName);
        using var directory = WindowsProbeNative.OpenPlainDirectory(quarantine, id);
        var plan = RetentionJournal.Read(directory, binding, id);
        action(store.Root, directory, plan);
    }

    private void ContinueRetention(SafeFileHandle root, SafeFileHandle directory, RetentionPlan plan, Action<RetentionStep>? checkpoint,
        DateTimeOffset now)
    {
        string state = RetentionJournal.State(directory, plan);
        if (state is "undone" or "purged" or "quarantined")
            return;
        if (state != "moving")
            throw new InvalidDataException("La operación debe reanudarse mediante deshacer o purgar.");
        // The durable plan is the only authority to finish a split replicated append.
        ApplyRetentionTransitions(root, plan, expired: true);
        checkpoint?.Invoke(RetentionStep.Expired);
        var inventory = AuditRetention(root);
        ValidatePlanReferences(plan, inventory);
        var referenced = ActiveReferences(inventory);
        using var objects = WindowsProbeNative.OpenPlainDirectory(root, ObjectDirectory);
        using var algorithm = WindowsProbeNative.OpenPlainDirectory(objects, Algorithm);
        foreach (string digest in plan.Review.Objects)
        {
            using var bucket = WindowsProbeNative.OpenPlainDirectory(algorithm, digest[..2]);
            bool inStore = WindowsProbeNative.PlainFileExists(bucket, digest);
            bool inQuarantine = WindowsProbeNative.PlainFileExists(directory, digest);
            if (inStore == inQuarantine)
                throw new InvalidDataException("La ubicación de un objeto es ambigua o falta. Se conserva para revisión.");
            if (referenced.Contains(digest))
            {
                if (inQuarantine)
                    throw new InvalidDataException("Un objeto retenido está en cuarentena. Detén la limpieza y recupéralo.");
                VerifyObject(bucket, digest, inventory.Objects[digest]);
                continue;
            }
            if (inStore)
            {
                using var file = WindowsProbeNative.OpenPlainFile(bucket, digest, FileAccess.Read, FileShare.None, rename: true);
                RequireObject(file, digest, inventory.Objects[digest]);
                WindowsProbeNative.RenameNew(file.SafeFileHandle, directory, digest);
            }
            else
                VerifyObject(directory, digest, inventory.Objects[digest]);
            checkpoint?.Invoke(RetentionStep.Moved);
        }
        DateTimeOffset completed = RetentionJournal.QuarantinedAt(directory, plan, now);
        if (now < completed || completed < plan.Review.ReviewedUtc)
            throw new InvalidDataException("El reloj retrocedió durante la cuarentena.");
        RetentionJournal.SetState(directory, plan, "quarantined");
        checkpoint?.Invoke(RetentionStep.Quarantined);
    }

    private RetentionOperation[] ReadRetentionOperations(SafeFileHandle root)
    {
        SafeFileHandle quarantine;
        try { quarantine = WindowsProbeNative.OpenPlainDirectory(root, RetentionJournal.DirectoryName); }
        catch (FileNotFoundException) { return []; }
        using (quarantine)
            return WindowsProbeNative.ReadDirectory(quarantine).Where(entry => entry.Name is not ("." or "..")).Select(entry =>
            {
                RetentionJournal.CheckId(entry.Name);
                using var directory = WindowsProbeNative.OpenPlainDirectory(quarantine, entry.Name);
                var plan = RetentionJournal.Read(directory, binding, entry.Name);
                return new RetentionOperation(entry.Name, plan.Review.ReviewedUtc, RetentionJournal.State(directory, plan), plan.Review.Objects.Length);
            }).ToArray();
    }

    private void RequireNoRetentionInProgress(SafeFileHandle root)
    {
        if (ReadRetentionOperations(root).Any(operation => operation.State is not ("undone" or "purged")))
            throw new InvalidDataException("Ya hay una cuarentena pendiente. Reanúdala, deshazla o completa su plazo de gracia.");
    }

    private static bool SameReview(RetentionPreview first, RetentionPreview second) =>
        first.Policy == second.Policy && first.QuarantineBytes == second.QuarantineBytes &&
        first.RegistryHash.AsSpan().SequenceEqual(second.RegistryHash) && first.Keep.SequenceEqual(second.Keep) &&
        first.Expire.SequenceEqual(second.Expire) && first.Objects.SequenceEqual(second.Objects);

    private static HashSet<string> ActiveReferences(RetentionInventory inventory) =>
        inventory.References.Where(pair => inventory.Registry.Generations.ContainsKey(pair.Key) && !inventory.Registry.Expired.Contains(pair.Key))
            .SelectMany(pair => pair.Value).ToHashSet(StringComparer.Ordinal);

    private static void ValidatePlanReferences(RetentionPlan plan, RetentionInventory inventory)
    {
        if (plan.Review.Keep.Any(id => !inventory.Registry.Generations.ContainsKey(id) || inventory.Registry.Expired.Contains(id)) ||
            plan.Review.Expire.Any(id => !inventory.Registry.Generations.ContainsKey(id)) ||
            plan.Review.Objects.Any(digest => !inventory.Objects.ContainsKey(digest)))
            throw new InvalidDataException("El diario referencia generaciones u objetos desconocidos.");
    }

    private void ApplyRetentionTransitions(SafeFileHandle root, RetentionPlan plan, bool expired)
    {
        var prefix = GenerationRegistry.ReadCommonPrefix(root, binding);
        foreach (string generation in plan.Review.Expire)
            if (prefix.Expired.Contains(generation) != expired)
                GenerationRegistry.SetExpired(root, binding, generation, ReadPoint(root, generation).DocumentHash, expired);
        _ = GenerationRegistry.Read(root, binding);
    }

    private static void RequireObject(FileStream input, string digest, BatchFile expected)
    {
        WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, digest);
        if (input.Length != expected.Length || !SHA256.HashData(input).AsSpan().SequenceEqual(expected.Hash))
            throw new InvalidDataException("El objeto de cuarentena no coincide con el catálogo.");
        WindowsProbeNative.RequireSupportedData(input.SafeFileHandle, digest);
    }

    private static void ReturnObject(SafeFileHandle root, SafeFileHandle directory, string digest, BatchFile expected)
    {
        using var objects = WindowsProbeNative.OpenPlainDirectory(root, ObjectDirectory);
        using var algorithm = WindowsProbeNative.OpenPlainDirectory(objects, Algorithm);
        using var bucket = WindowsProbeNative.OpenOrCreatePlainDirectory(algorithm, digest[..2]);
        bool inQuarantine = WindowsProbeNative.PlainFileExists(directory, digest);
        bool inStore = WindowsProbeNative.PlainFileExists(bucket, digest);
        if (inStore == inQuarantine)
            throw new InvalidDataException("El objeto que se quiere recuperar falta o tiene ubicaciones contradictorias.");
        if (inStore)
            VerifyObject(bucket, digest, expected);
        else
        {
            using var file = WindowsProbeNative.OpenPlainFile(directory, digest, FileAccess.Read, FileShare.None, rename: true);
            RequireObject(file, digest, expected);
            WindowsProbeNative.RenameNew(file.SafeFileHandle, bucket, digest);
        }
    }

    private bool RescueQuarantinedObject(SafeFileHandle root, string digest, BatchFile expected)
    {
        var operations = ReadRetentionOperations(root).Where(operation => operation.State is not ("purged" or "undone")).ToArray();
        if (operations.Length == 0)
            return false;
        using var quarantine = WindowsProbeNative.OpenPlainDirectory(root, RetentionJournal.DirectoryName);
        bool rescued = false;
        foreach (var operation in operations)
        {
            using var directory = WindowsProbeNative.OpenPlainDirectory(quarantine, operation.Id);
            var plan = RetentionJournal.Read(directory, binding, operation.Id);
            if (plan.Review.Objects.Contains(digest, StringComparer.Ordinal) && WindowsProbeNative.PlainFileExists(directory, digest))
            {
                if (rescued)
                    throw new InvalidDataException("Un objeto aparece en varias cuarentenas.");
                ReturnObject(root, directory, digest, expected);
                rescued = true;
            }
        }
        return rescued;
    }

    private bool WasPurged(SafeFileHandle root, string digest)
    {
        var completed = ReadRetentionOperations(root).Where(operation => operation.State is "purging" or "purged").ToArray();
        if (completed.Length == 0)
            return false;
        using var quarantine = WindowsProbeNative.OpenPlainDirectory(root, RetentionJournal.DirectoryName);
        foreach (var operation in completed)
        {
            using var directory = WindowsProbeNative.OpenPlainDirectory(quarantine, operation.Id);
            if (RetentionJournal.Read(directory, binding, operation.Id).Review.Objects.Contains(digest, StringComparer.Ordinal))
                return true;
        }
        return false;
    }
}
