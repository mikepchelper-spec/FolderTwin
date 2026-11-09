using System.Security.Cryptography;

if (args is ["--drive-aliases"])
    DriveAliasProbe.Run();
else if (args is ["--waiting-controls", var executable] && File.Exists(executable))
{
    string fullPath = Path.GetFullPath(executable);
    string core = Path.Combine(Path.GetDirectoryName(fullPath)!, "CopyProbe.dll");
    using var package = File.OpenRead(core);
    using var referenced = File.OpenRead(typeof(LabWorkspace).Assembly.Location);
    if (!SHA256.HashData(package).AsSpan().SequenceEqual(SHA256.HashData(referenced)))
        throw new InvalidDataException("The executable and the referenced core must come from the same package.");
    await WaitingControlsProbe.RunAsync(executable);
}
else
{
    Console.Error.WriteLine("Use --drive-aliases or --waiting-controls <FolderTwin.exe> from a verified package.");
    return 2;
}
return 0;
