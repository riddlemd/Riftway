using Oriel.Cef.Handlers;

namespace Oriel;

/// <summary>How the window's title bar looks. Electron's <c>titleBarStyle</c>.</summary>
public enum TitleBarStyle
{
    /// <summary>The standard title bar. Electron's <c>default</c>.</summary>
    Default,

    /// <summary>No title bar; the page fills the window. On Windows and Linux with <see cref="BrowserWindowOptions.TitleBarOverlay"/>
    /// the window keeps its minimize, maximize and close buttons over the page. Electron's <c>hidden</c>.</summary>
    Hidden,

    /// <summary>macOS: hidden, with the traffic lights inset. Elsewhere as <see cref="Hidden"/>. Electron's <c>hiddenInset</c>.</summary>
    HiddenInset,

    /// <summary>macOS: hidden, the traffic lights shown on hover. Elsewhere as <see cref="Hidden"/>.
    /// Electron's <c>customButtonsOnHover</c>.</summary>
    CustomButtonsOnHover,
}

/// <summary>The window's buttons drawn over the page when the title bar is hidden; Electron's <c>titleBarOverlay</c>
/// object. Null colours and height take the system's.</summary>
public sealed record TitleBarOverlayOptions
{
    /// <summary>The buttons' background, a CSS colour as Chromium's parser takes it: <c>#RGB</c>, <c>#RGBA</c>,
    /// <c>#RRGGBB</c>, <c>#RRGGBBAA</c>, <c>rgb()</c>/<c>rgba()</c>, <c>hsl()</c>/<c>hsla()</c> or a named colour. Alpha
    /// shows the page through. Electron's <c>color</c>.</summary>
    public string? Color { get; init; }

    /// <summary>The buttons' symbols, a CSS colour as for <see cref="Color"/>. Electron's <c>symbolColor</c>.</summary>
    public string? SymbolColor { get; init; }

    /// <summary>The overlay's height in DIPs. Electron's <c>height</c>.</summary>
    public int? Height { get; init; }
}

internal static class TitleBar
{
    /// <summary>A caption button's width in DIPs before its spacing: Chromium's <c>kWindowsCaptionButtonWidth</c>.</summary>
    internal const int ButtonWidth = 45;

    /// <summary>The gap left of every button but the first, in DIPs, inside the button: Chromium's
    /// <c>kWindowsCaptionButtonVisualSpacing</c>. The button's background starts after it; its symbol centres on the whole.</summary>
    internal const int ButtonSpacing = 1;

    /// <summary>Button <paramref name="index"/>'s width with its spacing, as Electron's <c>CalculatePreferredSize</c>.</summary>
    internal static int ButtonWidthAt(int index) => ButtonWidth + SpacingAt(index);

    /// <summary>Button <paramref name="index"/>'s spacing: none for the first.</summary>
    internal static int SpacingAt(int index) => index == 0 ? 0 : ButtonSpacing;

    /// <summary>Electron's close button tint, <c>#E81123</c>: opaque under the pointer, <see cref="ClosePressedAlpha"/> pressed.</summary>
    internal const uint CloseBase = 0xFFE81123;

    /// <summary>The close button tint's alpha while pressed.</summary>
    internal const byte ClosePressedAlpha = 0x98;

    /// <summary>The overlay height where the OS gives none: Views' caption button layout height in DIPs (Linux).</summary>
    internal const int FallbackHeight = 32;

    /// <summary>Electron's rule: no frame, or a hidden title bar on Windows and Linux (macOS keeps its frame and moves the
    /// traffic lights instead).</summary>
    internal static bool IsFrameless(bool frame, TitleBarStyle style, bool isMacOS) => !frame || (style != TitleBarStyle.Default && !isMacOS);

    /// <summary>The overlay needs a hidden title bar and options; macOS draws its own buttons.</summary>
    internal static bool HasOverlay(TitleBarStyle style, TitleBarOverlayOptions? overlay, bool isMacOS)
        => overlay is not null && style != TitleBarStyle.Default && !isMacOS;

    /// <summary><paramref name="amount"/> of <paramref name="over"/> laid on <paramref name="under"/>, opaque.</summary>
    internal static uint Blend(uint under, uint over, double amount)
    {
        static uint Channel(uint under, uint over, int shift, double amount)
            => (uint)Math.Round(((under >> shift) & 0xFF) * (1 - amount) + ((over >> shift) & 0xFF) * amount) << shift;
        return 0xFF000000 | Channel(under, over, 16, amount) | Channel(under, over, 8, amount) | Channel(under, over, 0, amount);
    }

