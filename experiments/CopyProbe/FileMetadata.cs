using Microsoft.Win32.SafeHandles;

internal sealed record FileMetadata(DateTime LastWriteUtc, DateTime? CreationUtc = null, bool? Hidden = null)
{
    internal static FileMetadata Read(SafeFileHandle file) =>
        new(File.GetLastWriteTimeUtc(file), File.GetCreationTimeUtc(file),
            File.GetAttributes(file).HasFlag(FileAttributes.Hidden));

    internal bool IsValid() => IsSupportedTime(LastWriteUtc) &&
        (CreationUtc is { } creation ? IsSupportedTime(creation) && Hidden.HasValue : !Hidden.HasValue);

    internal void Apply(SafeFileHandle file)
    {
        if (Hidden is { } hidden)
        {
            FileAttributes current = File.GetAttributes(file);
            if (current.HasFlag(FileAttributes.Hidden) != hidden)
            {
                FileAttributes attributes = hidden
                    ? (current & ~FileAttributes.Normal) | FileAttributes.Hidden
                    : current & ~FileAttributes.Hidden;
                File.SetAttributes(file, attributes == 0 ? FileAttributes.Normal : attributes);
            }
        }
        if (CreationUtc is { } creation)
            File.SetCreationTimeUtc(file, creation);
        File.SetLastWriteTimeUtc(file, LastWriteUtc);
    }

    internal void RequireMatch(SafeFileHandle file)
    {
        if (File.GetLastWriteTimeUtc(file) != LastWriteUtc)
            throw new InvalidDataException("La fecha de modificación del archivo no coincide con la registrada.");
        if (CreationUtc is { } creation && File.GetCreationTimeUtc(file) != creation)
            throw new InvalidDataException("La fecha de creación del archivo no coincide con la registrada.");
        if (Hidden is { } hidden && File.GetAttributes(file).HasFlag(FileAttributes.Hidden) != hidden)
            throw new InvalidDataException("El atributo Oculto del archivo no coincide con el registrado.");
    }

    private static bool IsSupportedTime(DateTime value) =>
        value.Kind == DateTimeKind.Utc && value > DateTime.FromFileTimeUtc(0);
}
