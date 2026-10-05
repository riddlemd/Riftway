using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>Windows DPAPI: encrypts bytes under the current user's logon credentials, the key behind <c>safeStorage</c>.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsDpapi
{
    private const uint UiForbidden = 0x1; // CRYPTPROTECT_UI_FORBIDDEN

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint Size;
        public byte* Data;
    }

    [DllImport("crypt32.dll")]
    private static extern int CryptProtectData(DataBlob* input, nint description, DataBlob* entropy, nint reserved, nint prompt, uint flags, DataBlob* output);

    [DllImport("crypt32.dll")]
    private static extern int CryptUnprotectData(DataBlob* input, nint description, DataBlob* entropy, nint reserved, nint prompt, uint flags, DataBlob* output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(void* memory);

    /// <summary>Encrypts <paramref name="data"/> for the current user, bound to <paramref name="entropy"/>; throws if DPAPI fails.</summary>
    public static byte[] Protect(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy)
        => Run(data, entropy, protect: true) ?? throw new InvalidOperationException("CryptProtectData failed.");

    /// <summary>Decrypts <see cref="Protect"/>'s output; null if it was tampered with, is another user's, or used other entropy.</summary>
    public static byte[]? Unprotect(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy)
        => Run(data, entropy, protect: false);

    private static byte[]? Run(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy, bool protect)
    {
        // An empty span pins to null, and DPAPI rejects a null buffer even at length 0.
        byte placeholder = 0;
        fixed (byte* dataPtr = data)
        fixed (byte* entropyPtr = entropy)
        {
            var input = new DataBlob { Size = (uint)data.Length, Data = dataPtr is null ? &placeholder : dataPtr };
            var salt = new DataBlob { Size = (uint)entropy.Length, Data = entropyPtr is null ? &placeholder : entropyPtr };
            DataBlob output = default;
            var ok = protect
                ? CryptProtectData(&input, 0, &salt, 0, 0, UiForbidden, &output)
                : CryptUnprotectData(&input, 0, &salt, 0, 0, UiForbidden, &output);
            if (ok == 0) return null;
            try
            {
                return new ReadOnlySpan<byte>(output.Data, (int)output.Size).ToArray();
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
    }
}
