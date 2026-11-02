namespace Riftway;

/// <summary>How a vibrant window's material follows the window's state (macOS). Electron's <c>visualEffectState</c>.</summary>
public enum VisualEffectState
{
    /// <summary>Active while the window is active. Electron's <c>followWindow</c>.</summary>
    FollowWindow,

    /// <summary>Always active. Electron's <c>active</c>.</summary>
    Active,

    /// <summary>Always inactive. Electron's <c>inactive</c>.</summary>
    Inactive,
}

/// <summary>Options for <see cref="BaseWindow.SetVibrancy"/>.</summary>
public sealed record VibrancyOptions
{
    /// <summary>Fades the material in or out over this many milliseconds; 0 for none. Electron's <c>animationDuration</c>.</summary>
    public int AnimationDuration { get; init; }
}

// Electron v44's NativeWindowMac::SetVibrancy and NativeWindow::IsTranslucent.
internal static class MacVibrancy
{
    /// <summary>The <c>NSVisualEffectMaterial</c> for Electron's type name; null for an unknown one, which Electron ignores.</summary>
    internal static long? Material(string type) => type switch
    {
        "titlebar" => 3,
        "selection" => 4,
        "menu" => 5,
        "popover" => 6,
        "sidebar" => 7,
        "header" => 10,
        "sheet" => 11,
        "window" => 12,
        "hud" => 13,
        "fullscreen-ui" => 15,
        "tooltip" => 17,
        "content" => 18,
        "under-window" => 21,
        "under-page" => 22,
        _ => null,
    };

    /// <summary>The <c>NSVisualEffectState</c>: follows the window 0, active 1, inactive 2.</summary>
    internal static long State(VisualEffectState state) => state switch
    {
        VisualEffectState.Active => 1,
        VisualEffectState.Inactive => 2,
        _ => 0,
    };

    /// <summary><c>UpdateVibrancyRadii</c>: the corner radius of the material's mask, or null for none. A titled window
    /// that is modal, or frameless and not modal, is rounded by 9, squared in full screen.</summary>
    internal static double? CornerRadius(bool titled, bool modal, bool hasFrame, bool fullScreen)
        => titled && (modal || !hasFrame) ? fullScreen ? 0 : 9 : null;

    /// <summary><c>IsTranslucent</c> on macOS: transparent, or with any vibrancy type, known or not.</summary>
    internal static bool IsTranslucent(bool transparent, string? vibrancy) => transparent || !string.IsNullOrEmpty(vibrancy);
}
