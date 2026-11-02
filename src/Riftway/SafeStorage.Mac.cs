using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>The generic-password calls os_crypt makes on the Keychain (Chromium's <c>KeychainV2</c>).</summary>
internal interface IKeychain
{
    /// <summary>The item's password, or the <c>OSStatus</c> that stopped the lookup.</summary>
    (byte[]? Password, int Status) FindGenericPassword(string service, string account);

    /// <summary>Adds the item (or updates it if it has appeared meanwhile); the <c>OSStatus</c>.</summary>
    int AddGenericPassword(string service, string account, byte[] password);
}

/// <summary>Chromium's <c>FakeKeychainV2</c>, which os_crypt uses under <c>--use-mock-keychain</c>: every lookup finds
/// "mock_password".</summary>
internal sealed class MockKeychain : IKeychain
{
    public (byte[]? Password, int Status) FindGenericPassword(string service, string account) => ("mock_password"u8.ToArray(), 0);

    public int AddGenericPassword(string service, string account, byte[] password) => 0;
}

/// <summary>os_crypt on macOS, as Electron sets it up: the Keychain item with service "<c>{app name} Safe Storage</c>" and
/// account "<c>{app name}</c>" holds a password (created as 16 random bytes in base64 when missing), and PBKDF2-SHA1
/// (1003 iterations) turns it into the AES-128-CBC key. Data is "v10" + ciphertext.</summary>
/// <remarks>The synchronous API is <c>os_crypt_mac.mm</c>: it asks the Keychain once per process and stays unavailable if
/// that fails. The asynchronous one is <c>KeychainKeyProvider</c>: its own lookup, a failure that makes "v10" data
/// temporarily unavailable, and the empty-key fallback on decryption.</remarks>
internal sealed class KeychainOsCryptBackend(Func<IKeychain> keychain, Func<string> appName) : ISafeStorageBackend
{
    internal const int ErrSecItemNotFound = -25300;
    private const int Iterations = 1003;

    private readonly Lock _lock = new();
    private bool _triedKeychain;
    private byte[]? _key;
    private bool _asyncLoaded;
    private byte[]? _asyncKey;

    internal static KeychainOsCryptBackend CreateDefault() => new(
        () => RiftwayApp.CurrentOrNull?.CommandLine.HasSwitch("use-mock-keychain") == true ? new MockKeychain() : new SystemKeychain(),
        () => RiftwayApp.CurrentOrNull?.Name ?? "");

    public bool IsEncryptionAvailable => Key is not null;

    private byte[]? Key
    {
        get
        {
            lock (_lock)
            {
                if (_key is null && !_triedKeychain)
                {
                    _triedKeychain = true;
                    if (GetPassword() is { Length: > 0 } password) _key = OsCryptCbc.DeriveKey(password, Iterations);
                }
                return _key;
            }
        }
    }

    /// <summary><c>KeychainPassword::GetPassword()</c>: the stored password, else a new one added; null on any failure.</summary>
    internal byte[]? GetPassword()
    {
        var name = appName();
        var store = keychain();
        var service = name + " Safe Storage";
        var (password, status) = store.FindGenericPassword(service, name);
        if (status == 0) return password;
        if (status != ErrSecItemNotFound) return null;
        var created = OsCryptCbc.NewPassword();
        return store.AddGenericPassword(service, name, created) == 0 ? created : null;
    }

    public byte[]? Encrypt(byte[] plain)
    {
        if (plain.Length == 0) return [];
        return Key is { } key ? OsCryptCbc.Encrypt("v10"u8, key, plain) : null;
    }

    // macOS's sync os_crypt knows only "v10" and, unlike Linux's, has no empty-key fallback.
    public byte[]? Decrypt(byte[] encrypted)
    {
        if (encrypted.Length == 0) return [];
        if (!encrypted.AsSpan().StartsWith("v10"u8) || Key is not { } key) return null;
        return OsCryptCbc.Decrypt(key, encrypted.AsSpan(3));
    }

    public void LoadAsyncKeys()
    {
        lock (_lock)
        {
            if (_asyncLoaded) return;
            _asyncLoaded = true;
            if (GetPassword() is { Length: > 0 } password) _asyncKey = OsCryptCbc.DeriveKey(password, Iterations);
        }
    }

    public byte[]? AsyncEncrypt(byte[] plain)
    {
        LoadAsyncKeys();
        return _asyncKey is { } key ? OsCryptCbc.Encrypt("v10"u8, key, plain) : null;
    }

    public AsyncDecryptOutcome AsyncDecrypt(byte[] encrypted)
    {
        LoadAsyncKeys();
        if (!encrypted.AsSpan().StartsWith("v10"u8)) return new(null);
        // The provider failed with kTemporarilyUnavailable, so its tag is in the key ring without a key.
        if (_asyncKey is not { } key) return new(null, TemporarilyUnavailable: true);
        return new(OsCryptCbc.DecryptWithEmptyKeyFallback(key, encrypted.AsSpan(3)));
    }
}

/// <summary>The login Keychain, through <see cref="MacKeychain"/>.</summary>
internal sealed class SystemKeychain : IKeychain
{
    public (byte[]? Password, int Status) FindGenericPassword(string service, string account)
        => OperatingSystem.IsMacOS() ? MacKeychain.FindGenericPassword(service, account) : (null, -4);

    public int AddGenericPassword(string service, string account, byte[] password)
        => OperatingSystem.IsMacOS() ? MacKeychain.AddGenericPassword(service, account, password) : -4;
}
