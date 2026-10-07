using System.Text;

namespace Oriel.Tests;

/// <summary>A Keychain that answers from memory and counts what it was asked.</summary>
internal sealed class FakeKeychain : IKeychain
{
    public byte[]? Password;
    public int FindStatus;
    public int AddStatus;
    public readonly List<string> Calls = [];

    public (byte[]? Password, int Status) FindGenericPassword(string service, string account)
    {
        Calls.Add($"find {service}|{account}");
        if (FindStatus != 0) return (null, FindStatus);
        return Password is null ? (null, KeychainOsCryptBackend.ErrSecItemNotFound) : (Password, 0);
    }

    public int AddGenericPassword(string service, string account, byte[] password)
    {
        Calls.Add($"add {service}|{account}|{Encoding.ASCII.GetString(password)}");
        if (AddStatus == 0) Password = password;
        return AddStatus;
    }
}

/// <summary>A secret store that answers from memory and counts what it was asked.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    public byte[]? LibsecretPassword;
    public byte[]? ServiceSecret;
    public readonly List<string> Calls = [];

    public byte[]? GetLibsecretPassword(string appName)
    {
        Calls.Add("libsecret " + appName);
        return LibsecretPassword;
    }

    public byte[]? GetSecretServiceSecret(string appName, string keyName)
    {
        Calls.Add($"service {appName}|{keyName}");
        return ServiceSecret;
    }
}

/// <summary>macOS and Linux os_crypt behind <see cref="SafeStorage"/>. Expected ciphertexts come from OpenSSL
/// (<c>openssl kdf ... PBKDF2</c> then <c>openssl enc -aes-128-cbc -iv 2020...20</c>) or Chromium's own unittests, not
/// from Oriel's code.</summary>
[Collection("SafeStorage")]
public sealed class SafeStorageOsCryptTests : IDisposable
{
    private const string EncryptError = "Error while encrypting the text provided to safeStorage.encryptString.";
    private const string DecryptError = "Error while decrypting the ciphertext provided to safeStorage.decryptString.";
    private const string AsyncEncryptError = "Error while encrypting the text provided to safeStorage.encryptStringAsync.";
    private const string AsyncDecryptError = "Error while decrypting the ciphertext provided to safeStorage.decryptStringAsync.";
    private const string Text = "Oriel ✓ safeStorage";

    // openssl: PBKDF2-SHA1("peanuts", "saltysalt", 1) then AES-128-CBC of Text.
    private static readonly byte[] s_v10Text = [.. "v10"u8, .. Convert.FromHexString("46189fb2ec86b3f6b3e4f5c082f9431f804270f56dc9988d0d6b7c807fdce6ee")];
    // openssl: PBKDF2-SHA1("mock_password", "saltysalt", 1003) then AES-128-CBC of Text.
    private static readonly byte[] s_mockText = [.. "v10"u8, .. Convert.FromHexString("d42c5198e914b22f1088223243a09fd0acf193767d983d47eceffd62efdf0bca")];
    // openssl: PBKDF2-SHA1("Tm90QVJlYWxQYXNzd29yZA==", "saltysalt", 1003) then AES-128-CBC of "hello".
    private static readonly byte[] s_macHello = [.. "v10"u8, .. Convert.FromHexString("c3f56ea7073633cc79a10a5c2d4ccea4")];
    // openssl: PBKDF2-SHA1("", "saltysalt", 1) then AES-128-CBC of "legacy": the empty-key fallback.
    private static readonly byte[] s_emptyKeyLegacy = Convert.FromHexString("f426f2bfe8fa9fbf6a97c61aca707354");
    // Chromium's os_crypt_linux_unittest.cc EncryptDecryptKnownAnswers: password "zxqfb".
    private static readonly byte[] s_chromiumV11 = [.. "v11"u8, .. Convert.FromHexString(
        "772100608709e894de634f803640b7dd80e12c6f9ab66bba93afecb589e61228f085a2bbf9303087f8d1104e97e17573")];

    private readonly ISafeStorageBackend _savedBackend = SafeStorage.Backend;
    private readonly Func<bool> _savedReady = SafeStorage.IsAppReady;
    private readonly FakeKeychain _keychain = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly Dictionary<string, string> _env = [];
    private string _store = "";
    private bool _ready = true;

    public SafeStorageOsCryptTests()
    {
        SafeStorage.IsAppReady = () => _ready;
        SafeStorage.SetUsePlainTextEncryption(false);
    }

