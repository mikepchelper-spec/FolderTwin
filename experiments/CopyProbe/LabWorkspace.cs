using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal sealed record WorkspacePreview(BatchPlan Plan, ulong AvailableBytes);
internal sealed record WorkspaceJob(string Destination, DestinationBinding Binding, int FileCount, string? Source)
{
    internal bool Confirmed => Source is null;
}

// Explicitly created, disposable workspaces. Never adopt an existing directory as the workspace.
internal sealed class LabWorkspace
{
    internal const string Prefix = "FolderTwin-Workspace-";
    internal const string MarkerName = ".foldertwin-workspace";
    internal const ulong ReserveBytes = 1024 * 1024;
    private static ReadOnlySpan<byte> MarkerMagic => "FTWORK01"u8;
    private readonly byte[] identity;

    internal string Root { get; }

    private LabWorkspace(string root, byte[] identity)
    {
        Root = root;
        this.identity = identity;
    }

    internal static LabWorkspace Create()
    {
        string root = Directory.CreateTempSubdirectory(Prefix).FullName;
        using var fence = new DirectoryFence(root);
        byte[] identity = WindowsProbeNative.DirectoryIdentity(fence.Root);
        using var marker = WindowsProbeNative.OpenPlainFile(fence.Root, MarkerName, FileAccess.Write, FileShare.None, FileMode.CreateNew);
        marker.Write(MarkerMagic);
        marker.Write(identity);
        marker.Flush(flushToDisk: true);
        return new(root, identity);
    }

