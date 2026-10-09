using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>Chromium's <c>base::nix::DesktopEnvironment</c>.</summary>
internal enum LinuxDesktop { Other, Cinnamon, Deepin, Gnome, Kde3, Kde4, Kde5, Kde6, Pantheon, Ukui, Unity, Xfce, Lxqt, Cosmic }

/// <summary>Chromium's <c>os_crypt::SelectedLinuxBackend</c>, less <c>DEFER</c>, which Electron never selects.</summary>
internal enum LinuxPasswordStore { BasicText, GnomeLibsecret, KWallet, KWallet5, KWallet6 }

/// <summary>What os_crypt reads at startup on Linux: <c>--password-store</c>, <c>--enable-encryption-selection</c> with
/// the session data directory, the environment, and the app's name.</summary>
internal sealed record LinuxOsCryptConfig(string Store, bool ShouldUsePreference, string? UserDataPath, string AppName, Func<string, string?> Environment);

/// <summary>Store selection as Chromium does it (<c>key_storage_util_linux.cc</c>, <c>base/nix/xdg_util.cc</c>).</summary>
internal static class LinuxStoreSelection
{
    /// <summary>The file in the session data directory that, under <c>--enable-encryption-selection</c>, turns the
    /// secret store off (<c>kBackendPreferenceFileName</c>).</summary>
    internal const string BackendPreferenceFileName = "Disable Local Encryption";

    /// <summary><c>base::nix::GetDesktopEnvironment()</c>.</summary>
    internal static LinuxDesktop DetectDesktop(Func<string, string?> env)
    {
        if (env("XDG_CURRENT_DESKTOP") is { } current)
        {
            foreach (var raw in current.Split(':'))
            {
                var value = raw.Trim();
                switch (value)
                {
                    case "Unity":
                        return (env("DESKTOP_SESSION") ?? "").Contains("gnome-fallback", StringComparison.Ordinal) ? LinuxDesktop.Gnome : LinuxDesktop.Unity;
                    case "Deepin": return LinuxDesktop.Deepin;
                    case "GNOME": return LinuxDesktop.Gnome;
                    case "X-Cinnamon": return LinuxDesktop.Cinnamon;
                    case "KDE":
                        return (env("KDE_SESSION_VERSION") ?? "") switch
                        {
                            "5" => LinuxDesktop.Kde5,
                            "6" => LinuxDesktop.Kde6,
                            _ => LinuxDesktop.Kde4,
                        };
                    case "Pantheon": return LinuxDesktop.Pantheon;
                    case "XFCE": return LinuxDesktop.Xfce;
                    case "UKUI": return LinuxDesktop.Ukui;
                    case "LXQt": return LinuxDesktop.Lxqt;
                    case "COSMIC": return LinuxDesktop.Cosmic;
                }
            }
        }

        var session = env("DESKTOP_SESSION") ?? "";
        switch (session)
        {
            case "deepin": return LinuxDesktop.Deepin;
            case "gnome" or "mate": return LinuxDesktop.Gnome;
            case "kde4" or "kde-plasma": return LinuxDesktop.Kde4;
            case "kde": return env("KDE_SESSION_VERSION") is not null ? LinuxDesktop.Kde4 : LinuxDesktop.Kde3;
        }
        if (session.Contains("xfce", StringComparison.Ordinal) || session == "xubuntu") return LinuxDesktop.Xfce;
        if (session == "ukui") return LinuxDesktop.Ukui;

        if (env("GNOME_DESKTOP_SESSION_ID") is not null) return LinuxDesktop.Gnome;
        if (env("KDE_FULL_SESSION") is not null) return env("KDE_SESSION_VERSION") is not null ? LinuxDesktop.Kde4 : LinuxDesktop.Kde3;
        return LinuxDesktop.Other;
    }

