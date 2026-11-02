using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>What Chromium's <c>OsSettingsProviderWin</c> knows of Windows' settings. Colours are COLORREFs (0x00BBGGRR).</summary>
internal readonly record struct WindowsThemeInputs(
    bool AppsUseLightTheme,
    bool EnableTransparency,
    bool ForcedColorsActive,
    uint WindowColor,
    uint WindowTextColor,
    bool InvertFilter,
    uint? AccentColor,
    uint CaretBlinkTime);

/// <summary>Chromium's <c>NativeTheme::PreferredContrast</c>.</summary>
internal enum PreferredContrast { NoPreference, More, Less, Custom }

/// <summary>The native-UI <c>NativeTheme</c>'s members that <c>UpdateVariablesForToolkitSettings</c> recomputes; a
/// change in any of them notifies observers, so Electron emits <c>updated</c>. <see cref="Dark"/> ignores
/// <c>themeSource</c>, as <c>preferred_color_scheme_</c> does.</summary>
internal readonly record struct WindowsThemeVariables(
    bool ForcedColors,
    bool Dark,
    PreferredContrast Contrast,
    bool ReducedTransparency,
    bool Inverted,
    uint? Accent,
    uint CaretBlinkTime);

/// <summary>Chromium 154's Windows theme logic (<c>os_settings_provider_win.cc</c>, <c>os_settings_provider.cc</c>,
/// <c>native_theme.cc</c>) and Electron's getters over it (<c>electron_api_native_theme.cc</c>).</summary>
internal static class WindowsTheme
{
    public static WindowsThemeVariables Variables(in WindowsThemeInputs inputs) => new(
        inputs.ForcedColorsActive,
        PrefersDark(inputs),
        Contrast(inputs),
        !inputs.EnableTransparency,
        inputs.InvertFilter,
        inputs.AccentColor,
        inputs.CaretBlinkTime);

    /// <summary><c>OsSettingsProviderWin::PreferredColorScheme</c>: in a contrast theme, the window colour's Lab
    /// lightness decides when it is clearly dark (under 33) or light (over 67); otherwise <c>AppsUseLightTheme</c>.</summary>
    public static bool PrefersDark(in WindowsThemeInputs inputs)
    {
        if (inputs.ForcedColorsActive)
        {
            var lightness = LabLightness(inputs.WindowColor);
            if (lightness < 33f) return true;
            if (lightness > 67f) return false;
        }
        return !inputs.AppsUseLightTheme;
    }

    /// <summary><c>OsSettingsProvider::PreferredContrast</c>: only a contrast theme has a preference, by the WCAG ratio
    /// of window text to window (7 or more is More, 2.5 or less is Less).</summary>
    public static PreferredContrast Contrast(in WindowsThemeInputs inputs)
    {
        if (!inputs.ForcedColorsActive) return PreferredContrast.NoPreference;
        var ratio = ContrastRatio(inputs.WindowColor, inputs.WindowTextColor);
        if (ratio >= 7f) return PreferredContrast.More;
        return ratio <= 2.5f ? PreferredContrast.Less : PreferredContrast.Custom;
    }

    /// <summary>CIE Lab L* of an sRGB COLORREF, through D50 XYZ as <c>gfx::XYZD50ToLab</c> (only Y matters).</summary>
    public static float LabLightness(uint colorref)
    {
        var (r, g, b) = Linear(colorref);
        // Row Y of Skia's SkNamedGamut::kSRGB (sRGB primaries adapted to D50); kD50_y is 1.
        var y = 0.222488403f * r + 0.716873169f * g + 0.060607910f * b;
        const float delta = 24f / 116f * (24f / 116f) * (24f / 116f);
        var f = y <= delta ? 841f / 108f * y + 16f / 116f : MathF.Cbrt(y);
        return 116f * f - 16f;
    }

    /// <summary><c>color_utils::GetContrastRatio</c>.</summary>
    public static float ContrastRatio(uint a, uint b)
    {
        var la = Luminance(a) + 0.05f;
        var lb = Luminance(b) + 0.05f;
        return la > lb ? la / lb : lb / la;
    }

    private static float Luminance(uint colorref)
    {
        var (r, g, b) = Linear(colorref);
        return 0.2126f * r + 0.7152f * g + 0.0722f * b;
    }

    private static (float R, float G, float B) Linear(uint colorref)
    {
        static float Channel(uint value)
        {
            var c = value / 255f;
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return (Channel(colorref & 0xFF), Channel((colorref >> 8) & 0xFF), Channel((colorref >> 16) & 0xFF));
    }

    /// <summary>Whether <c>OsSettingsProviderWin</c> passes a notification on, and whether it forces observers to hear
    /// it even when nothing changed (<c>WM_SYSCOLORCHANGE</c> in a contrast theme).</summary>
    public static (bool Notify, bool Force) Gate(WindowsThemeChange change, in WindowsThemeInputs before, in WindowsThemeInputs after)
        => change switch
        {
            WindowsThemeChange.Personalize => (PrefersDark(before) != PrefersDark(after) || before.EnableTransparency != after.EnableTransparency, false),
            WindowsThemeChange.ColorFiltering => (before.InvertFilter != after.InvertFilter, false),
            WindowsThemeChange.Dwm => (before.AccentColor != after.AccentColor, false),
            WindowsThemeChange.HighContrast => (before.ForcedColorsActive != after.ForcedColorsActive, false),
            _ => (after.ForcedColorsActive, true),
        };

    /// <summary>The variables to adopt and announce after <paramref name="change"/>, or null when Electron emits nothing:
    /// the provider gate, then <c>NativeTheme::OnToolkitSettingsChanged</c>'s comparison.</summary>
    public static WindowsThemeVariables? Next(WindowsThemeChange change, in WindowsThemeInputs before, in WindowsThemeInputs after,
        in WindowsThemeVariables current)
    {
        var (notify, force) = Gate(change, before, after);
        if (!notify) return null;
        var next = Variables(after);
        return force || next != current ? next : null;
    }

    /// <summary>Electron's <c>shouldUseDarkColors</c>: the forced source, else the system's scheme.</summary>
    public static bool ShouldUseDarkColors(ThemeSource source, in WindowsThemeVariables variables) => source switch
    {
        ThemeSource.Dark => true,
        ThemeSource.Light => false,
        _ => variables.Dark,
    };

    /// <summary>Electron's <c>shouldUseHighContrastColors</c>: <c>preferred_contrast() == kMore</c>.</summary>
    public static bool ShouldUseHighContrastColors(in WindowsThemeVariables variables) => variables.Contrast == PreferredContrast.More;

    /// <summary>Electron's <c>shouldUseInvertedColorScheme</c> off macOS: forced colours with a dark scheme, the
    /// source's override included (Electron's <c>preferred_color_scheme()</c> patch).</summary>
    public static bool ShouldUseInvertedColorScheme(ThemeSource source, in WindowsThemeVariables variables)
        => variables.ForcedColors && ShouldUseDarkColors(source, variables);
}
