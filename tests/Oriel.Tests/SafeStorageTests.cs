using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

/// <summary>A fact that only runs on Windows (DPAPI) and shows as skipped elsewhere, rather than passing vacuously.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows DPAPI";
    }
}

/// <summary>A theory that only runs on Windows (DPAPI) and shows as skipped elsewhere.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows DPAPI";
    }
}

/// <summary>An in-memory <c>Local State</c> for os_crypt's two preferences.</summary>
internal sealed class FakeLocalState : ILocalState
{
    public readonly Dictionary<string, object> Values = [];
    public bool RefuseWrites;

    public string? GetString(string name) => Values.TryGetValue(name, out var v) ? (string)v : null;

    public bool GetBoolean(string name) => Values.TryGetValue(name, out var v) && (bool)v;

    public bool Set(string name, string value) => !RefuseWrites && (Values[name] = value) is not null;

    public bool Set(string name, bool value)
    {
        if (RefuseWrites) return false;
        Values[name] = value;
        return true;
    }
}

[SupportedOSPlatform("windows")]
[Collection("SafeStorage")]
public sealed class SafeStorageTests : IDisposable
{
    private const string EncryptError = "Error while encrypting the text provided to safeStorage.encryptString.";
    private const string DecryptError = "Error while decrypting the ciphertext provided to safeStorage.decryptString.";
    private const string NotReady = "safeStorage cannot be used before app is ready";

    private readonly ISafeStorageBackend _savedBackend = SafeStorage.Backend;
    private readonly Func<bool> _savedReady = SafeStorage.IsAppReady;
    private readonly FakeLocalState _state = new();
    private bool _ready = true;

    public SafeStorageTests()
    {
        SafeStorage.IsAppReady = () => _ready;
        if (OperatingSystem.IsWindows()) SafeStorage.Backend = new OsCryptBackend(_state);
    }

    public void Dispose()
    {
        SafeStorage.Backend = _savedBackend;
        SafeStorage.IsAppReady = _savedReady;
    }

    private static string Wrap(byte[] key) => Convert.ToBase64String([.. "DPAPI"u8, .. WindowsDpapi.ProtectAudited(key, "test")!]);

    private byte[] StoredKey()
    {
        var decoded = Convert.FromBase64String((string)_state.Values[OsCryptBackend.EncryptedKeyPref]);
        Assert.Equal("DPAPI"u8.ToArray(), decoded[..5]);
        return WindowsDpapi.Unprotect(decoded.AsSpan(5))!;
    }

    [System.Runtime.InteropServices.DllImport("crypt32.dll")]
    private static extern unsafe int CryptUnprotectData(DpapiBlob* input, char** description, nint entropy, nint reserved, nint prompt, uint flags, DpapiBlob* output);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern unsafe nint LocalFree(void* memory);

    private unsafe struct DpapiBlob
    {
        public uint Size;
        public byte* Data;
    }

    // DPAPI's ppszDataDescr out-parameter, read here rather than through Oriel's wrapper.
    private unsafe string StoredKeyDescription()
    {
        var decoded = Convert.FromBase64String((string)_state.Values[OsCryptBackend.EncryptedKeyPref]);
        fixed (byte* data = decoded)
        {
            var input = new DpapiBlob { Size = (uint)(decoded.Length - 5), Data = data + 5 };
            DpapiBlob output = default;
            char* description = null;
            Assert.NotEqual(0, CryptUnprotectData(&input, &description, 0, 0, 0, 0, &output));
            LocalFree(output.Data);
            var text = new string(description);
            LocalFree(description);
            return text;
        }
    }

    // Chromium's layout, built here with .NET's AES-GCM rather than Oriel's code.
    private static byte[] OsCryptEncrypt(byte[] key, string text)
    {
        var plain = Encoding.UTF8.GetBytes(text);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        return [.. "v10"u8, .. nonce, .. cipher, .. tag];
    }

    private static string OsCryptDecrypt(byte[] key, byte[] data)
    {
        var plain = new byte[data.Length - 31];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(data.AsSpan(3, 12), data.AsSpan(15, plain.Length), data.AsSpan(data.Length - 16), plain);
        return Encoding.UTF8.GetString(plain);
    }

    [WindowsTheory]
    [InlineData("hello")]
    [InlineData("héllo wörld ✓ 日本語 😀")]
    [InlineData("")]
    public void EncryptString_IsOsCryptsV10UnderTheStoredKey(string text)
    {
        var encrypted = SafeStorage.EncryptString(text);
        Assert.Equal("v10"u8.ToArray(), encrypted[..3]);
        Assert.Equal(3 + 12 + Encoding.UTF8.GetByteCount(text) + 16, encrypted.Length);
        Assert.Equal(text, OsCryptDecrypt(StoredKey(), encrypted));
        Assert.Equal(text, SafeStorage.DecryptString(encrypted));
    }

