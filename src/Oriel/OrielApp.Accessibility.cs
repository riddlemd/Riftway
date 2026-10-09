namespace Oriel;

public sealed partial class OrielApp
{
    // Electron's scoped_accessibility_mode_: whether the app asked for Chromium's complete accessibility mode.
    private volatile bool _accessibilityRequested;

    internal bool AccessibilityRequested => _accessibilityRequested;

    /// <summary>Accessibility support was turned on or off by <see cref="SetAccessibilitySupportEnabled"/>; the argument
    /// is the new state. Electron's <c>accessibility-support-changed</c>; Oriel does not see a screen reader start or
    /// stop (drift A28).</summary>
    public event EventHandler<bool>? AccessibilitySupportChanged;

    /// <summary>Whether Chromium's complete accessibility mode is on: here, whether
    /// <see cref="SetAccessibilitySupportEnabled"/> turned it on, since CEF does not report the mode a screen reader
    /// turns on (drift A28). Electron's <c>app.isAccessibilitySupportEnabled()</c>.</summary>
    public bool IsAccessibilitySupportEnabled() => _accessibilityRequested;

    /// <summary>Turns Chromium's complete accessibility mode (native APIs, web contents, inline text boxes, extended
    /// properties) on or off for every page, and for pages made later. Electron's
    /// <c>app.setAccessibilitySupportEnabled()</c>; turning it off also turns off what a screen reader turned on, as
    /// CEF sets each page's mode outright (drift A28).</summary>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    public void SetAccessibilitySupportEnabled(bool enabled)
    {
        if (!IsReady) throw new InvalidOperationException("app.setAccessibilitySupportEnabled() can only be called after app is ready");
        var changed = AccessibilityTransition(_accessibilityRequested, enabled);
        _accessibilityRequested = enabled;
        if (changed is { } apply)
            foreach (var page in WebContents.GetAllWebContents()) page.SetAccessibilityState(apply);
        AccessibilitySupportChanged?.Invoke(this, enabled);
    }

    // Electron keeps one scoped mode: enabling twice does not stack, and the event fires on every call.
    internal static bool? AccessibilityTransition(bool current, bool requested) => current == requested ? null : requested;

    /// <summary><see cref="IsAccessibilitySupportEnabled"/> and <see cref="SetAccessibilitySupportEnabled"/>. Electron's
    /// <c>app.accessibilitySupportEnabled</c>.</summary>
    public bool AccessibilitySupportEnabled
    {
        get => IsAccessibilitySupportEnabled();
        set => SetAccessibilitySupportEnabled(value);
    }
}
