internal sealed partial class ProductJobRegistry
{
    internal IDisposable BeginIndependentRecovery(DedicatedDestinationReview target)
    {
        var held = new List<IDisposable>();
        try
        {
            held.Add(OpenLock());
            string output = Path.Combine(target.Parent.VolumePath, target.FolderName);
            if (LocalJobRoots.Overlap(output, volumePath) || LocalJobRoots.Overlap(output, target.Source.VolumePath))
                throw new InvalidDataException("Recupera fuera de la configuración y del respaldo elegido.");
            bool unknownPaths = false;
            foreach (var entry in WindowsProbeNative.ReadDirectory(directory.Root))
            {
                if (!Guid.TryParseExact(entry.Name, "N", out Guid id)) continue;
                var job = OpenJob(id);
                held.Add(job);
                held.Add(WindowsProbeNative.OpenPlainFile(job, "execution.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate));
                held.Add(WindowsProbeNative.OpenPlainFile(job, "scheduled.lock", FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate));
                string source, destination;
                try
                {
                    bool confirmed = WindowsProbeNative.PlainFileExists(job, CommitName);
                    var intent = ReadIntent(job, id, allowDraft: !confirmed);
                    source = confirmed ? ReadConfirmed(job, intent).Source.VolumePath : intent.Source.VolumePath;
                    destination = TargetPath(intent);
                }
                catch (Exception error) when (VersionJob.Expected(error)) { unknownPaths = true; continue; }
                if (LocalJobRoots.Overlap(output, source) || LocalJobRoots.Overlap(output, destination))
                    throw new InvalidDataException("La carpeta recuperada debe quedar fuera del origen y destino de los trabajos.");
            }
            // Job roots are dedicated subdirectories. A fresh child of a volume
            // root cannot be inside an existing job's unknown directory.
            if (unknownPaths && !Path.TrimEndingDirectorySeparator(target.Parent.VolumePath).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetPathRoot(target.Parent.VolumePath)!), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Hay configuraciones dañadas cuyas carpetas no se pueden comprobar. " +
                    "Elige la raíz de un disco, como D:\\, para crear una carpeta de recuperación independiente.");
            return new IndependentRecoveryLease(held);
        }
        catch
        {
            foreach (var item in held.AsEnumerable().Reverse()) item.Dispose();
            throw;
        }
    }

    private sealed class IndependentRecoveryLease(List<IDisposable> held) : IDisposable
    {
        public void Dispose()
        {
            foreach (var item in held.AsEnumerable().Reverse()) item.Dispose();
            held.Clear();
        }
    }
}
