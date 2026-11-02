using System.Security.Cryptography;

namespace Riftway;

/// <summary>Chromium's os_crypt format on macOS and Linux: a three-byte version tag, then AES-128-CBC with PKCS#7 padding
/// and an IV of 16 spaces, under a key from PBKDF2-HMAC-SHA1 with the salt "saltysalt" (<c>os_crypt_mac.mm</c>,
/// <c>os_crypt_linux.cc</c>, <c>encryptor.cc</c>).</summary>
internal static class OsCryptCbc
{
    internal const int KeySize = 16;

    private static readonly byte[] s_iv = "                "u8.ToArray();
    private static readonly byte[] s_salt = "saltysalt"u8.ToArray();

    /// <summary>Linux's "v10" key: PBKDF2 of "peanuts", one iteration (<c>kV10Key</c>).</summary>
    internal static readonly byte[] V10Key = [0xfd, 0x62, 0x1f, 0xe5, 0xa2, 0xb4, 0x02, 0x53, 0x9d, 0xfa, 0x14, 0x7c, 0xa9, 0x27, 0x27, 0x78];

    /// <summary>PBKDF2 of "", one iteration: the key some old builds encrypted with by mistake (<c>kEmptyKey</c>,
    /// crbug.com/40055416), tried when the right key fails.</summary>
    internal static readonly byte[] EmptyKey = [0xd0, 0xd0, 0xec, 0x9c, 0x7d, 0x77, 0xd4, 0x3a, 0xc5, 0x41, 0x87, 0xfa, 0x48, 0x18, 0xd1, 0x7f];

    /// <summary>The 16-byte key for <paramref name="password"/>: 1003 iterations on macOS, 1 on Linux.</summary>
    internal static byte[] DeriveKey(ReadOnlySpan<byte> password, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, s_salt, iterations, HashAlgorithmName.SHA1, KeySize);

    /// <summary><paramref name="tag"/> followed by the ciphertext of <paramref name="plain"/>.</summary>
    internal static byte[] Encrypt(ReadOnlySpan<byte> tag, byte[] key, ReadOnlySpan<byte> plain)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return [.. tag, .. aes.EncryptCbc(plain, s_iv, PaddingMode.PKCS7)];
    }

    /// <summary>The plaintext of <paramref name="cipher"/> (no tag), or null when the padding does not check out.</summary>
    internal static byte[]? Decrypt(byte[] key, ReadOnlySpan<byte> cipher)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        try
        {
            return aes.DecryptCbc(cipher, s_iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary><see cref="Decrypt"/>, then with <see cref="EmptyKey"/> if that fails, as Linux's os_crypt and every
    /// AES-128-CBC key in os_crypt_async do.</summary>
    internal static byte[]? DecryptWithEmptyKeyFallback(byte[] key, ReadOnlySpan<byte> cipher)
        => Decrypt(key, cipher) ?? Decrypt(EmptyKey, cipher);

    /// <summary>A new random password as Chromium stores one in the Keychain or secret service: 16 random bytes in base64.</summary>
    internal static byte[] NewPassword() => System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
}
