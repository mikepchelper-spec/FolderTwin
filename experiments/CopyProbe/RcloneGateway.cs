using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

internal sealed class RcloneGateway : IDisposable
{
    private readonly Process server;
    private readonly SafeFileHandle lifetime;
    internal string Endpoint { get; }
    internal KopiaS3Credentials Credentials { get; }

    private RcloneGateway(Process server, SafeFileHandle lifetime, string endpoint, KopiaS3Credentials credentials)
        => (this.server, this.lifetime, Endpoint, Credentials) = (server, lifetime, endpoint, credentials);

    internal static async Task<RcloneGateway> OpenAsync(string executable, CloudDestination destination,
        CloudCredentials account, bool create, bool readOnly, CancellationToken token, string? labRoot = null)
    {
        var settings = new Dictionary<string, string>();
        string folder = destination.Prefix.TrimEnd('/');
        string remote = "ftcloud:" + folder;
        if (labRoot is not null)
        {
            using var fence = new DirectoryFence(labRoot);
            remote = ":local:" + Path.Combine(labRoot, folder.Replace('/', Path.DirectorySeparatorChar));
        }
        else
        {
            string login = await ObscureAsync(account.SecretKey);
            if (destination.Provider == CloudProvider.Filen)
            {
                settings["RCLONE_CONFIG_FTCLOUD_TYPE"] = "filen";
                settings["RCLONE_CONFIG_FTCLOUD_EMAIL"] = account.AccessKey;
                settings["RCLONE_CONFIG_FTCLOUD_PASSWORD"] = login;
                settings["RCLONE_CONFIG_FTCLOUD_API_KEY"] = await ObscureAsync(account.SessionToken!);
            }
            else
            {
                settings["RCLONE_CONFIG_FTCLOUD_TYPE"] = "mega";
                settings["RCLONE_CONFIG_FTCLOUD_USER"] = account.AccessKey;
                settings["RCLONE_CONFIG_FTCLOUD_PASS"] = login;
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        if (create)
            await RunAsync(["mkdir", remote.TrimEnd('/', '\\') + "/foldertwin"], settings, null, timeout.Token);

        var key = new KopiaS3Credentials("ft" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        settings["RCLONE_AUTH_KEY"] = $"\"{key.AccessKey},{key.SecretKey}\"";
        using var setup = new SafeFileHandle(CreateJobObject(0, null), ownsHandle: true);
        if (setup.IsInvalid || !SetInformationJobObject(setup, 9, new ExtendedLimits { Flags = 0x2000 }, (uint)Marshal.SizeOf<ExtendedLimits>()))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        var start = StartInfo(["serve", "s3", remote, "--addr", "127.0.0.1:0", "--log-level", "INFO",
            "--vfs-cache-mode", "off", "--dir-cache-time", "0s", .. readOnly ? new[] { "--read-only" } : []], settings);
        var process = Process.Start(start) ?? throw new IOException("No se pudo abrir el puente local de rclone.");
        try
        {
            if (!AssignProcessToJobObject(setup, process.SafeHandle))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            process.StandardInput.Close();
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            while (await process.StandardError.ReadLineAsync(timeout.Token) is { } line)
            {
                var match = Regex.Match(line, @"http://127\.0\.0\.1:([1-9][0-9]{0,4})(?:/|\s|$)");
                if (!match.Success || !ushort.TryParse(match.Groups[1].Value, out ushort port)) continue;
                _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
                var owner = new SafeFileHandle(setup.DangerousGetHandle(), ownsHandle: true);
                setup.SetHandleAsInvalid();
                return new(process, owner, $"127.0.0.1:{port}", key);
            }
            throw new IOException("rclone no pudo abrir la carpeta remota. Revisa la cuenta y el nombre de la carpeta.");
        }
        catch
        {
            Stop(process);
            process.Dispose();
            throw;
        }

        async Task<string> ObscureAsync(string secret)
        {
            string value = (await RunAsync(["obscure", "-"], new Dictionary<string, string>(), secret, token)).Trim();
            if (value.Length == 0 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
                throw new InvalidDataException("rclone no devolvió una credencial válida.");
            return value;
        }

        ProcessStartInfo StartInfo(string[] arguments, Dictionary<string, string> environment)
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var name in info.Environment.Keys.Where(name => name.StartsWith("RCLONE_", StringComparison.OrdinalIgnoreCase)).ToArray())
                info.Environment.Remove(name);
            foreach (var (name, value) in environment) info.Environment[name] = value;
            foreach (var argument in new[] { "--config", "NUL", "--ask-password=false" }.Concat(arguments))
                info.ArgumentList.Add(argument);
            return info;
        }

        async Task<string> RunAsync(string[] arguments, Dictionary<string, string> environment, string? input, CancellationToken cancellation)
        {
            using var child = Process.Start(StartInfo(arguments, environment)) ?? throw new IOException("No se pudo iniciar rclone.");
            try
            {
                var output = child.StandardOutput.ReadToEndAsync(cancellation);
                var errors = child.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellation);
                if (input is not null) await child.StandardInput.WriteLineAsync(input.AsMemory(), cancellation);
                child.StandardInput.Close();
                await Task.WhenAll(child.WaitForExitAsync(cancellation), output, errors);
                if (child.ExitCode != 0)
                    throw new IOException("rclone no pudo preparar la conexión. Revisa la cuenta y el acceso a la carpeta.");
                return await output;
            }
            finally { Stop(child); }
        }
    }

    public void Dispose()
    {
        lifetime.Dispose();
        Stop(server);
        server.Dispose();
    }

    private static void Stop(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(); }
        catch (InvalidOperationException) when (process.HasExited) { }
        process.WaitForExit(5000);
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION on the Windows x64 target.
    // The handle closes if the FolderTwin worker is terminated.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct ExtendedLimits
    {
        [FieldOffset(16)] internal uint Flags;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, in ExtendedLimits limits, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