    internal static LabWorkspace Open(string root)
    {
        string relative = Path.GetRelativePath(Path.GetTempPath(), root);
        if (!Path.IsPathFullyQualified(root) || Path.GetFullPath(root) != root || relative != Path.GetFileName(root) ||
            !relative.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Elige un espacio desechable creado por FolderTwin.");
        using var fence = new DirectoryFence(root);
        byte[] identity = WindowsProbeNative.DirectoryIdentity(fence.Root);
        CheckMarker(fence.Root, identity);
        return new(root, identity);
    }

    internal WorkspacePreview Analyze(string source, string destination)
    {
        using var root = OpenRegisteredRoot();
        CheckRoots(source, destination);
        using var target = new DirectoryFence(destination);
        var binding = DestinationBinding.Capture(target.Root);
        RequireEmpty(target.Root);
        var plan = FolderBatch.Prepare(Root, source, destination, binding, unregisteredDestination: true);
        return new(plan, AvailableSpace(target.Root));
    }

    internal BatchResult Start(WorkspacePreview preview, CancellationToken cancellationToken = default,
        IProgress<BatchProgress>? progress = null, Func<ulong>? measureAvailable = null,
        Action<BatchProgress>? waitBetweenFiles = null)
    {
        var plan = preview.Plan;
        using var root = OpenRegisteredRoot();
        if (plan.FixtureRoot != Root || plan.SourceBinding is not null)
            throw new InvalidDataException("El análisis pertenece a otro espacio.");
        CheckRoots(plan.Source, plan.Destination);
        FolderBatch.ValidateEntries(plan.Files, plan.Directories);
        if (cancellationToken.IsCancellationRequested)
            return new([], Cancelled: true);
        using var target = new DirectoryFence(plan.Destination);
        plan.Target.RequireDirectoryIdentity(target.Root);
        if (!WindowsProbeNative.PlainFileExists(target.Root, DestinationBinding.MarkerName))
        {
            using var source = new DirectoryFence(plan.Source);
            if (!WindowsProbeNative.DirectoryIdentity(source.Root).AsSpan().SequenceEqual(plan.SourceIdentity))
                throw new InvalidDataException("La carpeta de origen cambió desde el análisis.");
            RequireEmpty(target.Root);
            RequireSpace(plan.Bytes, measureAvailable?.Invoke() ?? AvailableSpace(target.Root));
            plan.Target.WriteNewMarker(target.Root);
        }
        return PendingBatch.Start(plan, cancellationToken, progress: progress, waitBetweenFiles: waitBetweenFiles);
    }

    internal WorkspaceJob OpenJob(string destination)
    {
        using var root = OpenRegisteredRoot();
        CheckChild(destination);
        using var target = new DirectoryFence(destination);
        if (WindowsProbeNative.PlainFileExists(target.Root, BatchCatalog.CommitName))
        {
            var catalog = BatchCatalog.Read(destination);
            return new(destination, catalog.Binding, catalog.Files.Length, Source: null);
        }
        var plan = PendingBatch.Read(Root, destination);
        CheckRoots(plan.Source, destination);
        return new(destination, plan.Target, plan.Files.Length, plan.Source);
    }

    internal BatchResult Resume(WorkspaceJob job, CancellationToken cancellationToken = default,
        IProgress<BatchProgress>? progress = null, Action<BatchProgress>? waitBetweenFiles = null)
    {
        using var root = OpenRegisteredRoot();
        if (job.Source is not { } source)
            throw new InvalidDataException("El lote ya está confirmado.");
        CheckRoots(source, job.Destination);
        return PendingBatch.Resume(Root, job.Destination, cancellationToken, validate: plan =>
        {
            if (plan.Source != source)
                throw new InvalidDataException("El origen de la sesión cambió desde que se abrió.");
            RequireSameBinding(job.Binding, plan.Target);
        }, finishCurrentFileOnCancel: true, progress: progress, waitBetweenFiles: waitBetweenFiles);
    }

    internal BatchResult Verify(WorkspaceJob job, IProgress<BatchProgress>? progress = null)
    {
        using var root = OpenRegisteredRoot();
        CheckChild(job.Destination);
        return BatchCatalog.Verify(job.Destination, job.Binding, progress);
    }

    internal AttemptLog ReadHistory(string destination, DestinationBinding binding)
    {
        using var root = OpenRegisteredRoot();
        CheckChild(destination);
        return AttemptHistory.Read(Root, binding, destination);
    }

    internal void AppendHistory(string destination, DestinationBinding binding, AttemptOperation operation, BatchResult result)
    {
        using var root = OpenRegisteredRoot();
        CheckChild(destination);
        AttemptHistory.Append(Root, binding, operation, result, destination);
    }

    internal BatchResult Restore(WorkspaceJob job, string output, IProgress<BatchProgress>? progress = null)
    {
        using var root = OpenRegisteredRoot();
        CheckRoots(job.Destination, output);
        using var target = new DirectoryFence(output);
        RequireEmpty(target.Root);
        var catalog = BatchCatalog.Read(job.Destination);
        RequireSameBinding(job.Binding, catalog.Binding);
        var binding = DestinationBinding.Capture(target.Root);
        if (binding.Identity.AsSpan().SequenceEqual(catalog.Binding.Identity))
            throw new InvalidDataException("El respaldo y el destino de restauración son la misma carpeta.");
        var plan = catalog.RestoreTo(Root, job.Destination, output, binding);
        RequireSpace(plan.Bytes, AvailableSpace(target.Root));
        binding.WriteNewMarker(target.Root);
        var result = FolderBatch.Run(plan, progress: progress);
        return catalog.Degraded
            ? result with { Note = "Se usó una réplica íntegra. La otra copia del catálogo necesita revisión." }
            : result;
    }

    private DirectoryFence OpenRegisteredRoot()
    {
        var fence = new DirectoryFence(Root);
        try
        {
            if (!WindowsProbeNative.DirectoryIdentity(fence.Root).AsSpan().SequenceEqual(identity))
                throw new InvalidDataException("El espacio desechable cambió de identidad.");
            CheckMarker(fence.Root, identity);
            return fence;
        }
        catch
        {
            fence.Dispose();
            throw;
        }
    }

    private static void CheckMarker(SafeFileHandle directory, byte[] identity)
    {
        using var marker = WindowsProbeNative.OpenPlainFile(directory, MarkerName, FileAccess.Read, FileShare.Read);
        Span<byte> bytes = stackalloc byte[32];
        if (marker.ReadAtLeast(bytes, bytes.Length, false) != bytes.Length || marker.ReadByte() != -1 ||
            !bytes[..8].SequenceEqual(MarkerMagic) || !bytes[8..].SequenceEqual(identity))
            throw new InvalidDataException("El registro del espacio desechable no corresponde a esta carpeta.");
    }

    private void CheckRoots(string source, string destination)
    {
        CheckChild(source);
        CheckChild(destination);
        FolderBatch.ValidateRoots(Root, source, destination);
    }

    internal void CheckChild(string path)
    {
        string name = Path.GetFileName(path);
        DirectoryFence.CheckName(name);
        if (name is "." or ".." || name.StartsWith(FolderBatch.ControlPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(path, Path.Combine(Root, name), StringComparison.Ordinal))
            throw new InvalidDataException("Elige una subcarpeta directa del espacio desechable.");
    }

    private static void RequireSameBinding(DestinationBinding expected, DestinationBinding actual)
    {
        if (expected.Marker != actual.Marker || !expected.Identity.AsSpan().SequenceEqual(actual.Identity))
            throw new InvalidDataException("El respaldo cambió de identidad.");
    }

    private static void RequireEmpty(SafeFileHandle directory)
    {
        WindowsProbeNative.RequireSupportedData(directory, "destino");
        if (WindowsProbeNative.ReadDirectory(directory).Any(entry => entry.Name is not ("." or "..")))
            throw new InvalidDataException("Elige una carpeta de destino vacía.");
    }

    private static void RequireSpace(long bytes, ulong available)
    {
        if ((ulong)bytes + ReserveBytes > available)
            throw new IOException("El espacio disponible no alcanza para el contenido y la reserva del laboratorio.");
    }

    private static ulong AvailableSpace(SafeFileHandle directory)
    {
        if (!GetDiskFreeSpaceEx(WindowsProbeNative.VolumePath(directory), out ulong available, out _, out _))
            throw new IOException("No se pudo consultar el espacio disponible en el destino.", new Win32Exception(Marshal.GetLastPInvokeError()));
        return available;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}
