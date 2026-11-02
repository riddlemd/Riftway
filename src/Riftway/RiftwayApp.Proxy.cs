using Riftway.Cef;
using Riftway.Cef.Interop;

namespace Riftway;

public sealed unsafe partial class RiftwayApp
{
    /// <summary>Sets the proxy of the system network context (the app's own requests outside any session), until the
    /// app quits. Electron's <c>app.setProxy()</c>.</summary>
    /// <remarks>Through the global ("Local State") <c>proxy</c> preference, which Chromium's system network context
    /// follows; the change reaches it asynchronously, where Electron's promise waits for <c>ForceReloadProxyConfig</c>.</remarks>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    /// <exception cref="ArgumentException">Chromium rejected the configuration.</exception>
    public Task SetProxyAsync(ProxyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!IsReady) return Task.FromException(new InvalidOperationException("app.setProxy() can only be called after app is ready."));
        var entries = Session.ProxyPreference(config);
        CefThread.InvokeOnUi(() =>
        {
            var manager = CefNative.cef_preference_manager_get_global();
            if (manager is null) throw new InvalidOperationException("app.setProxy() failed due to internal error.");
            try { Session.SetPreference(manager, Session.ProxyPreferenceName, Session.ProxyValue(entries)); }
            finally { NativeObject.Release(manager); }
            return true;
        });
        return Task.CompletedTask;
    }

    /// <summary>The proxies the system network context would use for <paramref name="url"/>, in PAC format; empty when
    /// the lookup fails. Electron's <c>app.resolveProxy()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Stock CEF has no proxy lookup (drift A35); needs Riftway's CEF patch 0008.</exception>
    public Task<string> ResolveProxyAsync(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return Session.ResolveProxyCore(callback => CefPatchedApi.ResolveSystemProxy(url, (cef_resolve_proxy_callback_t*)callback));
    }

    private static void ForgetSavedSystemProxy()
    {
        var manager = CefNative.cef_preference_manager_get_global();
        if (manager is null) return;
        Session.ForgetSavedProxy(manager);
        NativeObject.Release(manager);
    }
}
