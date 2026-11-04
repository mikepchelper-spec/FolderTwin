using System.ComponentModel;
using System.Text.Json;

internal static class VersionRecovery
{
    internal static int Run(string workspacePath, string storeName, string generation, string outputName)
    {
        try
        {
            var workspace = LabWorkspace.Open(workspacePath);
            string destination = Path.Combine(workspace.Root, storeName);
            string output = Path.Combine(workspace.Root, outputName);
            var store = VersionStore.Open(workspace, destination);
            var catalog = store.Restore(generation, output);
            Console.WriteLine($"Restored {catalog.Files.Length} files. Degraded catalog replica = {catalog.Degraded}.");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            ArgumentException or JsonException or Win32Exception)
        {
            Console.Error.WriteLine("Recovery requires review. " + error.Message);
            return 1;
        }
    }
}