    public void Dispose()
    {
        SafeStorage.Backend = _savedBackend;
        SafeStorage.IsAppReady = _savedReady;
        SafeStorage.SetUsePlainTextEncryption(false);
    }

#pragma warning disable CA1416 // The platform-neutral backends run on any OS here.
    private static string Selected() => SafeStorage.GetSelectedStorageBackend();
#pragma warning restore CA1416

    private void UseMac(IKeychain? keychain = null) => SafeStorage.Backend = new KeychainOsCryptBackend(() => keychain ?? _keychain, () => "My App");

    private void UseLinux(string? userData = null, bool selection = false) => SafeStorage.Backend = new LinuxOsCryptBackend(
        () => new LinuxOsCryptConfig(_store, selection, userData, "my-app", name => _env.GetValueOrDefault(name)), _secrets);

    [Theory]
    [InlineData("peanuts", 1, "fd621fe5a2b402539dfa147ca9272778")]
    [InlineData("", 1, "d0d0ec9c7d77d43ac54187fa4818d17f")]
    [InlineData("zxqfb", 1, "b756307474b00da355f773f02f861ae4")]
    [InlineData("mock_password", 1003, "af0f762aaf6d7d11581b7aa8ce7218de")]
    [InlineData("Tm90QVJlYWxQYXNzd29yZA==", 1003, "fb3ad83f4935a680905fce0144764ac3")]
    public void DeriveKey_MatchesKnownAnswers(string password, int iterations, string key)
        => Assert.Equal(key, Convert.ToHexStringLower(OsCryptCbc.DeriveKey(Encoding.UTF8.GetBytes(password), iterations)));

    [Fact]
    public void FixedKeys_AreChromiums()
    {
        Assert.Equal(OsCryptCbc.DeriveKey("peanuts"u8, 1), OsCryptCbc.V10Key);
        Assert.Equal(OsCryptCbc.DeriveKey(""u8, 1), OsCryptCbc.EmptyKey);
    }

    // ---- Linux ----

    [Fact]
    public void Linux_BasicText_IsUnavailableUntilPlainTextIsAllowed()
    {
        UseLinux();
        Assert.Equal("basic_text", Selected());
        Assert.False(SafeStorage.IsEncryptionAvailable());
        Assert.Equal(EncryptError + " Encryption is not available.", Assert.Throws<InvalidOperationException>(() => SafeStorage.EncryptString("x")).Message);
        Assert.Equal(DecryptError + " Decryption is not available.", Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString(s_v10Text)).Message);
        Assert.Empty(_secrets.Calls);

