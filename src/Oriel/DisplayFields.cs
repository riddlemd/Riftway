namespace Oriel;

/// <summary>Whether a display has a feature; Electron's <c>'available'</c>, <c>'unavailable'</c> or <c>'unknown'</c>.</summary>
public enum DisplayFeatureSupport
{
    /// <summary><c>'unknown'</c>.</summary>
    Unknown,

    /// <summary><c>'available'</c>.</summary>
    Available,

    /// <summary><c>'unavailable'</c>.</summary>
    Unavailable,
}

/// <remarks>The values follow Chromium's <c>display::Display</c> as Electron's converter reads it
/// (shell/common/gin_converters/gfx_converter.cc); where an OS does not set a field, Chromium's default stands.</remarks>
public sealed partial record Display
{
    /// <summary>The monitor's name: the EDID friendly name on Windows, empty without one. Electron's <c>label</c>.</summary>
    public string Label { get; init; } = "";

    /// <summary>Whether this is the built-in screen (a laptop panel). Electron's <c>internal</c>.</summary>
    public bool Internal { get; init; }

    /// <summary>The refresh rate in Hz; 0 when unknown. Electron's <c>displayFrequency</c>.</summary>
    public double DisplayFrequency { get; init; }

    /// <summary>Bits per pixel: 24, or 30 while HDR is on. Electron's <c>colorDepth</c>.</summary>
    public int ColorDepth { get; init; } = DisplayMapping.DefaultBitsPerPixel;

    /// <summary>Bits per colour component: 8, or 10 while HDR is on. Electron's <c>depthPerComponent</c>.</summary>
    public int DepthPerComponent { get; init; } = DisplayMapping.DefaultBitsPerComponent;

    /// <summary>The colour space pages are rasterized in, as Chromium prints a <c>gfx::ColorSpace</c>, e.g.
    /// <c>{primaries:BT709, transfer:SRGB, matrix:RGB, range:FULL}</c>. Electron's <c>colorSpace</c>.</summary>
    public string ColorSpace { get; init; } = DisplayMapping.Srgb;

    /// <summary>Whether the display is monochrome. Electron's <c>monochrome</c>.</summary>
    public bool Monochrome { get; init; }

    /// <summary>Whether the display takes touch input; Chromium on Windows leaves it unknown.
    /// Electron's <c>touchSupport</c>.</summary>
    public DisplayFeatureSupport TouchSupport { get; init; }

    /// <summary>Whether the display has an accelerometer; unknown outside ChromeOS. Electron's <c>accelerometerSupport</c>.</summary>
    public DisplayFeatureSupport AccelerometerSupport { get; init; }

    /// <summary>False only while Chromium has lost sight of every display and keeps the last ones. Electron's <c>detected</c>.</summary>
    public bool Detected { get; init; } = true;

    /// <summary>The display's origin before Chromium lays displays out: on Windows its pixel origin divided by its scale.
    /// Electron's <c>nativeOrigin</c>.</summary>
    public Point NativeOrigin { get; init; }

    /// <summary>The largest cursor the display can show; (0, 0) outside ChromeOS. Electron's <c>maximumCursorSize</c>.</summary>
    public (int Width, int Height) MaximumCursorSize { get; init; }

    // Chromium compares whole DisplayColorSpaces for a change (HDR white level, luminance), which ColorSpace alone does not show.
    internal string ColorSpacesKey { get; init; } = "";
}
