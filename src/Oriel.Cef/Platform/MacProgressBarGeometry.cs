namespace Oriel.Cef.Platform;

/// <summary>Electron v44's <c>ElectronProgressBar drawRect:</c> (shell/browser/native_window_mac.mm): the outline is the
/// bounds inset by 1, the clipped inside the outline inset by 2, and the fill the inside's left part for the value.</summary>
public readonly record struct MacProgressBarGeometry(MacProgressBarGeometry.Rect Outline, MacProgressBarGeometry.Rect Inside, MacProgressBarGeometry.Rect Fill)
{
    /// <summary>A rectangle in the bar's coordinates.</summary>
    public readonly record struct Rect(double X, double Y, double Width, double Height);

    /// <summary>The geometry for a bar <paramref name="width"/> by <paramref name="height"/> at <paramref name="value"/> of
    /// <paramref name="max"/>; the fill's width is floored, as Electron's.</summary>
    public static MacProgressBarGeometry Of(double width, double height, double value, double max)
    {
        var outline = Inset(new Rect(0, 0, width, height), 1);
        var inside = Inset(outline, 2);
        return new MacProgressBarGeometry(outline, inside, inside with { Width = Math.Floor(inside.Width * (value / max)) });
    }

    private static Rect Inset(Rect r, double by) => new(r.X + by, r.Y + by, r.Width - 2 * by, r.Height - 2 * by);
}
