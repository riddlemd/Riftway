namespace Riftway.Cef.Platform;

/// <summary>Where Electron's <c>WindowButtonsProxy redraw</c> puts the traffic lights, in the title bar container's
/// bottom-left coordinates: the container's height and the three buttons' left edges and common bottom edge.</summary>
public readonly record struct MacWindowButtonsFrame(double ContainerHeight, double LeftX, double MiddleX, double RightX, double ButtonY);

/// <summary>The geometry of Electron v44's <c>WindowButtonsProxy</c> (shell/browser/ui/cocoa/window_buttons_proxy.mm).</summary>
/// <param name="ButtonWidth">The leftmost button's width.</param>
/// <param name="ButtonHeight">The leftmost button's height.</param>
/// <param name="Padding">The gap between the leftmost and middle buttons.</param>
/// <param name="DefaultMarginY">The margin AppKit gave the buttons, as the proxy read it when created.</param>
public readonly record struct MacWindowButtonsLayout(double ButtonWidth, double ButtonHeight, double Padding, double DefaultMarginY)
{
    /// <summary><c>useCustomHeight</c>: a height counts only when it is taller than the buttons with their default margins.</summary>
    public bool UsesCustomHeight(double height) => height > ButtonHeight + 2 * DefaultMarginY;

    /// <summary><c>redraw</c> for a window <paramref name="windowWidth"/> wide, with the margin <paramref name="marginX"/>,
    /// <paramref name="marginY"/> and the custom <paramref name="height"/> (0 for none). Right to left, the margin is from
    /// the window's right edge and the buttons run zoom, minimise, close.</summary>
    public MacWindowButtonsFrame Redraw(double windowWidth, double marginX, double marginY, double height, bool rightToLeft)
    {
        var start = rightToLeft ? windowWidth - 3 * ButtonWidth - 2 * Padding - marginX : marginX;
        var containerHeight = UsesCustomHeight(height) ? height : ButtonHeight + 2 * marginY;
        // getCurrentMargin, read after the container took its new height.
        var y = height != 0
            ? marginY != DefaultMarginY ? height - ButtonHeight - marginY : (height - ButtonHeight) / 2
            : (containerHeight - ButtonHeight) / 2;
        var step = ButtonWidth + Padding;
        return new MacWindowButtonsFrame(containerHeight, start, start + step, start + 2 * step, y);
    }
}
