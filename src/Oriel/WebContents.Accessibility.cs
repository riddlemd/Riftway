using Oriel.Cef;

namespace Oriel;

/// <summary>Image animation and caret browsing.</summary>
public sealed partial class WebContents
{
    private bool _caretBrowsing;

    /// <summary>Sets how images animate: <c>animate</c>, <c>animateOnce</c> or <c>noAnimation</c>; anything else is
    /// ignored, as Electron's. Images already shown keep their policy until reloaded. Electron's
    /// <c>webContents.setImageAnimationPolicy()</c>; it applies to every page of the <see cref="Session"/> (drift C53).</summary>
    public void SetImageAnimationPolicy(string policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        // Chromium's names for Electron's three policies.
        var value = policy switch
        {
            "animate" => "allowed",
            "animateOnce" => "once",
            "noAnimation" => "none",
            _ => null,
        };
        if (value is null) return;
        CefThread.InvokeOnUi(() =>
        {
            Session.SetRendererPreference("settings.a11y.animation_policy", value);
            return true;
        });
    }

    /// <summary>Whether caret browsing is on: arrow keys move a caret through the page's text. Electron's
    /// <c>webContents.isCaretBrowsingEnabled()</c> and <c>caretBrowsingEnabled</c>.</summary>
    public bool IsCaretBrowsingEnabled() => CefThread.InvokeOnUi(() => _caretBrowsing);

    /// <summary>Turns caret browsing on or off, at once. Electron's <c>webContents.setCaretBrowsingEnabled()</c>; it applies
    /// to every page of the <see cref="Session"/> (drift C53).</summary>
    public void SetCaretBrowsingEnabled(bool enabled) => CefThread.InvokeOnUi(() =>
    {
        _caretBrowsing = enabled;
        Session.SetRendererPreference("settings.a11y.caretbrowsing.enabled", enabled);
        return true;
    });
}