    /// <summary><c>os_crypt::SelectBackend()</c>: an explicit <c>--password-store</c> wins, then the desktop.</summary>
    internal static LinuxPasswordStore Select(string store, bool useBackend, LinuxDesktop desktop) => store switch
    {
        "kwallet" => LinuxPasswordStore.KWallet,
        "kwallet5" => LinuxPasswordStore.KWallet5,
        "kwallet6" => LinuxPasswordStore.KWallet6,
        "gnome-libsecret" => LinuxPasswordStore.GnomeLibsecret,
        "basic" => LinuxPasswordStore.BasicText,
        _ when !useBackend => LinuxPasswordStore.BasicText,
        _ => desktop switch
        {
            LinuxDesktop.Kde4 => LinuxPasswordStore.KWallet,
            LinuxDesktop.Kde5 => LinuxPasswordStore.KWallet5,
            LinuxDesktop.Kde6 => LinuxPasswordStore.KWallet6,
            LinuxDesktop.Cinnamon or LinuxDesktop.Deepin or LinuxDesktop.Gnome or LinuxDesktop.Pantheon or LinuxDesktop.Ukui
                or LinuxDesktop.Unity or LinuxDesktop.Xfce or LinuxDesktop.Cosmic => LinuxPasswordStore.GnomeLibsecret,
            _ => LinuxPasswordStore.BasicText,
        },
    };

    /// <summary>The name <c>safeStorage.getSelectedStorageBackend()</c> reports (Electron's <c>SetLinuxStorageBackend</c>).</summary>
    internal static string Name(LinuxPasswordStore store) => store switch
    {
        LinuxPasswordStore.GnomeLibsecret => "gnome_libsecret",
        LinuxPasswordStore.KWallet => "kwallet",
        LinuxPasswordStore.KWallet5 => "kwallet5",
        LinuxPasswordStore.KWallet6 => "kwallet6",
        _ => "basic_text",
    };

    /// <summary>The store for <paramref name="config"/>, as both Electron's startup and os_crypt's <c>CreateService</c> pick it.</summary>
    internal static LinuxPasswordStore Select(LinuxOsCryptConfig config)
    {
        var useBackend = !config.ShouldUsePreference || !BackendDisabled(config.UserDataPath);
        return Select(config.Store, useBackend, DetectDesktop(config.Environment));
    }

    // os_crypt::GetBackendUse(): the preference file turns the store off; no directory leaves it on.
    private static bool BackendDisabled(string? userDataPath)
        => !string.IsNullOrEmpty(userDataPath) && File.Exists(Path.Combine(userDataPath, BackendPreferenceFileName));

    /// <summary>Whether os_crypt_async's <c>FreedesktopSecretKeyProvider</c> goes to the Secret Service (rather than
    /// KWallet, or nothing under <c>--password-store=basic</c>). It reads the desktop itself, so an unknown desktop still
    /// tries the Secret Service, unlike the synchronous selection.</summary>
    internal static bool AsyncUsesSecretService(LinuxOsCryptConfig config) => config.Store switch
    {
        "basic" or "kwallet" or "kwallet5" or "kwallet6" => false,
        "gnome-libsecret" => true,
        _ => DetectDesktop(config.Environment) is not (LinuxDesktop.Kde3 or LinuxDesktop.Kde4 or LinuxDesktop.Kde5 or LinuxDesktop.Kde6),
    };
}

/// <summary>Where a Linux os_crypt key's password comes from.</summary>
internal interface ISecretStore
{
    /// <summary><c>KeyStorageLibsecret::GetKey()</c>: the password stored for <paramref name="appName"/>, a new one stored
    /// when there is none, or null when the Secret Service cannot be used.</summary>
    byte[]? GetLibsecretPassword(string appName);

    /// <summary><c>FreedesktopSecretKeyProvider</c>'s secret: the default collection's item for <paramref name="appName"/>,
    /// or a new one labelled <paramref name="keyName"/>; null on failure.</summary>
    byte[]? GetSecretServiceSecret(string appName, string keyName);
}

/// <summary>os_crypt on Linux, as Electron sets it up. The synchronous API encrypts "v11" under PBKDF2 (1 iteration) of
/// the secret store's password when the selected store gives one, else "v10" under Chromium's fixed key; it is
/// "available" only with the v11 key. The asynchronous encryptor has two keys: "v10" (<c>PosixKeyProvider</c>) and "v11"
/// (<c>FreedesktopSecretKeyProvider</c>), and encrypts with "v11" when it has it.</summary>
internal sealed class LinuxOsCryptBackend(Func<LinuxOsCryptConfig> config, ISecretStore secrets) : ISafeStorageBackend
{
    private readonly Lock _lock = new();
    private LinuxOsCryptConfig? _config;
    private bool _triedV11;
    private byte[]? _v11Key;
    private bool _asyncLoaded;
    private byte[]? _asyncV11Key;

