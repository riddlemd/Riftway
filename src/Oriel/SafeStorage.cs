using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Oriel.Cef;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>What <see cref="SafeStorage.DecryptStringAsync"/> resolves with; Electron's <c>decryptStringAsync()</c> result.</summary>
/// <param name="ShouldReEncrypt">Whether the data was encrypted under a key that is no longer the one used for
/// encryption, and should be encrypted again.</param>
/// <param name="Result">The decrypted string.</param>
public sealed record SafeStorageDecryptResult(bool ShouldReEncrypt, string Result);

/// <summary>Encrypts and decrypts strings with the OS's per-user secret store; Electron's <c>safeStorage</c>.</summary>
/// <remarks>Windows uses Chromium's os_crypt format, as Electron does: "v10", a 12-byte nonce, then AES-256-GCM under
/// a random key that DPAPI wraps in <c>Local State</c> (<c>os_crypt.encrypted_key</c>), so ciphertext moves between
/// Oriel and Electron apps that share the key. Other platforms report encryption as unavailable.</remarks>
public static class SafeStorage
{
    private const string NotReady = "safeStorage cannot be used before app is ready";

    internal static ISafeStorageBackend Backend { get; set; } = OperatingSystem.IsWindows()
        ? new OsCryptBackend(new CefLocalState())
        : new UnsupportedSafeStorageBackend();

    internal static Func<bool> IsAppReady { get; set; } = () => OrielApp.CurrentOrNull is { IsReady: true };

    /// <summary>Whether encryption is available: on Windows, once the app is ready and the key is loaded.
    /// <c>safeStorage.isEncryptionAvailable()</c>.</summary>
    public static bool IsEncryptionAvailable() => IsAppReady() && Backend.IsEncryptionAvailable;

    /// <summary>Encrypts <paramref name="plainText"/> (as UTF-8); <c>safeStorage.encryptString()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before ready, when encryption is unavailable, or when it fails, with
    /// Electron's messages.</exception>
    public static byte[] EncryptString(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        const string Error = "Error while encrypting the text provided to safeStorage.encryptString.";
        if (!IsEncryptionAvailable())
            throw new InvalidOperationException(IsAppReady() ? Error + " Encryption is not available." : NotReady);
        return Backend.Encrypt(Encoding.UTF8.GetBytes(plainText)) ?? throw new InvalidOperationException(Error);
    }

    /// <summary>Decrypts <see cref="EncryptString"/>'s output; "" for an empty buffer. <c>safeStorage.decryptString()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before ready, when decryption is unavailable, when the data does not
    /// start with os_crypt's "v10" or "v11", or when it cannot be decrypted, with Electron's messages.</exception>
    public static string DecryptString(byte[] encrypted)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        const string Error = "Error while decrypting the ciphertext provided to safeStorage.decryptString.";
        if (!IsEncryptionAvailable())
            throw new InvalidOperationException(IsAppReady() ? Error + " Decryption is not available." : NotReady);
        if (encrypted.Length == 0) return "";
        if (!encrypted.AsSpan().StartsWith("v10"u8) && !encrypted.AsSpan().StartsWith("v11"u8))
            throw new InvalidOperationException(Error + " Ciphertext does not appear to be encrypted.");
        var plain = Backend.Decrypt(encrypted) ?? throw new InvalidOperationException(Error);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Whether the asynchronous encryptor is available: false before ready, else true once it has initialised.
    /// <c>safeStorage.isAsyncEncryptionAvailable()</c>.</summary>
    public static Task<bool> IsAsyncEncryptionAvailableAsync()
    {
        if (!IsAppReady()) return Task.FromResult(false);
        return Task.Run(() =>
        {
            _ = Backend.IsEncryptionAvailable;
            return true;
        });
    }

    /// <summary>Encrypts <paramref name="plainText"/> as os_crypt_async's encryptor does: the same "v10" format, and an
    /// empty result for "". <c>safeStorage.encryptStringAsync()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before ready, or when encryption fails, with Electron's messages.</exception>
    public static Task<byte[]> EncryptStringAsync(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        if (!IsAppReady()) return Task.FromException<byte[]>(new InvalidOperationException(NotReady));
        return Task.Run(() =>
        {
            if (plainText.Length == 0) return [];
            return Backend.Encrypt(Encoding.UTF8.GetBytes(plainText))
                ?? throw new InvalidOperationException("Error while encrypting the text provided to safeStorage.encryptStringAsync.");
        });
    }

