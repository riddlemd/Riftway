using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Linux behaviour that only a libcef with Oriel's CEF patches (<c>eng/cef</c>) can give; each call does
/// nothing and returns false on stock CEF and on other platforms, so the caller keeps its stock fallback.</summary>
internal static unsafe class LinuxCefPatches
{
    /// <summary>Turns all input to <paramref name="window"/> off or back on in Views (patch 0002), as Electron's
    /// <c>SetEnabledInternal</c> does for <c>setEnabled</c> and a shown modal's parent (DRIFT W5). UI thread.</summary>
    /// <returns>Whether libcef took it, so that no X11 input cover is needed.</returns>
    internal static bool SetWindowInputEnabled(cef_window_t* window, bool enabled)
        => OperatingSystem.IsLinux() && CefPatchedApi.SetEventListeningEnabled(window, enabled);

    /// <summary>Hands a status notifier host's <c>ProvideXdgActivationToken</c> token to the next window Chromium
    /// maps or activates (patch 0004; T5 in linux-tray's register). UI thread.</summary>
    /// <returns>Whether the token reached Chromium.</returns>
    internal static bool ProvideActivationToken(string token)
        => OperatingSystem.IsLinux() && CefPatchedApi.SetXdgActivationToken(token);
}