    /// <summary><paramref name="over"/> laid on <paramref name="under"/>, both straight (not premultiplied) ARGB, as
    /// source-over painting composes them.</summary>
    internal static uint Over(uint under, uint over)
    {
        double ao = (over >> 24) / 255.0, au = (under >> 24) / 255.0;
        var a = ao + au * (1 - ao);
        if (a == 0) return 0;
        uint Channel(int shift) => (uint)Math.Round((((over >> shift) & 0xFF) * ao + ((under >> shift) & 0xFF) * au * (1 - ao)) / a) << shift;
        return ((uint)Math.Round(a * 255) << 24) | Channel(16) | Channel(8) | Channel(0);
    }

    /// <summary>Chromium's caption foreground on Windows: white over a frame whose Windows luma is at most 128, else
    /// black.</summary>
    internal static uint CaptionForeground(uint frame)
    {
        var luma = 0.25f * ((frame >> 16) & 0xFF) + 0.625f * ((frame >> 8) & 0xFF) + 0.125f * (frame & 0xFF);
        return luma <= 128.0f ? 0xFFFFFFFF : 0xFF000000;
    }

    /// <summary>Chromium's default frame colour (<c>kColorFrameActiveUnthemed</c>) for the light or dark theme.</summary>
    internal static uint DefaultFrame(bool dark) => dark ? 0xFF202124 : 0xFFDEE1E6;

    /// <summary>What a caption button paints over the overlay's own background, as Electron's
    /// <c>WinCaptionButton::OnPaintBackground</c>: the overlay colour again, then the tint, the
    /// caption foreground (the close button: <see cref="CloseBase"/>) at the pressed alpha, or the hovered alpha scaled by
    /// <paramref name="hover"/>, the hover animation's value. Over a colour with alpha the tints are 1.3 times stronger.</summary>
    internal static uint ButtonFill(uint color, uint foreground, bool close, bool pressed, double hover)
    {
        byte hoveredAlpha = 0xFF, pressedAlpha = ClosePressedAlpha;
        if (!close)
        {
            (hoveredAlpha, pressedAlpha) = ((byte)0x1A, (byte)0x33);
            if (color >> 24 > 0)
            {
                hoveredAlpha = (byte)MathF.Round(hoveredAlpha * 1.3f, MidpointRounding.AwayFromZero);
                pressedAlpha = (byte)MathF.Round(pressedAlpha * 1.3f, MidpointRounding.AwayFromZero);
            }
        }
        var alpha = pressed ? pressedAlpha : (byte)IntValueBetween(hover, 0, hoveredAlpha);
        return Over(color, WithAlpha(close ? CloseBase : foreground, alpha));
    }

    /// <summary>The symbol colour, as Electron's <c>PaintSymbol</c>: the close button's fades to white with the hover
    /// animation and is white hovered or pressed.</summary>
    internal static uint SymbolColor(uint symbol, bool close, bool hoveredOrPressed, bool animating, double hover)
    {
        if (close && animating) return ColorValueBetween(hover, symbol, 0xFFFFFFFF);
        return close && hoveredOrPressed ? 0xFFFFFFFF : symbol;
    }

    /// <summary><c>gfx::Tween::IntValueBetween</c>: <paramref name="start"/> to <paramref name="target"/> at
    /// <paramref name="value"/>, truncating against one past the end so the target is reached only at 1.</summary>
    internal static int IntValueBetween(double value, int start, int target)
    {
        if (start == target) return start;
        double delta = target - start;
        delta = delta < 0 ? delta - 1 : delta + 1;
        return start + (int)(value * (delta < 0 ? Math.BitIncrement(delta) : Math.BitDecrement(delta)));
    }

    /// <summary><c>gfx::Tween::ColorValueBetween</c> for <c>SkColor</c>: premultiplied blending in float.</summary>
    internal static uint ColorValueBetween(double value, uint start, uint target)
    {
        float startA = (start >> 24) / 255f, targetA = (target >> 24) / 255f;
        var blendedA = (float)(startA + (targetA - startA) * value);
        if (blendedA <= 0f) return 0;
        blendedA = Math.Min(blendedA, 1f);
        uint Channel(int shift)
        {
            float s = ((start >> shift) & 0xFF) / 255f * startA, t = ((target >> shift) & 0xFF) / 255f * targetA;
            return ToByte((float)(s + (t - s) * value) / blendedA) << shift;
        }
        return (ToByte(blendedA) << 24) | Channel(16) | Channel(8) | Channel(0);
    }

    // base::ClampRound<uint8_t>(f * 255.0f).
    private static uint ToByte(float f) => (uint)Math.Clamp(MathF.Round(f * 255.0f, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>The colour with its alpha replaced.</summary>
    internal static uint WithAlpha(uint color, byte alpha) => (color & 0x00FFFFFF) | ((uint)alpha << 24);

    /// <summary>The page's regions moved to window coordinates, where CEF hit-tests them: offset by the page's origin.</summary>
    internal static DraggableRegion[] ToWindow(IReadOnlyList<DraggableRegion> regions, int pageX, int pageY)
        => [.. regions.Select(r => r with { X = r.X + pageX, Y = r.Y + pageY })];
}
