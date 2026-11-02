using System.Globalization;
using System.Text.Json;
using Riftway.Cef;
using Riftway.Cef.Interop;

namespace Riftway;

public sealed unsafe partial class Session
{
    // Chromium's local-state prefs, watched by SystemNetworkContextManager and SSLConfigServiceManager.
    internal const string AuthServerAllowlistPreference = "auth.server_allowlist";
    internal const string SslVersionMinPreference = "ssl.version_min";
    internal const string SslVersionMaxPreference = "ssl.version_max";
    internal const string DisabledCipherSuitesPreference = "ssl.cipher_suites.blacklist";

    private NetworkEmulationOptions? _networkEmulation;

    /// <summary>Where the session keeps its data on disk; null for an in-memory session. Electron's <c>ses.storagePath</c>.</summary>
    public string? StoragePath => _storagePath;

    /// <summary>Whether the built-in spell checker is on. Electron's <c>ses.spellCheckerEnabled</c>.</summary>
    public bool SpellCheckerEnabled
    {
        get => IsSpellCheckerEnabled();
        set => SetSpellCheckerEnabled(value);
    }

    /// <summary>Emulates a slow or absent network for the session's pages, open ones included. Electron's
    /// <c>ses.enableNetworkEmulation()</c>.</summary>
    /// <remarks>Requests the session makes without a page (<see cref="DownloadUrl"/>, <c>net</c>, service workers) are
    /// not throttled (drift E43).</remarks>
    public void EnableNetworkEmulation(NetworkEmulationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SetNetworkEmulation(options);
    }

    /// <summary>Ends <see cref="EnableNetworkEmulation"/>. Electron's <c>ses.disableNetworkEmulation()</c>.</summary>
    public void DisableNetworkEmulation() => SetNetworkEmulation(null);

    private void SetNetworkEmulation(NetworkEmulationOptions? options)
    {
        WebContents[] pages;
        lock (_pages)
        {
            _networkEmulation = options;
            pages = [.. _pages.Select(p => p.TryGetTarget(out var page) ? page : null).OfType<WebContents>()];
        }
        CefThread.RunOnUi(() =>
        {
            foreach (var page in pages) page.OnSessionNetworkEmulation(options);
        });
    }

    internal NetworkEmulationOptions? NetworkEmulation
    {
        get { lock (_pages) return _networkEmulation; }
    }

    /// <summary>Sets the TLS versions and cipher suites connections may use. Electron's <c>ses.setSSLConfig()</c>.</summary>
    /// <remarks>Applies to every session, not only this one (drift E44).</remarks>
    /// <exception cref="ArgumentException">A version other than <c>tls1.2</c> or <c>tls1.3</c>.</exception>
    public void SetSslConfig(SslConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var prefs = NetworkPrefs.Ssl(config);
        CefThread.InvokeOnUi(() =>
        {
            SetGlobalString(SslVersionMinPreference, prefs.MinVersion);
            SetGlobalString(SslVersionMaxPreference, prefs.MaxVersion);
            var list = CefNative.cef_list_value_create();
            for (var i = 0; i < prefs.DisabledCipherSuites.Count; i++)
            {
                cef_string_utf16_t text = default;
                CefString.Set(&text, prefs.DisabledCipherSuites[i]);
                list->set_string(list, (nuint)i, &text);
                CefString.Clear(&text);
            }
            var value = CefNative.cef_value_create();
            value->set_list(value, list);
            SetGlobalPreference(DisabledCipherSuitesPreference, value);
            return true;
        });
    }

    /// <summary>The servers that may receive the user's integrated (NTLM, Negotiate) credentials without asking: a
    /// comma-separated list, wildcards allowed, <c>*</c> for all. Applies to every session, as Electron's. Electron's
    /// <c>ses.allowNTLMCredentialsForDomains()</c>.</summary>
    public void AllowNtlmCredentialsForDomains(string domains)
    {
        ArgumentNullException.ThrowIfNull(domains);
        CefThread.InvokeOnUi(() =>
        {
            SetGlobalString(AuthServerAllowlistPreference, domains);
            return true;
        });
    }

    /// <summary>UI thread: drops what <see cref="SetSslConfig"/> and <see cref="AllowNtlmCredentialsForDomains"/> left in
    /// Local State, which outlives the run where Electron's settings do not.</summary>
    internal static void ResetProcessNetworkPreferences()
    {
        foreach (var name in new[] { AuthServerAllowlistPreference, SslVersionMinPreference, SslVersionMaxPreference, DisabledCipherSuitesPreference })
        {
            try { SetGlobalPreference(name, null); }
            catch (ArgumentException e) { Console.Error.WriteLine($"[Riftway] Could not reset {name}: {e.Message}"); }
        }
    }