    [WindowsFact]
    public void EncryptString_UsesAFreshNonce()
        => Assert.NotEqual(SafeStorage.EncryptString("same")[3..15], SafeStorage.EncryptString("same")[3..15]);

    [WindowsFact]
    public void ExistingKey_IsReadNotReplaced()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var stored = Wrap(key);
        _state.Values[OsCryptBackend.EncryptedKeyPref] = stored;
        _state.Values[OsCryptBackend.AuditEnabledPref] = true;
        Assert.Equal("from chromium ✓", SafeStorage.DecryptString(OsCryptEncrypt(key, "from chromium ✓")));
        Assert.Equal(stored, _state.Values[OsCryptBackend.EncryptedKeyPref]);
    }

    [WindowsFact]
    public void MissingKey_IsGeneratedAndStored()
    {
        Assert.True(SafeStorage.IsEncryptionAvailable());
        Assert.Equal(32, StoredKey().Length);
        Assert.Equal(true, _state.Values[OsCryptBackend.AuditEnabledPref]);
        Assert.Equal("Chromium", StoredKeyDescription());
    }

    [WindowsFact]
    public void UndecryptableKey_IsReplaced()
    {
        var bad = Convert.ToBase64String([.. "DPAPI"u8, 1, 2, 3]);
        _state.Values[OsCryptBackend.EncryptedKeyPref] = bad;
        Assert.True(SafeStorage.IsEncryptionAvailable());
        Assert.NotEqual(bad, _state.Values[OsCryptBackend.EncryptedKeyPref]);
        Assert.Equal("x", OsCryptDecrypt(StoredKey(), SafeStorage.EncryptString("x")));
    }

    [WindowsFact]
    public void UnauditedKey_IsRewrappedAndKept()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var stored = Wrap(key);
        _state.Values[OsCryptBackend.EncryptedKeyPref] = stored;
        Assert.True(SafeStorage.IsEncryptionAvailable());
        Assert.NotEqual(stored, _state.Values[OsCryptBackend.EncryptedKeyPref]);
        Assert.Equal(key, StoredKey());
        Assert.Equal(true, _state.Values[OsCryptBackend.AuditEnabledPref]);
        Assert.Equal("Chromium", StoredKeyDescription());
    }

    [WindowsTheory]
    [InlineData("bm90IGRwYXBp")] // "not dpapi"
    [InlineData("!!")]
    public void KeyWithoutTheDpapiHeader_MakesEncryptionUnavailable(string stored)
    {
        _state.Values[OsCryptBackend.EncryptedKeyPref] = stored;
        Assert.False(SafeStorage.IsEncryptionAvailable());
        Assert.Equal(EncryptError + " Encryption is not available.", Assert.Throws<InvalidOperationException>(() => SafeStorage.EncryptString("x")).Message);
        Assert.Equal(DecryptError + " Decryption is not available.", Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString([1])).Message);
        Assert.Equal(stored, _state.Values[OsCryptBackend.EncryptedKeyPref]);
    }

    [WindowsFact]
    public void KeyThatCannotBeStored_MakesEncryptionUnavailable()
    {
        _state.RefuseWrites = true;
        Assert.False(SafeStorage.IsEncryptionAvailable());
    }

    [WindowsFact]
    public async Task BeforeReady_NothingWorks()
    {
        _ready = false;
        Assert.False(SafeStorage.IsEncryptionAvailable());
        Assert.Equal(NotReady, Assert.Throws<InvalidOperationException>(() => SafeStorage.EncryptString("x")).Message);
        Assert.Equal(NotReady, Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString([1])).Message);
        Assert.False(await SafeStorage.IsAsyncEncryptionAvailableAsync());
        Assert.Equal(NotReady, (await Assert.ThrowsAsync<InvalidOperationException>(() => SafeStorage.EncryptStringAsync("x"))).Message);
        Assert.Equal(NotReady, (await Assert.ThrowsAsync<InvalidOperationException>(() => SafeStorage.DecryptStringAsync([1]))).Message);
        Assert.Empty(_state.Values);
    }

    [WindowsFact]
    public void DecryptString_EmptyIsEmpty() => Assert.Equal("", SafeStorage.DecryptString([]));

    [WindowsTheory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { (byte)'v', (byte)'1', (byte)'2', 0 })]
    public void DecryptString_RefusesDataWithoutAVersion(byte[] data)
        => Assert.Equal(DecryptError + " Ciphertext does not appear to be encrypted.",
            Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString(data)).Message);

    [WindowsFact]
    public void DecryptString_RefusesRawDpapi()
        => Assert.Equal(DecryptError + " Ciphertext does not appear to be encrypted.",
            Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString(WindowsDpapi.Protect("x"u8, default))).Message);

    [WindowsTheory]
    [InlineData("v11")]
    [InlineData("tamper")]
    [InlineData("short")]
    public void DecryptString_ThrowsWhenItCannotDecrypt(string how)
    {
        var data = how switch
        {
            "v11" => [.. "v11"u8, .. SafeStorage.EncryptString("secret")[3..]],
            "tamper" => SafeStorage.EncryptString("secret"),
            _ => "v10short"u8.ToArray(),
        };
        if (how == "tamper") data[^1] ^= 0xFF;
        Assert.Equal(DecryptError, Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString(data)).Message);
    }

    [WindowsFact]
    public async Task AsyncApi_SharesTheFormatAndKey()
    {
        Assert.True(await SafeStorage.IsAsyncEncryptionAvailableAsync());
        var encrypted = await SafeStorage.EncryptStringAsync("async ✓");
        Assert.Equal("async ✓", OsCryptDecrypt(StoredKey(), encrypted));
        Assert.Equal(new SafeStorageDecryptResult(false, "sync"), await SafeStorage.DecryptStringAsync(SafeStorage.EncryptString("sync")));
        Assert.Empty(await SafeStorage.EncryptStringAsync(""));
        Assert.Equal(new SafeStorageDecryptResult(false, ""), await SafeStorage.DecryptStringAsync([]));
    }

    [WindowsFact]
    public async Task AsyncApi_RejectsWithElectronsMessages()
    {
        Assert.Equal("Error while decrypting the ciphertext provided to safeStorage.decryptStringAsync.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => SafeStorage.DecryptStringAsync([1, 2, 3]))).Message);
        _state.Values[OsCryptBackend.EncryptedKeyPref] = "bm9wZQ==";
        SafeStorage.Backend = new OsCryptBackend(_state);
        Assert.True(await SafeStorage.IsAsyncEncryptionAvailableAsync());
        Assert.Equal("Error while encrypting the text provided to safeStorage.encryptStringAsync.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => SafeStorage.EncryptStringAsync("x"))).Message);
    }

    [WindowsFact]
    public void Unprotect_ReturnsNullForOtherEntropy()
    {
        var blob = WindowsDpapi.Protect("x"u8, "a"u8);
        Assert.Null(WindowsDpapi.Unprotect(blob, "b"u8));
        Assert.Equal("x"u8.ToArray(), WindowsDpapi.Unprotect(blob, "a"u8)!);
    }

    [WindowsFact]
    public void ProtectAudited_OpensWithoutEntropy()
    {
        var blob = WindowsDpapi.ProtectAudited("key"u8, "Product")!;
        Assert.Equal("key"u8.ToArray(), WindowsDpapi.Unprotect(blob)!);
        Assert.Null(WindowsDpapi.Unprotect(blob, "a"u8));
    }

    [WindowsFact]
    public void Unprotect_ReturnsEmptyForEmptyData()
    {
        var plain = WindowsDpapi.Unprotect(WindowsDpapi.Protect(default, "a"u8), "a"u8);
        Assert.NotNull(plain);
        Assert.Empty(plain);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => SafeStorage.EncryptString(null!));
        Assert.Throws<ArgumentNullException>(() => SafeStorage.DecryptString(null!));
        Assert.Throws<ArgumentNullException>(() => { _ = SafeStorage.EncryptStringAsync(null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = SafeStorage.DecryptStringAsync(null!); });
    }

    [Fact]
    public void SetUsePlainTextEncryption_IsInertOffLinux() => SafeStorage.SetUsePlainTextEncryption(true);

    [Fact]
    public async Task UnsupportedBackend_IsUnavailableWithElectronsErrors()
    {
        SafeStorage.Backend = new UnsupportedSafeStorageBackend();
        Assert.False(SafeStorage.IsEncryptionAvailable());
        Assert.Equal(EncryptError + " Encryption is not available.", Assert.Throws<InvalidOperationException>(() => SafeStorage.EncryptString("x")).Message);
        Assert.Equal("Error while encrypting the text provided to safeStorage.encryptStringAsync.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => SafeStorage.EncryptStringAsync("x"))).Message);
    }
}