    /// <summary>Decrypts "v10" data as os_crypt_async's encryptor does; an empty buffer gives "". Unlike
    /// <see cref="DecryptString"/> there is no DPAPI fallback. <c>safeStorage.decryptStringAsync()</c>.</summary>
    /// <exception cref="InvalidOperationException">Before ready, or when the data cannot be decrypted, with Electron's
    /// messages.</exception>
    public static Task<SafeStorageDecryptResult> DecryptStringAsync(byte[] encrypted)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        if (!IsAppReady()) return Task.FromException<SafeStorageDecryptResult>(new InvalidOperationException(NotReady));
        if (encrypted.Length == 0) return Task.FromResult(new SafeStorageDecryptResult(false, ""));
        return Task.Run(() =>
        {
            var plain = Backend.Decrypt(encrypted);
            // One key provider on Windows, so nothing decrypted here was under a key other than the encrypting one.
            return plain is null
                ? throw new InvalidOperationException("Error while decrypting the ciphertext provided to safeStorage.decryptStringAsync.")
                : new SafeStorageDecryptResult(false, Encoding.UTF8.GetString(plain));
        });
    }

    /// <summary>Linux only: allows an in-memory password when no secret store is found;
    /// <c>safeStorage.setUsePlainTextEncryption()</c>. No-op on Windows and macOS, as in Electron.</summary>
    public static void SetUsePlainTextEncryption(bool usePlainText) { }

    /// <summary>The secret store in use; <c>safeStorage.getSelectedStorageBackend()</c>, which Electron has on Linux only.
    /// "unknown" until Linux support exists.</summary>
    [SupportedOSPlatform("linux")]
    public static string GetSelectedStorageBackend() => "unknown";
}

/// <summary>A platform's secret store behind <see cref="SafeStorage"/>.</summary>
internal interface ISafeStorageBackend
{
    /// <summary>Whether <see cref="Encrypt"/> and <see cref="Decrypt"/> work: on Windows, whether the key loaded.</summary>
    bool IsEncryptionAvailable { get; }

    /// <summary>Encrypts bytes for the current user; null if that fails or encryption is unavailable.</summary>
    byte[]? Encrypt(byte[] plain);

    /// <summary>Decrypts "v10" bytes from <see cref="Encrypt"/>; null when they cannot be.</summary>
    byte[]? Decrypt(byte[] encrypted);
}

/// <summary>The two <c>Local State</c> preferences os_crypt keeps its key in.</summary>
internal interface ILocalState
{
    /// <summary>The string preference, or null when it is unset or unknown.</summary>
    string? GetString(string name);

    bool GetBoolean(string name);

    /// <summary>Sets a preference; false if it was refused.</summary>
    bool Set(string name, string value);

    bool Set(string name, bool value);
}

/// <summary>Chromium's sync os_crypt on Windows (<c>os_crypt_win.cc</c>, which Electron restores): "v10" + 12-byte nonce +
/// AES-256-GCM (no associated data) with the 16-byte tag last.</summary>
[SupportedOSPlatform("windows")]
internal sealed class OsCryptBackend(ILocalState localState) : ISafeStorageBackend
{
    internal const string EncryptedKeyPref = "os_crypt.encrypted_key";
    internal const string AuditEnabledPref = "os_crypt.audit_enabled";
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private readonly Lock _lock = new();
    private bool _initialized;
    private byte[]? _key;

    public bool IsEncryptionAvailable => Key is not null;