    internal static LinuxOsCryptBackend CreateDefault() => new(
        () =>
        {
            var app = OrielApp.CurrentOrNull;
            return new LinuxOsCryptConfig(
                app?.CommandLine.GetSwitchValue("password-store") ?? "",
                app?.CommandLine.HasSwitch("enable-encryption-selection") == true,
                OrielApp.DataDirectory,
                app?.Name ?? "",
                Environment.GetEnvironmentVariable);
        },
        new LibsecretStore());

    private LinuxOsCryptConfig Config
    {
        get
        {
            lock (_lock) return _config ??= config();
        }
    }

    public string SelectedStorageBackend => LinuxStoreSelection.Name(LinuxStoreSelection.Select(Config));

    public bool IsEncryptionAvailable => V11Key is not null;

    // KWallet is selected but not implemented, so it fails as a store that cannot initialise does.
    private byte[]? V11Key
    {
        get
        {
            lock (_lock)
            {
                if (_v11Key is null && !_triedV11)
                {
                    _triedV11 = true;
                    var current = Config;
                    if (LinuxStoreSelection.Select(current) == LinuxPasswordStore.GnomeLibsecret
                        && secrets.GetLibsecretPassword(current.AppName) is { } password)
                        _v11Key = OsCryptCbc.DeriveKey(password, 1);
                }
                return _v11Key;
            }
        }
    }

    public byte[]? Encrypt(byte[] plain)
    {
        if (plain.Length == 0) return [];
        return V11Key is { } key ? OsCryptCbc.Encrypt("v11"u8, key, plain) : OsCryptCbc.Encrypt("v10"u8, OsCryptCbc.V10Key, plain);
    }

    public byte[]? Decrypt(byte[] encrypted)
    {
        if (encrypted.Length == 0) return [];
        var span = encrypted.AsSpan();
        if (span.StartsWith("v10"u8)) return OsCryptCbc.DecryptWithEmptyKeyFallback(OsCryptCbc.V10Key, span[3..]);
        if (span.StartsWith("v11"u8) && V11Key is { } key) return OsCryptCbc.DecryptWithEmptyKeyFallback(key, span[3..]);
        return null;
    }

    public void LoadAsyncKeys()
    {
        lock (_lock)
        {
            if (_asyncLoaded) return;
            _asyncLoaded = true;
            var current = Config;
            if (LinuxStoreSelection.AsyncUsesSecretService(current)
                && secrets.GetSecretServiceSecret(current.AppName, current.AppName + " Safe Storage") is { Length: > 0 } secret)
                _asyncV11Key = OsCryptCbc.DeriveKey(secret, 1);
        }
    }

    public byte[]? AsyncEncrypt(byte[] plain)
    {
        LoadAsyncKeys();
        return _asyncV11Key is { } key ? OsCryptCbc.Encrypt("v11"u8, key, plain) : OsCryptCbc.Encrypt("v10"u8, OsCryptCbc.V10Key, plain);
    }

    // A provider that fails here fails permanently, so its tag is simply absent from the key ring.
    public AsyncDecryptOutcome AsyncDecrypt(byte[] encrypted)
    {
        LoadAsyncKeys();
        var span = encrypted.AsSpan();
        var encryptingTag = _asyncV11Key is null ? "v10" : "v11";
        if (span.StartsWith("v10"u8))
            return new(OsCryptCbc.DecryptWithEmptyKeyFallback(OsCryptCbc.V10Key, span[3..]), ShouldReEncrypt: encryptingTag != "v10");
        if (span.StartsWith("v11"u8) && _asyncV11Key is { } key)
            return new(OsCryptCbc.DecryptWithEmptyKeyFallback(key, span[3..]));
        return new(null);
    }
}

/// <summary>The Secret Service through libsecret (<see cref="LinuxLibsecret"/>).</summary>
internal sealed class LibsecretStore : ISecretStore
{
    public byte[]? GetLibsecretPassword(string appName)
        => OperatingSystem.IsLinux() ? LinuxLibsecret.GetOsCryptPassword(appName) : null;

    public byte[]? GetSecretServiceSecret(string appName, string keyName)
        => OperatingSystem.IsLinux() ? LinuxLibsecret.GetDefaultCollectionSecret(appName, keyName) : null;
}
