internal sealed partial class VersionStore
{
    internal (string? Generation, string? OccupiedPath) CaptureReadableFiles(string source, Action<string> beforePublication,
        Action<PendingStep>? checkpoint = null)
    {
        using var scope = workspace.OpenRegisteredRoot();
        workspace.CheckRoots(source, Destination);
        using var origin = new DirectoryFence(source);
        workspace.RequireSourceIdentity(source, origin.Root);
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.ReadWrite);
        RequireNoAbandonInProgress(store.Root);
        RequireNoPendingMirror(store.Root);
        var ledger = GenerationRegistry.Read(store.Root, binding);
        var occupied = new List<string>();
        var plan = FolderBatch.AnalyzeSource(workspace.Root, source, Destination, binding,
            WindowsProbeNative.DirectoryIdentity(origin.Root), BatchCatalog.VersionMaximumEntries, observe, occupied);
        if (occupied.Count == 0 || plan.Files.Length == 0) return (null, occupied.FirstOrDefault());
        workspace.ValidatePlan(plan);
        string? last = ledger.RegistrationOrder.LastOrDefault(id => !ledger.Expired.Contains(id));
        if (last is not null)
        {
            var catalog = ReadPoint(store.Root, last, requireBoth: true);
            if (!catalog.DocumentHash.AsSpan().SequenceEqual(ledger.Generations[last]))
                throw new InvalidDataException("El punto anterior no coincide con el registro.");
            if (BatchCatalog.WriteMetadata(Stream.Null, plan, last).AsSpan().SequenceEqual(catalog.DocumentHash))
                return (null, occupied[0]);
        }
        string generation = NewGenerationId() + PartialPointSuffix;
        LabWorkspace.RequireSpace(0, LabWorkspace.AvailableSpace(store.Root));
        // A partial point never publishes or retires current files. Verify and
        // put every readable object in CAS before declaring a durable intent.
        CopyAndConfirm(store.Root, plan, generation, checkpoint: null, objectCheckpoint: null, prepareOnly: true);
        beforePublication(generation);
        using var pending = WindowsProbeNative.OpenPlainDirectory(store.Root, PendingDirectory);
        workspace.SaveIntent(plan, scope.Root, IntentFiles(pending, generation), checkpoint);
        RecordExpected(store.Root, plan, generation);
        CopyAndConfirm(store.Root, plan, generation, checkpoint: null, objectCheckpoint: null);
        return (generation, occupied[0]);
    }

    internal string? ResumePartialPoint(string source, byte[] identity)
    {
        using var scope = workspace.OpenRegisteredRoot();
        using var store = new DirectoryFence(Destination);
        using var marker = binding.OpenVerifiedMarker(store.Root);
        using var operation = OpenLock(store.Root, FileAccess.ReadWrite);
        RequireNoAbandonInProgress(store.Root);
        using var pending = WindowsProbeNative.OpenPlainDirectory(store.Root, PendingDirectory);
        var pendingIds = WindowsProbeNative.ReadDirectory(pending)
            .Select(entry => entry.Name.EndsWith(".tmp", StringComparison.Ordinal) ? entry.Name[..^4] : entry.Name)
            .Select(name => name.EndsWith(LocationSuffix, StringComparison.Ordinal) ? name[..^LocationSuffix.Length] :
                name.EndsWith(".jsonl", StringComparison.Ordinal) ? name[..^6] : "")
            .Distinct(StringComparer.Ordinal)
            .Where(id => IsPartialPoint(id) && !IsCommitted(store.Root, id)).ToArray();
        if (pendingIds.Length == 0) return null;
        if (pendingIds.Length != 1) throw new InvalidDataException("Hay más de un punto parcial pendiente.");
        string generation = pendingIds[0];
        var files = IntentFiles(pending, generation);
        if (!WindowsProbeNative.PlainFileExists(pending, files.Location))
        {
            string name = WindowsProbeNative.PlainFileExists(pending, files.Metadata) ? files.Metadata : files.Metadata + ".tmp";
            CatalogSnapshot metadata;
            using (var data = WindowsProbeNative.OpenPlainFile(pending, name, FileAccess.Read, FileShare.Read))
                metadata = BatchCatalog.Parse(data, generation);
            if (metadata.Binding.Marker != binding.Marker || !metadata.Binding.Identity.AsSpan().SequenceEqual(binding.Identity))
                throw new InvalidDataException("El punto parcial corresponde a otro destino.");
            var draft = new BatchPlan(workspace.Root, source, Destination, identity, binding, metadata.Directories, metadata.Files);
            workspace.ValidatePlan(draft);
            VerifyObjects(store.Root, draft.Files);
            workspace.SaveIntent(draft, scope.Root, files);
        }
        var plan = ReadIntent(scope.Root, store.Root, pending, generation);
        // These objects were staged before writing the intent. Resumption needs
        // only the bound destination; missing objects never authorize a new read.
        RecordExpected(store.Root, plan, generation);
        CopyAndConfirm(store.Root, plan, generation, checkpoint: null, objectCheckpoint: null);
        return generation;
    }
}
