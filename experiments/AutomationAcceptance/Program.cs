using System.Security.Cryptography;

if (args is ["--drive-aliases"])
    DriveAliasProbe.Run();
else if (args.Length >= 2 && File.Exists(args[1]) && args[0] is "--waiting-controls" or "--daily-performance")
{
    string fullPath = Path.GetFullPath(args[1]);
    string core = Path.Combine(Path.GetDirectoryName(fullPath)!, "CopyProbe.dll");
    using var package = File.OpenRead(core);
    using var referenced = File.OpenRead(typeof(LabWorkspace).Assembly.Location);
    if (!SHA256.HashData(package).AsSpan().SequenceEqual(SHA256.HashData(referenced)))
        throw new InvalidDataException("The executable and the referenced core must come from the same package.");
    if (args is ["--waiting-controls", _]) await WaitingControlsProbe.RunAsync(fullPath);
    else if (args is ["--daily-performance", _, _, _] or ["--daily-performance", _, _, _, "prepared"] &&
        int.TryParse(args[2], out int files) && files is 4096 or 329000 &&
        Enum.TryParse<ProductBackupMode>(args[3], out var mode) && Enum.IsDefined(mode))
        await DailyPerformanceProbe.RunAsync(fullPath, files, mode, args.Length == 5 || files == 329000);
    else return 2;
}
else
{
    Console.Error.WriteLine("Use --drive-aliases, --waiting-controls <FolderTwin.exe>, or --daily-performance <FolderTwin.exe> <4096|329000> <mode> [prepared].");
    return 2;
}
return 0;