    private static void SetGlobalString(string name, string text)
    {
        var value = CefNative.cef_value_create();
        cef_string_utf16_t s = default;
        CefString.Set(&s, text);
        value->set_string(value, &s);
        CefString.Clear(&s);
        SetGlobalPreference(name, value);
    }

    // Takes the value's reference; null resets the preference. UI thread.
    private static void SetGlobalPreference(string name, cef_value_t* value)
    {
        var manager = CefNative.cef_preference_manager_get_global();
        if (manager is null)
        {
            if (value is not null) NativeObject.Release(value);
            throw new InvalidOperationException("CEF has no global preferences yet.");
        }
        cef_string_utf16_t key = default, error = default;
        CefString.Set(&key, name);
        var ok = manager->set_preference(manager, &key, value, &error) != 0;
        var message = CefString.Read(&error);
        CefString.Clear(&key);
        CefString.Clear(&error);
        NativeObject.Release(manager);
        if (!ok) throw new ArgumentException($"Chromium rejected the '{name}' preference: {message}");
    }
}

/// <summary>What <see cref="Session.EnableNetworkEmulation"/> emulates. Electron's <c>enableNetworkEmulation</c> options.</summary>
public sealed record NetworkEmulationOptions
{
    /// <summary>Fail every request as if the network were down.</summary>
    public bool Offline { get; init; }

    /// <summary>Round-trip time in milliseconds; 0 adds none.</summary>
    public double Latency { get; init; }

    /// <summary>Download rate in bytes per second; 0 does not throttle.</summary>
    public double DownloadThroughput { get; init; }

    /// <summary>Upload rate in bytes per second; 0 does not throttle.</summary>
    public double UploadThroughput { get; init; }
}

/// <summary>What <see cref="Session.SetSslConfig"/> sets. Electron's <c>setSSLConfig</c> config.</summary>
public sealed record SslConfig
{
    /// <summary><c>tls1.2</c> or <c>tls1.3</c>; null for Chromium's minimum (TLS 1.2).</summary>
    public string? MinVersion { get; init; }

    /// <summary><c>tls1.2</c> or <c>tls1.3</c>; null for TLS 1.3.</summary>
    public string? MaxVersion { get; init; }

    /// <summary>Cipher suites to refuse besides those Chromium already refuses, as <c>0xAABB</c> numbers. TLS 1.3
    /// suites cannot be disabled.</summary>
    public IReadOnlyList<ushort>? DisabledCipherSuites { get; init; }
}

/// <summary>Electron's network settings as the DevTools parameters and Chromium preferences that carry them.</summary>
internal static class NetworkPrefs
{
    internal sealed record SslPrefs(string MinVersion, string MaxVersion, IReadOnlyList<string> DisabledCipherSuites);

    // Electron's SSLProtocolVersionFromString: Chromium's switches::kSSLVersionTLSv12 and TLSv13, nothing older.
    private static string Version(string? version, string name) => version switch
    {
        null => "",
        "tls1.2" or "tls1.3" => version,
        _ => throw new ArgumentException($"Invalid {name} '{version}': must be tls1.2 or tls1.3.", name),
    };

    /// <summary>The local-state values for <paramref name="config"/>: an empty version is Chromium's default, and cipher
    /// suites are <c>ParseSSLCipherString</c>'s six-character hex, sorted as Electron sorts them.</summary>
    internal static SslPrefs Ssl(SslConfig config) => new(
        Version(config.MinVersion, "minVersion"),
        Version(config.MaxVersion, "maxVersion"),
        [.. (config.DisabledCipherSuites ?? []).Order().Select(s => "0x" + s.ToString("X4", CultureInfo.InvariantCulture))]);

    /// <summary><c>Network.emulateNetworkConditionsByRule</c>'s parameters: one rule with an empty pattern matches every
    /// request, as Electron's single unmatched condition; null clears the rules.</summary>
    internal static void WriteEmulation(Utf8JsonWriter w, NetworkEmulationOptions? options)
    {
        w.WriteStartArray("matchedNetworkConditions");
        if (options is not null)
        {
            w.WriteStartObject();
            w.WriteString("urlPattern", "");
            w.WriteBoolean("offline", options.Offline);
            w.WriteNumber("latency", options.Latency);
            w.WriteNumber("downloadThroughput", options.DownloadThroughput);
            w.WriteNumber("uploadThroughput", options.UploadThroughput);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