        SafeStorage.SetUsePlainTextEncryption(true);
        Assert.True(SafeStorage.IsEncryptionAvailable());
        Assert.Equal(s_v10Text, SafeStorage.EncryptString(Text));
        Assert.Equal(Text, SafeStorage.DecryptString(s_v10Text));
    }

    [Fact]
    public void Linux_PlainText_DoesNotCoverAFailedSecretStore()
    {
        _env["XDG_CURRENT_DESKTOP"] = "GNOME";
        UseLinux();
        SafeStorage.SetUsePlainTextEncryption(true);
        Assert.Equal("gnome_libsecret", Selected());
        Assert.False(SafeStorage.IsEncryptionAvailable());
        Assert.Equal(["libsecret my-app"], _secrets.Calls);
    }

    [Fact]
    public void Linux_Libsecret_EncryptsV11AsChromiumDoes()
    {
        _store = "gnome-libsecret";
        _secrets.LibsecretPassword = "zxqfb"u8.ToArray();
        UseLinux();
        Assert.True(SafeStorage.IsEncryptionAvailable());
        Assert.Equal(s_chromiumV11, SafeStorage.EncryptString("Hello, World! This is a longer string."));
        Assert.Equal("Hello, World! This is a longer string.", SafeStorage.DecryptString(s_chromiumV11));
        Assert.Equal(Text, SafeStorage.DecryptString(s_v10Text));
        Assert.Equal(["libsecret my-app"], _secrets.Calls);
    }

    [Fact]
    public void Linux_EmptyKeyFallback_Decrypts()
    {
        _store = "gnome-libsecret";
        _secrets.LibsecretPassword = "zxqfb"u8.ToArray();
        UseLinux();
        Assert.Equal("legacy", SafeStorage.DecryptString([.. "v10"u8, .. s_emptyKeyLegacy]));
        Assert.Equal("legacy", SafeStorage.DecryptString([.. "v11"u8, .. s_emptyKeyLegacy]));
    }

    [Fact]
    public void Linux_ErrorsMatchElectron()
    {
        UseLinux();
        SafeStorage.SetUsePlainTextEncryption(true);
        Assert.Equal(DecryptError + " Ciphertext does not appear to be encrypted.",
            Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString("V10abc"u8.ToArray())).Message);
        Assert.Equal(DecryptError, Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString(s_chromiumV11)).Message);
        var tampered = s_v10Text.ToArray();
        tampered[^1] ^= 0x01;
        Assert.Equal(DecryptError, Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString(tampered)).Message);
        Assert.Equal(DecryptError, Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString("v10"u8.ToArray())).Message);
        Assert.Equal(DecryptError, Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString(s_v10Text[..^1])).Message);
        Assert.Empty(SafeStorage.EncryptString(""));
        Assert.Equal("", SafeStorage.DecryptString([]));
    }

    [Fact]
    public void Linux_SecretStoreIsAskedOnce()
    {
        _store = "gnome-libsecret";
        UseLinux();
        Assert.False(SafeStorage.IsEncryptionAvailable());
        Assert.False(SafeStorage.IsEncryptionAvailable());
        Assert.Single(_secrets.Calls);
    }

    [Fact]
    public async Task Linux_BeforeReady()
    {
        _ready = false;
        UseLinux();
        Assert.Equal("unknown", Selected());
        Assert.False(await SafeStorage.IsAsyncEncryptionAvailableAsync());
    }

    [Theory]
    [InlineData("", "", "", "basic_text")]
    [InlineData("GNOME", "", "", "gnome_libsecret")]
    [InlineData("ubuntu:GNOME", "", "", "gnome_libsecret")]
    [InlineData("Unity", "gnome-fallback", "", "gnome_libsecret")]
    [InlineData("Unity", "", "", "gnome_libsecret")]
    [InlineData("X-Cinnamon", "", "", "gnome_libsecret")]
    [InlineData("XFCE", "", "", "gnome_libsecret")]
    [InlineData("KDE", "", "", "kwallet")]
    [InlineData("KDE", "", "5", "kwallet5")]
    [InlineData("KDE", "", "6", "kwallet6")]
    [InlineData("LXQt", "", "", "basic_text")]
    [InlineData("COSMIC", "", "", "gnome_libsecret")]
    [InlineData("Hyprland", "", "", "basic_text")]
    [InlineData("", "mate", "", "gnome_libsecret")]
    [InlineData("", "kde", "", "basic_text")]
    [InlineData("", "kde", "4", "kwallet")]
    [InlineData("", "xubuntu", "", "gnome_libsecret")]
    [InlineData("", "my-xfce-session", "", "gnome_libsecret")]
    public void Linux_StoreFollowsTheDesktop(string current, string session, string kdeVersion, string expected)
    {
        if (current.Length > 0) _env["XDG_CURRENT_DESKTOP"] = current;
        if (session.Length > 0) _env["DESKTOP_SESSION"] = session;
        if (kdeVersion.Length > 0) _env["KDE_SESSION_VERSION"] = kdeVersion;
        UseLinux();
        Assert.Equal(expected, Selected());
    }

    [Theory]
    [InlineData("Unity", "gnome-fallback-compiz", "Gnome")]
    [InlineData("Unity", "ubuntu", "Unity")]
    [InlineData(":KDE:", "", "Kde4")]
    [InlineData("Deepin", "", "Deepin")]
    [InlineData("Pantheon", "", "Pantheon")]
    [InlineData("UKUI", "", "Ukui")]
    [InlineData("", "deepin", "Deepin")]
    [InlineData("", "kde-plasma", "Kde4")]
    [InlineData("", "ukui", "Ukui")]
    public void Linux_DesktopDetection(string current, string session, string expected)
    {
        if (current.Length > 0) _env["XDG_CURRENT_DESKTOP"] = current;
        if (session.Length > 0) _env["DESKTOP_SESSION"] = session;
        Assert.Equal(Enum.Parse<LinuxDesktop>(expected), LinuxStoreSelection.DetectDesktop(name => _env.GetValueOrDefault(name)));
    }

    [Theory]
    [InlineData("GNOME_DESKTOP_SESSION_ID", "gnome_libsecret")]
    [InlineData("KDE_FULL_SESSION", "basic_text")]
    public void Linux_StoreFollowsOldVariables(string variable, string expected)
    {
        _env[variable] = "1";
        UseLinux();
        Assert.Equal(expected, Selected());
    }

    [Theory]
    [InlineData("basic", "basic_text")]
    [InlineData("gnome-libsecret", "gnome_libsecret")]
    [InlineData("kwallet", "kwallet")]
    [InlineData("kwallet5", "kwallet5")]
    [InlineData("kwallet6", "kwallet6")]
    [InlineData("bogus", "gnome_libsecret")]
    public void Linux_PasswordStoreSwitchWins(string store, string expected)
    {
        _env["XDG_CURRENT_DESKTOP"] = "GNOME";
        _store = store;
        UseLinux();
        Assert.Equal(expected, Selected());
    }

    [Fact]
    public void Linux_EncryptionSelectionFile_TurnsTheStoreOff()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            _env["XDG_CURRENT_DESKTOP"] = "GNOME";
            UseLinux(dir, selection: true);
            Assert.Equal("gnome_libsecret", Selected());
            File.WriteAllText(Path.Combine(dir, LinuxStoreSelection.BackendPreferenceFileName), "");
            UseLinux(dir, selection: false);
            Assert.Equal("gnome_libsecret", Selected());
            UseLinux(dir, selection: true);
            Assert.Equal("basic_text", Selected());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Linux_Async_WithoutASecretServiceUsesV10()
    {
        UseLinux();
        Assert.True(await SafeStorage.IsAsyncEncryptionAvailableAsync());
        Assert.Equal(s_v10Text, await SafeStorage.EncryptStringAsync(Text));
        Assert.Equal(new SafeStorageDecryptResult(false, Text), await SafeStorage.DecryptStringAsync(s_v10Text));
        // An unknown desktop still asks the Secret Service, unlike the synchronous selection.
        Assert.Equal(["service my-app|my-app Safe Storage"], _secrets.Calls);
        Assert.Equal(AsyncDecryptError, (await Assert.ThrowsAsync<InvalidOperationException>(() => SafeStorage.DecryptStringAsync(s_chromiumV11))).Message);
        Assert.Equal(AsyncDecryptError, (await Assert.ThrowsAsync<InvalidOperationException>(() => SafeStorage.DecryptStringAsync([1, 2, 3]))).Message);
        Assert.Empty(await SafeStorage.EncryptStringAsync(""));
    }

    [Fact]
    public async Task Linux_Async_PrefersV11AndAsksToReEncryptV10()
    {
        _secrets.ServiceSecret = "zxqfb"u8.ToArray();
        UseLinux();
        Assert.Equal(s_chromiumV11, await SafeStorage.EncryptStringAsync("Hello, World! This is a longer string."));
        Assert.Equal(new SafeStorageDecryptResult(false, "Hello, World! This is a longer string."), await SafeStorage.DecryptStringAsync(s_chromiumV11));
        Assert.Equal(new SafeStorageDecryptResult(true, Text), await SafeStorage.DecryptStringAsync(s_v10Text));
        Assert.Equal(new SafeStorageDecryptResult(false, "legacy"), await SafeStorage.DecryptStringAsync([.. "v11"u8, .. s_emptyKeyLegacy]));
        Assert.Single(_secrets.Calls);
    }

    [Theory]
    [InlineData("basic", "", false)]
    [InlineData("kwallet5", "", false)]
    [InlineData("", "KDE", false)]
    [InlineData("gnome-libsecret", "KDE", true)]
    [InlineData("", "LXQt", true)]
    public async Task Linux_Async_SecretServiceChoice(string store, string desktop, bool asks)
    {
        _store = store;
        if (desktop.Length > 0) _env["XDG_CURRENT_DESKTOP"] = desktop;
        _secrets.ServiceSecret = "zxqfb"u8.ToArray();
        UseLinux();
        var encrypted = await SafeStorage.EncryptStringAsync("x");
        Assert.Equal(asks ? "v11" : "v10", Encoding.ASCII.GetString(encrypted, 0, 3));
        Assert.Equal(asks, _secrets.Calls.Count == 1);
    }

    [Fact]
    public async Task Linux_Async_PlainTextBasicTextIsAvailableWithoutTheService()
    {
        UseLinux();
        SafeStorage.SetUsePlainTextEncryption(true);
        Assert.True(await SafeStorage.IsAsyncEncryptionAvailableAsync());
        Assert.Empty(_secrets.Calls);
    }

    // ---- macOS ----

    [Fact]
    public void Mac_UsesTheAppsKeychainItem()
    {
        _keychain.Password = "Tm90QVJlYWxQYXNzd29yZA=="u8.ToArray();
        UseMac();
        Assert.True(SafeStorage.IsEncryptionAvailable());
        Assert.Equal(s_macHello, SafeStorage.EncryptString("hello"));
        Assert.Equal("hello", SafeStorage.DecryptString(s_macHello));
        Assert.Equal(["find My App Safe Storage|My App"], _keychain.Calls);
    }

    [Fact]
    public void Mac_MissingItem_IsCreatedWithARandomPassword()
    {
        UseMac();
        Assert.True(SafeStorage.IsEncryptionAvailable());
        Assert.Equal(2, _keychain.Calls.Count);
        Assert.StartsWith("add My App Safe Storage|My App|", _keychain.Calls[1]);
        var password = _keychain.Password!;
        Assert.Equal(24, password.Length);
        Assert.Equal(16, Convert.FromBase64String(Encoding.ASCII.GetString(password)).Length);
        var encrypted = SafeStorage.EncryptString("hello");
        Assert.Equal(OsCryptCbc.Decrypt(OsCryptCbc.DeriveKey(password, 1003), encrypted.AsSpan(3)), "hello"u8.ToArray());
    }

    [Theory]
    [InlineData(-128, 0)] // errSecUserCanceled
    [InlineData(0, -61)] // add refused
    public void Mac_KeychainFailure_IsUnavailableAndNotRetried(int findStatus, int addStatus)
    {
        _keychain.FindStatus = findStatus;
        _keychain.AddStatus = addStatus;
        UseMac();
        Assert.False(SafeStorage.IsEncryptionAvailable());
        var calls = _keychain.Calls.Count;
        Assert.Equal(EncryptError + " Encryption is not available.", Assert.Throws<InvalidOperationException>(() => SafeStorage.EncryptString("x")).Message);
        Assert.Equal(calls, _keychain.Calls.Count);
    }

    [Fact]
    public void Mac_MockKeychain_IsChromiumsMockPassword()
    {
        UseMac(new MockKeychain());
        Assert.Equal(s_mockText, SafeStorage.EncryptString(Text));
        Assert.Equal(Text, SafeStorage.DecryptString(s_mockText));
    }

    [Fact]
    public void Mac_Sync_KnowsOnlyV10AndHasNoEmptyKeyFallback()
    {
        _keychain.Password = "Tm90QVJlYWxQYXNzd29yZA=="u8.ToArray();
        UseMac();
        Assert.Equal(DecryptError, Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString([.. "v11"u8, .. s_macHello[3..]])).Message);
        Assert.Equal(DecryptError, Assert.Throws<InvalidOperationException>(() => SafeStorage.DecryptString([.. "v10"u8, .. s_emptyKeyLegacy])).Message);
        Assert.Empty(SafeStorage.EncryptString(""));
    }

    [Fact]
    public async Task Mac_Async_SharesTheKeyAndFallsBackToTheEmptyKey()
    {
        _keychain.Password = "Tm90QVJlYWxQYXNzd29yZA=="u8.ToArray();
        UseMac();
        Assert.True(await SafeStorage.IsAsyncEncryptionAvailableAsync());
        Assert.Equal(s_macHello, await SafeStorage.EncryptStringAsync("hello"));
        Assert.Equal(new SafeStorageDecryptResult(false, "hello"), await SafeStorage.DecryptStringAsync(s_macHello));
        Assert.Equal(new SafeStorageDecryptResult(false, "legacy"), await SafeStorage.DecryptStringAsync([.. "v10"u8, .. s_emptyKeyLegacy]));
        Assert.Equal(AsyncDecryptError, (await Assert.ThrowsAsync<InvalidOperationException>(() => SafeStorage.DecryptStringAsync([.. "v11"u8, .. s_macHello[3..]]))).Message);
    }

    [Fact]
    public async Task Mac_Async_EmptyTextStillStartsTheEncryptor()
    {
        UseMac();
        Assert.Empty(await SafeStorage.EncryptStringAsync(""));
        Assert.NotEmpty(_keychain.Calls);
    }

    [Fact]
    public async Task Mac_Async_KeychainFailureIsTemporary()
    {
        _keychain.FindStatus = -25293; // errSecAuthFailed
        UseMac();
        Assert.True(await SafeStorage.IsAsyncEncryptionAvailableAsync());
        Assert.Equal(AsyncEncryptError, (await Assert.ThrowsAsync<InvalidOperationException>(() => SafeStorage.EncryptStringAsync("x"))).Message);
        Assert.Equal("safeStorage.decryptStringAsync is temporarily unavailable. Please try again.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => SafeStorage.DecryptStringAsync(s_macHello))).Message);
        Assert.Equal(AsyncDecryptError, (await Assert.ThrowsAsync<InvalidOperationException>(() => SafeStorage.DecryptStringAsync("v11x"u8.ToArray()))).Message);
    }

    [Fact]
    public void Mac_PlainTextHasNoEffect()
    {
        _keychain.FindStatus = -128;
        UseMac();
        SafeStorage.SetUsePlainTextEncryption(true);
        Assert.False(SafeStorage.IsEncryptionAvailable());
        Assert.Equal("unknown", SafeStorage.Backend.SelectedStorageBackend);
    }
}
