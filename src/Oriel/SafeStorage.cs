using System.Runtime.Versioning;
using System.Text;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>Encrypts and decrypts strings with the OS's per-user secret store; Electron's <c>safeStorage</c>.</summary>
/// <remarks>Windows uses DPAPI. The ciphertext format is Oriel's own, not Electron's (Chromium's os_crypt writes "v10" plus
/// AES-256-GCM under a DPAPI-wrapped key in Local State), so data is not interchangeable with Electron apps.
/// Other platforms report encryption as unavailable.</remarks>
public static class SafeStorage
{
    internal static ISafeStorageBackend Backend { get; set; } = OperatingSystem.IsWindows()
        ? new DpapiSafeStorageBackend()
        : new UnsupportedSafeStorageBackend();

    /// <summary>Whether encryption is available; <c>safeStorage.isEncryptionAvailable()</c>.</summary>
    public static bool IsEncryptionAvailable() => Backend.IsEncryptionAvailable;

    /// <summary>Encrypts <paramref name="plainText"/> (as UTF-8); <c>safeStorage.encryptString()</c>.</summary>
    public static byte[] EncryptString(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        return Backend.Encrypt(Encoding.UTF8.GetBytes(plainText));
    }

    /// <summary>Decrypts <see cref="EncryptString"/>'s output; <c>safeStorage.decryptString()</c>.
    /// Throws <see cref="InvalidOperationException"/> when it cannot, as Electron does.</summary>
    public static string DecryptString(byte[] encrypted)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        var plain = Backend.Decrypt(encrypted)
            ?? throw new InvalidOperationException("Error while decrypting the ciphertext provided to safeStorage.decryptString.");
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Linux only: allows a plain-text fallback; <c>safeStorage.setUsePlainTextEncryption()</c>. No-op elsewhere.</summary>
    public static void SetUsePlainTextEncryption(bool usePlainText) { }

    /// <summary>Linux only: the secret store in use; <c>safeStorage.getSelectedStorageBackend()</c>. "unknown" elsewhere.</summary>
    public static string GetSelectedStorageBackend() => "unknown";
}

/// <summary>A platform's secret store behind <see cref="SafeStorage"/>.</summary>
internal interface ISafeStorageBackend
{
    /// <summary>Whether <see cref="Encrypt"/> and <see cref="Decrypt"/> work on this platform.</summary>
    bool IsEncryptionAvailable { get; }

    /// <summary>Encrypts bytes for the current user.</summary>
    byte[] Encrypt(byte[] plain);

    /// <summary>Decrypts bytes from <see cref="Encrypt"/>; null when they cannot be.</summary>
    byte[]? Decrypt(byte[] encrypted);
}

/// <summary>DPAPI backend.</summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiSafeStorageBackend : ISafeStorageBackend
{
    // Stops other apps of the same user decrypting blindly; DPAPI alone trusts every process of the user.
    private static readonly byte[] s_entropy = "Oriel.SafeStorage"u8.ToArray();

    public bool IsEncryptionAvailable => true;

    public byte[] Encrypt(byte[] plain) => WindowsDpapi.Protect(plain, s_entropy);

    public byte[]? Decrypt(byte[] encrypted) => WindowsDpapi.Unprotect(encrypted, s_entropy);
}

/// <summary>Backend for platforms without an implementation yet.</summary>
internal sealed class UnsupportedSafeStorageBackend : ISafeStorageBackend
{
    public bool IsEncryptionAvailable => false;

    public byte[] Encrypt(byte[] plain) => throw new PlatformNotSupportedException("safeStorage is not implemented on this platform.");

    public byte[]? Decrypt(byte[] encrypted) => throw new PlatformNotSupportedException("safeStorage is not implemented on this platform.");
}
