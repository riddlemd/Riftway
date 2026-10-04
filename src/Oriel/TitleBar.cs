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
    /// <summary>The buttons' background, <c>#RRGGBB</c>. Electron's <c>color</c>.</summary>
    public string? Color { get; init; }

    /// <summary>The buttons' symbols, <c>#RRGGBB</c>. Electron's <c>symbolColor</c>.</summary>
    public string? SymbolColor { get; init; }

    /// <summary>The overlay's height in DIPs. Electron's <c>height</c>.</summary>
    public int? Height { get; init; }
}

internal static class TitleBar
{
    /// <summary>Windows' caption button width in DIPs.</summary>
    internal const int ButtonWidth = 46;

    /// <summary>How much of the symbol colour a disabled button's symbol keeps over the background.</summary>
    internal const double DisabledSymbol = 0.4;

    /// <summary>Windows 11's close button background under the pointer.</summary>
    internal const uint CloseHover = 0xFFC42B1C;

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

    /// <summary>A caption button's background: the overlay colour, then a tenth and a fifth of the symbol colour over it
    /// for hover and press, as Windows tints its own; the close button turns red.</summary>
    internal static (uint Normal, uint Hover, uint Pressed) ButtonBackgrounds(uint color, uint symbol, bool close)
        => close
            ? (color, CloseHover, Blend(CloseHover, 0xFF000000, 0.1))
            : (color, Blend(color, symbol, 0.1), Blend(color, symbol, 0.2));

    /// <summary>The page's regions moved to window coordinates, where CEF hit-tests them: offset by the page's origin.</summary>
    internal static DraggableRegion[] ToWindow(IReadOnlyList<DraggableRegion> regions, int pageX, int pageY)
        => [.. regions.Select(r => r with { X = r.X + pageX, Y = r.Y + pageY })];
}
