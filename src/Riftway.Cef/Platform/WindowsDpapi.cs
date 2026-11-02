using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>Windows DPAPI: encrypts bytes under the current user's logon credentials, the key behind <c>safeStorage</c>.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsDpapi
{
    private const uint UiForbidden = 0x1; // CRYPTPROTECT_UI_FORBIDDEN
    private const uint Audit = 0x10; // CRYPTPROTECT_AUDIT

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint Size;
        public byte* Data;
    }

    [DllImport("crypt32.dll")]
    private static extern int CryptProtectData(DataBlob* input, char* description, DataBlob* entropy, nint reserved, nint prompt, uint flags, DataBlob* output);

    [DllImport("crypt32.dll")]
    private static extern int CryptUnprotectData(DataBlob* input, nint description, DataBlob* entropy, nint reserved, nint prompt, uint flags, DataBlob* output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(void* memory);

    /// <summary>Encrypts <paramref name="data"/> for the current user, bound to <paramref name="entropy"/>; throws if DPAPI fails.</summary>
    public static byte[] Protect(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy)
        => Run(data, entropy, null, UiForbidden, protect: true) ?? throw new InvalidOperationException("CryptProtectData failed.");

    /// <summary>Decrypts <see cref="Protect"/>'s output; null if it was tampered with, is another user's, or used other entropy.</summary>
    public static byte[]? Unprotect(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy)
        => Run(data, entropy, null, UiForbidden, protect: false);

    /// <summary>Encrypts as Chromium's os_crypt wraps its key: no entropy, <paramref name="description"/> stored with the
    /// blob, and <c>CRYPTPROTECT_AUDIT</c>. Null if DPAPI fails.</summary>
    public static byte[]? ProtectAudited(ReadOnlySpan<byte> data, string description)
        => Run(data, default, description, Audit, protect: true);

    /// <summary>Decrypts with no entropy and no flags, as Chromium's os_crypt does; null if DPAPI refuses.</summary>
    public static byte[]? Unprotect(ReadOnlySpan<byte> data) => Run(data, default, null, 0, protect: false);

    private static byte[]? Run(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy, string? description, uint flags, bool protect)
    {
        // An empty span pins to null, and DPAPI rejects a null buffer even at length 0.
        byte placeholder = 0;
        fixed (byte* dataPtr = data)
        fixed (byte* entropyPtr = entropy)
        fixed (char* descriptionPtr = description)
        {
            var input = new DataBlob { Size = (uint)data.Length, Data = dataPtr is null ? &placeholder : dataPtr };
            var salt = new DataBlob { Size = (uint)entropy.Length, Data = entropyPtr is null ? &placeholder : entropyPtr };
            DataBlob output = default;
            var ok = protect
                ? CryptProtectData(&input, descriptionPtr, &salt, 0, 0, flags, &output)
                : CryptUnprotectData(&input, 0, &salt, 0, 0, flags, &output);
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