    private byte[]? Key
    {
        get
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    _key = Init();
                    _initialized = true;
                }
                return _key;
            }
        }
    }

    public byte[]? Encrypt(byte[] plain)
    {
        if (Key is not { } key) return null;
        var result = new byte[3 + NonceLength + plain.Length + TagLength];
        "v10"u8.CopyTo(result);
        var nonce = result.AsSpan(3, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plain, result.AsSpan(3 + NonceLength, plain.Length), result.AsSpan(3 + NonceLength + plain.Length));
        return result;
    }

    // os_crypt would hand data without "v10" to raw DPAPI, but Electron refuses such data before it gets here.
    public byte[]? Decrypt(byte[] encrypted)
    {
        if (!encrypted.AsSpan().StartsWith("v10"u8) || Key is not { } key || encrypted.Length < 3 + NonceLength + TagLength) return null;
        var body = encrypted.AsSpan(3 + NonceLength, encrypted.Length - 3 - NonceLength - TagLength);
        var plain = new byte[body.Length];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(encrypted.AsSpan(3, NonceLength), body, encrypted.AsSpan(encrypted.Length - TagLength), plain);
            return plain;
        }
        catch (AuthenticationTagMismatchException) { return null; }
    }

    /// <summary><c>OSCryptImpl::Init()</c>: the existing key if it unwraps, else a new random one stored for next time;
    /// none (encryption unavailable) if the stored key is not DPAPI-wrapped or the new one cannot be stored.</summary>
    private byte[]? Init()
    {
        if (localState.GetString(EncryptedKeyPref) is { Length: > 0 } stored)
        {
            byte[] decoded;
            try { decoded = Convert.FromBase64String(stored); }
            catch (FormatException) { decoded = []; }
            if (!decoded.AsSpan().StartsWith("DPAPI"u8)) return null;
            if (WindowsDpapi.Unprotect(decoded.AsSpan(5)) is { } existing)
            {
                if (!localState.GetBoolean(AuditEnabledPref))
                {
                    Store(existing);
                    localState.Set(AuditEnabledPref, true);
                }
                return existing;
            }
        }
        var key = RandomNumberGenerator.GetBytes(KeyLength);
        if (!Store(key)) return null;
        localState.Set(AuditEnabledPref, true);
        return key;
    }

    private bool Store(byte[] key)
    {
        if (WindowsDpapi.ProtectAudited(key, WindowsAppIdentity.ProductName) is not { } wrapped) return false;
        return localState.Set(EncryptedKeyPref, Convert.ToBase64String([.. "DPAPI"u8, .. wrapped]));
    }
}

/// <summary>CEF's own <c>Local State</c>, through its global preference manager on the UI thread, so Oriel never writes
/// the file CEF owns. If CEF does not know a preference, the file is read instead (never written).</summary>
internal sealed unsafe class CefLocalState : ILocalState
{
    public string? GetString(string name)
    {
        var fromCef = CefThread.InvokeOnUi(() => Read(name, out var value) ? value : null);
        return fromCef switch
        {
            string s => s.Length > 0 ? s : null,
            _ => FromFile(name)?.GetValue<string>(),
        };
    }

    public bool GetBoolean(string name)
        => CefThread.InvokeOnUi(() => Read(name, out var value) ? value : null) is bool b ? b : FromFile(name)?.GetValue<bool>() ?? false;

    public bool Set(string name, string value) => CefThread.InvokeOnUi(() =>
    {
        var v = CefNative.cef_value_create();
        cef_string_utf16_t text = default;
        CefString.Set(&text, value);
        v->set_string(v, &text);
        CefString.Clear(&text);
        return Write(name, v);
    });

    public bool Set(string name, bool value) => CefThread.InvokeOnUi(() =>
    {
        var v = CefNative.cef_value_create();
        v->set_bool(v, value ? 1 : 0);
        return Write(name, v);
    });

    private static bool Read(string name, out object? value)
    {
        value = null;
        var manager = CefNative.cef_preference_manager_get_global();
        if (manager is null) return false;
        cef_string_utf16_t key = default;
        CefString.Set(&key, name);
        var v = manager->get_preference(manager, &key);
        CefString.Clear(&key);
        NativeObject.Release(manager);
        if (v is null) return false;
        var type = v->get_type(v);
        if (type == cef_value_type_t.VTYPE_STRING) value = CefString.ReadUserFree(v->get_string(v));
        else if (type == cef_value_type_t.VTYPE_BOOL) value = v->get_bool(v) != 0;
        NativeObject.Release(v);
        return value is not null;
    }

    // Takes the value's reference.
    private static bool Write(string name, cef_value_t* value)
    {
        var manager = CefNative.cef_preference_manager_get_global();
        if (manager is null)
        {
            NativeObject.Release(value);
            return false;
        }
        cef_string_utf16_t key = default, error = default;
        CefString.Set(&key, name);
        var ok = manager->set_preference(manager, &key, value, &error) != 0;
        CefString.Clear(&key);
        CefString.Clear(&error);
        NativeObject.Release(manager);
        return ok;
    }

    private static JsonNode? FromFile(string name)
    {
        if (OrielApp.DataDirectory is not { } directory) return null;
        try
        {
            JsonNode? node = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "Local State")));
            foreach (var part in name.Split('.')) node = node?[part];
            return node;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Backend for platforms without an implementation yet.</summary>
internal sealed class UnsupportedSafeStorageBackend : ISafeStorageBackend
{
    public bool IsEncryptionAvailable => false;

    public byte[]? Encrypt(byte[] plain) => null;

    public byte[]? Decrypt(byte[] encrypted) => null;
}
