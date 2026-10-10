using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

public sealed unsafe partial class Session
{
    /// <summary>Routes the session's requests through a proxy, until the app quits. Electron's <c>ses.setProxy()</c>.</summary>
    /// <remarks>Connections already open keep their route; <see cref="CloseAllConnectionsAsync"/> drops them.</remarks>
    /// <exception cref="ArgumentException">Chromium rejected the configuration.</exception>
    public Task SetProxyAsync(ProxyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var entries = ProxyPreference(config);
        CefThread.InvokeOnUi(() =>
        {
            SetPreference(_context, ProxyPreferenceName, ProxyValue(entries));
            return true;
        });
        return Task.CompletedTask;
    }

    /// <summary>The proxies the session would use for <paramref name="url"/>, in PAC format (<c>DIRECT</c>,
    /// <c>PROXY host:port</c>, several separated by <c>;</c>); empty when the lookup fails. Electron's
    /// <c>ses.resolveProxy()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Stock CEF has no proxy lookup (drift E8); needs Oriel's CEF patch 0008.</exception>
    public Task<string> ResolveProxyAsync(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var context = _context;
        return ResolveProxyCore(callback => CefPatchedApi.ResolveProxy(context, url, (cef_resolve_proxy_callback_t*)callback));
    }

    internal const string ProxyPreferenceName = "proxy";

    // start gets the callback's native pointer and its reference, on the UI thread.
    private static Task<(cef_errorcode_t Error, string Proxy)> StartResolveProxy(Func<IntPtr, bool> start)
    {
        var callback = new ResolveProxyCallback();
        var native = (IntPtr)callback.Native;
        CefThread.RunOnUi(() => start(native));
        return callback.Task;
    }

    /// <summary>Electron's proxy preference for <paramref name="config"/> (<c>ProxyConfigDictionary</c>'s factories as
    /// <c>Session::SetProxy</c> and <c>App::SetProxy</c> pick them), as key and string or bool value pairs.</summary>
    internal static IReadOnlyList<KeyValuePair<string, object>> ProxyPreference(ProxyConfig config)
    {
        var pac = config.PacScript ?? "";
        var rules = config.ProxyRules ?? "";
        var bypass = config.ProxyBypassRules ?? "";
        // pacScript takes precedence over proxyRules only when no mode is given.
        var mode = config.Mode ?? (pac.Length > 0 ? ProxyMode.PacScript : ProxyMode.FixedServers);
        // CreateFixedServers falls back to CreateDirect without a server.
        if (mode == ProxyMode.FixedServers && rules.Length == 0) mode = ProxyMode.Direct;
        List<KeyValuePair<string, object>> entries = [new("mode", ProxyModeName(mode))];
        if (mode == ProxyMode.PacScript && pac.Length > 0)
        {
            entries.Add(new("pac_url", pac));
            entries.Add(new("pac_mandatory", true));
        }
        else if (mode == ProxyMode.FixedServers)
        {
            entries.Add(new("server", rules));
            if (bypass.Length > 0) entries.Add(new("bypass_list", bypass));
        }
        return entries;
    }

    internal static string ProxyModeName(ProxyMode mode) => mode switch
    {
        ProxyMode.Direct => "direct",
        ProxyMode.AutoDetect => "auto_detect",
        ProxyMode.PacScript => "pac_script",
        ProxyMode.FixedServers => "fixed_servers",
        ProxyMode.System => "system",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    // The value's reference goes to the caller. UI thread.
    internal static cef_value_t* ProxyValue(IReadOnlyList<KeyValuePair<string, object>> entries)
    {
        var dictionary = CefNative.cef_dictionary_value_create();
        foreach (var (key, value) in entries)
        {
            if (value is bool flag)
            {
                cef_string_utf16_t k = default;
                CefString.Set(&k, key);
                dictionary->set_bool(dictionary, &k, flag ? 1 : 0);
                CefString.Clear(&k);
            }
            else SetString(dictionary, key, (string)value);
        }
        var result = CefNative.cef_value_create();
        result->set_dictionary(result, dictionary);
        return result;
    }

    // Electron keeps setProxy in an in-memory pref store; CEF writes it to the profile, so a new run starts without it.
    internal static void ForgetSavedProxy(cef_preference_manager_t* manager)
    {
        try { SetPreference(manager, ProxyPreferenceName, null); }
        catch (ArgumentException e) { Console.Error.WriteLine($"[Oriel] Could not reset the saved proxy: {e.Message}"); }
    }
}

// Outside the unsafe part: C# does not allow await in an unsafe context.
public sealed partial class Session
{
    internal static async Task<string> ResolveProxyCore(Func<IntPtr, bool> start)
    {
        if (!CefRuntime.IsPatched) throw new PlatformNotSupportedException("Proxy lookup needs Oriel's CEF patch 0008.");
        var (_, proxy) = await StartResolveProxy(start).ConfigureAwait(false);
        return proxy;
    }
}
