using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal static class CloudSecret
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        internal int Size;
        internal nint Data;
    }

    internal static unsafe byte[] Transform(byte[] input, byte[] purpose, bool encrypt)
    {
        fixed (byte* data = input, entropy = purpose)
        {
            var source = new Blob { Size = input.Length, Data = (nint)data };
            var context = new Blob { Size = purpose.Length, Data = (nint)entropy };
            const uint forbidPrompt = 1;
            bool ok = encrypt
                ? CryptProtectData(ref source, 0, ref context, 0, 0, forbidPrompt, out var output)
                : CryptUnprotectData(ref source, 0, ref context, 0, 0, forbidPrompt, out output);
            if (!ok) throw new IOException("Windows no pudo abrir las credenciales de nube de este usuario.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
            try
            {
                if (output.Size is < 1 or > 60_000) throw new InvalidDataException("La credencial excede el tamaño permitido.");
                return new ReadOnlySpan<byte>((void*)output.Data, output.Size).ToArray();
            }
            finally
            {
                if (output.Size > 0) CryptographicOperations.ZeroMemory(new Span<byte>((void*)output.Data, output.Size));
                LocalFree(output.Data);
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, nint description, ref Blob entropy, nint reserved,
        nint prompt, uint flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, ref Blob entropy, nint reserved,
        nint prompt, uint flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
