using Riftway.Cef;
using Riftway.Cef.Interop;

namespace Riftway;

public sealed unsafe partial class Session
{
    private volatile Func<WebContents?, string, string, PermissionCheckDetails, bool>? _permissionCheckHandler;
    private volatile Func<DisplayMediaRequest, Task<DisplayMediaStreams?>>? _displayMediaRequestHandler;

    /// <summary>Answers the session's permission checks: the handler gets the page, Electron's permission name, the
    /// checking origin (<c>https://host/</c>) and details, and grants with true. With none, every check is granted, as
    /// Electron. Electron's <c>ses.setPermissionCheckHandler()</c>.</summary>
    /// <remarks>Only <c>navigator.permissions.query()</c> in the page's own frames is answered here; Chromium's other
    /// checks follow its remembered prompt answers (drift E28).</remarks>
    public void SetPermissionCheckHandler(Func<WebContents?, string, string, PermissionCheckDetails, bool>? handler)
        => _permissionCheckHandler = handler;

    /// <summary>Decides the session's <c>getDisplayMedia()</c> requests, after the permission request handler grants
    /// <c>media</c>: the handler's streams say what is captured; null refuses. With none, every request is refused, as
    /// Electron. Electron's <c>ses.setDisplayMediaRequestHandler()</c>.</summary>
    /// <remarks>CEF captures the whole desktop whichever source is named, and has no tab capture (drift E30).</remarks>
    public void SetDisplayMediaRequestHandler(Func<DisplayMediaRequest, Task<DisplayMediaStreams?>>? handler)
        => _displayMediaRequestHandler = handler;

    internal bool CheckPermission(WebContents page, string permission, string requestingOrigin, PermissionCheckDetails details)
    {
        if (_permissionCheckHandler is not { } handler) return permission != "deprecated-sync-clipboard-read";
        try { return handler(page, permission, requestingOrigin, details); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Riftway] The permission check handler failed; denying {permission}: {e}");
            return false;
        }
    }

    /// <summary>UI thread: drops the refusal Chromium stored for a prompt, so the next request asks the handler again,
    /// as Electron asks on every request.</summary>
    internal void ForgetRefusal(string requestingOrigin, string? topLevelUrl, uint mask)
    {
        cef_string_utf16_t requesting = default, topLevel = default;
        CefString.Set(&requesting, requestingOrigin);
        CefString.Set(&topLevel, topLevelUrl ?? requestingOrigin);
        foreach (var setting in PermissionNames.SettingsOf(mask))
        {
            if (_context->get_content_setting(_context, &requesting, &topLevel, setting) == cef_content_setting_values_t.CEF_CONTENT_SETTING_VALUE_BLOCK)
                _context->set_content_setting(_context, &requesting, &topLevel, setting, cef_content_setting_values_t.CEF_CONTENT_SETTING_VALUE_DEFAULT);
        }
        CefString.Clear(&requesting);
        CefString.Clear(&topLevel);
    }

    /// <summary>UI thread: drops the prompt answers a persistent profile kept from earlier runs; Electron remembers none.</summary>
    internal static void ForgetRememberedPermissions(cef_request_context_t* context)
    {
        var manager = (cef_preference_manager_t*)context;
        foreach (var name in PermissionNames.RememberedPreferences)
        {
            var key = "profile.content_settings.exceptions." + name;
            cef_string_utf16_t k = default;
            CefString.Set(&k, key);
            var known = manager->has_preference(manager, &k) != 0;
            CefString.Clear(&k);
            if (!known) continue;
            var value = CefNative.cef_value_create();
            value->set_dictionary(value, CefNative.cef_dictionary_value_create());
            try { SetPreference(context, key, value); }
            catch (ArgumentException e) { Console.Error.WriteLine($"[Riftway] Could not forget remembered permissions: {e.Message}"); }
        }
    }
}

// Outside the unsafe part: C# does not allow await in an unsafe context.
public sealed partial class Session
{
    // Chromium CHECK-fails on Linux without a PulseAudio loopback source; Electron documents loopback for Windows and macOS.
    private static bool LoopbackSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOSVersionAtLeast(13);

    /// <summary>The CEF media mask the display media handler allows for <paramref name="request"/>; 0 refuses.</summary>
    internal async Task<uint> ChooseDisplayMediaAsync(DisplayMediaRequest request, uint requested)
    {
        if (_displayMediaRequestHandler is not { } handler) return 0;
        DisplayMediaStreams? streams;
        try { streams = await handler(request).ConfigureAwait(false); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Riftway] The display media request handler failed; refusing: {e}");
            return 0;
        }
        var (allowed, error) = PermissionNames.DisplayMediaAllowed(requested, streams, LoopbackSupported);
        if (error is not null) Console.Error.WriteLine($"[Riftway] Display media request refused: {error}");
        return allowed;
    }
}
