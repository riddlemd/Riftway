namespace Oriel;

/// <summary>Electron's <c>NativeWindowMac::SetKiosk</c> for one window: kiosk hides the Dock and menu bar and blocks the
/// Apple menu, app switching, Force Quit, logging out and hiding, keeping the app's earlier presentation options to put
/// back, and enters full screen unless the window already was.</summary>
internal sealed class MacKiosk(Func<nuint> currentOptions, Action<nuint> setOptions)
{
    // NSApplicationPresentationHideDock | HideMenuBar | DisableAppleMenu | DisableProcessSwitching | DisableForceQuit
    // | DisableSessionTermination | DisableHideApplication.
    internal const nuint Options = (1 << 1) | (1 << 3) | (1 << 4) | (1 << 5) | (1 << 6) | (1 << 7) | (1 << 8);

    private nuint _savedOptions;
    private bool _fullScreenBefore;

    /// <summary>Electron's <c>is_kiosk_</c>.</summary>
    internal bool IsKiosk { get; private set; }

    /// <summary>Enters or leaves kiosk; <paramref name="setFullScreen"/> runs where Electron changes full screen. Leaving
    /// sets the options back after leaving full screen, so that they take effect. With
    /// <paramref name="optionsAfterFullScreen"/> (simple full screen, which sets presentation options of its own and puts
    /// back the ones it found) entering sets kiosk's options after full screen, or simple full screen would replace them.</summary>
    internal void Set(bool kiosk, bool isFullScreen, Action<bool> setFullScreen, bool optionsAfterFullScreen = false)
    {
        if (kiosk && !IsKiosk)
        {
            _savedOptions = currentOptions();
            if (!optionsAfterFullScreen) setOptions(Options);
            IsKiosk = true;
            _fullScreenBefore = isFullScreen;
            if (!_fullScreenBefore) setFullScreen(true);
            if (optionsAfterFullScreen) setOptions(Options);
        }
        else if (!kiosk && IsKiosk)
        {
            IsKiosk = false;
            if (!_fullScreenBefore) setFullScreen(false);
            setOptions(_savedOptions);
        }
    }
}
