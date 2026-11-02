using System.Globalization;

namespace Riftway;

/// <summary>A window's accent colour on Windows 11: the system accent colour (<see langword="true"/>), none
/// (<see langword="false"/>), or a CSS colour string. Electron's <c>boolean | string</c> for <c>accentColor</c>;
/// a null <see cref="AccentColor"/> follows the system setting.</summary>
public sealed record AccentColor
{
    private AccentColor(bool enabled, string? color)
    {
        Enabled = enabled;
        Color = color;
    }

    /// <summary>The system accent colour, whatever the system setting says. Electron's <c>true</c>.</summary>
    public static AccentColor System { get; } = new(true, null);

    /// <summary>No accent colour, whatever the system setting says. Electron's <c>false</c>.</summary>
    public static AccentColor None { get; } = new(false, null);

    /// <summary>A CSS colour (hex, <c>rgb()</c>, <c>rgba()</c>, <c>hsl()</c>, <c>hsla()</c> or a named colour), alpha
    /// ignored; <c>#ARGB</c> and <c>#AARRGGBB</c> put the alpha first. Electron's colour string.</summary>
    public static AccentColor FromColor(string color) => new(true, color ?? throw new ArgumentNullException(nameof(color)));

    /// <summary>Whether an accent colour is shown: false only for <see cref="None"/>.</summary>
    public bool Enabled { get; }

    /// <summary>The CSS colour string, or null for <see cref="System"/> and <see cref="None"/>.</summary>
    public string? Color { get; }

    /// <summary><see langword="true"/> is <see cref="System"/>, <see langword="false"/> is <see cref="None"/>.</summary>
    public static implicit operator AccentColor(bool enabled) => enabled ? System : None;

    /// <summary>A colour string; see <see cref="FromColor"/>.</summary>
    public static implicit operator AccentColor(string color) => FromColor(color);
}

/// <summary>Electron's Windows frame rules: the frameless window style, <c>thickFrame</c>, <c>roundedCorners</c> and the
/// accent colour.</summary>
internal static class WindowsFrame
{
    internal const uint WsCaption = 0x00C00000, WsThickFrame = 0x00040000, WsMinimizeBox = 0x00020000, WsMaximizeBox = 0x00010000;

    /// <summary><c>DWMWA_COLOR_DEFAULT</c>: DWM's own border and caption colour.</summary>
    internal const uint ColorDefault = 0xFFFFFFFF;

    /// <summary>Electron's <c>CanResize</c> on Windows: a framed window without a thick frame cannot be resized.</summary>
    internal static bool CanResize(bool hasFrame, bool resizable, bool thickFrame) => hasFrame ? resizable && thickFrame : resizable;

    /// <summary>The <c>GWL_STYLE</c> Electron gives a frameless window (<c>WS_OVERLAPPED</c> is 0): a caption for the
    /// minimize and maximize animations, a thick frame when resizable, and neither without <c>thickFrame</c>.</summary>
    internal static uint FramelessStyle(bool resizable, bool minimizable, bool maximizable, bool thickFrame)
    {
        var style = WsCaption;
        if (resizable) style |= WsThickFrame;
        if (minimizable) style |= WsMinimizeBox;
        if (maximizable && resizable) style |= WsMaximizeBox;
        if (!thickFrame) style &= ~(WsThickFrame | WsCaption);
        return style;
    }

    /// <summary>Whether a frameless window asks for rounded corners: Electron forces them off without a thick frame.</summary>
    internal static bool RoundedCorners(bool thickFrame, bool roundedCorners) => thickFrame && roundedCorners;

    /// <summary>A parsed accent setting: null follows the system, otherwise on or off with an optional ARGB colour.</summary>
    internal readonly record struct Accent(bool Enabled, uint? Argb);

    /// <summary>Electron's <c>accentColor</c> option: a colour string Electron cannot parse is ignored, leaving the
    /// system setting in charge.</summary>
    internal static Accent? FromOption(AccentColor? option)
    {
        if (option is null) return null;
        if (option.Color is null) return new Accent(option.Enabled, null);
        return CssColor.TryParseElectron(option.Color, out var argb) ? new Accent(true, argb) : null;
    }

    /// <summary>The COLORREF Electron's <c>UpdateWindowAccentColor</c> gives DWM: an explicit colour always, the system
    /// colour when on (or, following the system, when the system shows it and the window is active), else
    /// <c>DWMWA_COLOR_DEFAULT</c>.</summary>
    internal static uint BorderColor(Accent? accent, bool active, bool systemShowsAccent, uint? systemArgb)
    {
        if (accent is { Argb: { } argb }) return ColorRef(argb);
        var apply = accent is { } a ? a.Enabled : systemShowsAccent && active;
        return apply && systemArgb is { } system ? ColorRef(system) : ColorDefault;
    }

    /// <summary>Electron's <c>GetAccentColor</c>: the explicit colour, or the system colour unless turned off or there is
    /// none, as <c>#RRGGBB</c>; null stands for Electron's <c>false</c>.</summary>
    internal static string? Report(Accent? accent, uint? systemArgb)
    {
        if (accent is { Argb: { } argb }) return ToRgbHex(argb);
        if (accent is { Enabled: false }) return null;
        return systemArgb is { } system ? ToRgbHex(system) : null;
    }

    /// <summary>Electron's <c>ToRGBHex</c>: <c>#RRGGBB</c>, upper case, alpha dropped.</summary>
    internal static string ToRgbHex(uint argb) => "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    // RGB(r, g, b): 0x00BBGGRR, alpha dropped.
    internal static uint ColorRef(uint argb) => ((argb >> 16) & 0xFF) | (argb & 0xFF00) | ((argb & 0xFF) << 16);
}
