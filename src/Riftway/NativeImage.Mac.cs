using Riftway.Cef;
using Riftway.Cef.Platform;

// Each AppKit call runs behind an OperatingSystem.IsMacOS() check the analyzer cannot follow into the lambda.
#pragma warning disable CA1416

namespace Riftway;

/// <summary>An SF Symbol's stroke weight. Electron's <c>weight</c> for <c>createFromNamedImage</c>.</summary>
public enum SymbolWeight
{
    /// <summary>Electron's <c>regular</c>, the default.</summary>
    Regular,

    /// <summary>Electron's <c>ultralight</c>.</summary>
    UltraLight,

    /// <summary>Electron's <c>thin</c>.</summary>
    Thin,

    /// <summary>Electron's <c>light</c>.</summary>
    Light,

    /// <summary>Electron's <c>medium</c>.</summary>
    Medium,

    /// <summary>Electron's <c>semibold</c>.</summary>
    Semibold,

    /// <summary>Electron's <c>bold</c>.</summary>
    Bold,

    /// <summary>Electron's <c>heavy</c>.</summary>
    Heavy,

    /// <summary>Electron's <c>black</c>.</summary>
    Black,
}

/// <summary>An SF Symbol's scale. Electron's <c>scale</c> for <c>createFromNamedImage</c>.</summary>
public enum SymbolScale
{
    /// <summary>Electron's <c>medium</c>, the default.</summary>
    Medium,

    /// <summary>Electron's <c>small</c>.</summary>
    Small,

    /// <summary>Electron's <c>large</c>.</summary>
    Large,
}

/// <summary>Options for <see cref="NativeImage.CreateFromNamedImage(string, NamedImageOptions?)"/>; Electron's <c>options</c>.</summary>
public sealed record NamedImageOptions
{
    /// <summary>An SF Symbol's point size. Electron's <c>pointSize</c>, 30 by default.</summary>
    public double PointSize { get; init; } = 30;

    /// <summary>An SF Symbol's weight. Electron's <c>weight</c>.</summary>
    public SymbolWeight Weight { get; init; }

    /// <summary>An SF Symbol's scale. Electron's <c>scale</c>.</summary>
    public SymbolScale Scale { get; init; }

    /// <summary>Hue, saturation and lightness, applied when it has exactly three values: the absolute hue in 0..1 (or -1
    /// for none), then saturation and lightness in 0..1 with 0.5 for no change; a value out of range is no change.
    /// Electron's <c>hslShift</c>.</summary>
    public IReadOnlyList<double>? HslShift { get; init; }
}

// Electron v44's NativeImage::CreateFromNamedImage and CreateMenuSymbol rules.
internal static class NamedImageRules
{
    /// <summary>NSImageName constants spell differently from their strings (NSImageNameActionTemplate is "NSActionTemplate"),
    /// so the first "ImageName" is dropped.</summary>
    internal static string Normalize(string name)
    {
        var at = name.IndexOf("ImageName", StringComparison.Ordinal);
        return at < 0 ? name : name.Remove(at, "ImageName".Length);
    }

    /// <summary>A name without AppKit's NS or NX prefix is an SF Symbol.</summary>
    internal static bool IsSymbol(string name) => !name.StartsWith("NS", StringComparison.Ordinal) && !name.StartsWith("NX", StringComparison.Ordinal);

    /// <summary>The NSFontWeight value of each weight.</summary>
    internal static double FontWeight(SymbolWeight weight) => weight switch
    {
        SymbolWeight.UltraLight => -0.8,
        SymbolWeight.Thin => -0.6,
        SymbolWeight.Light => -0.4,
        SymbolWeight.Medium => 0.23,
        SymbolWeight.Semibold => 0.3,
        SymbolWeight.Bold => 0.4,
        SymbolWeight.Heavy => 0.56,
        SymbolWeight.Black => 0.62,
        _ => 0,
    };

    /// <summary>The NSImageSymbolScale value of each scale.</summary>
    internal static long ImageSymbolScale(SymbolScale scale) => scale switch
    {
        SymbolScale.Small => 1,
        SymbolScale.Large => 3,
        _ => 2,
    };

    /// <summary>Electron's <c>safeShift</c>: a value in 0..1, or the default itself, else the default.</summary>
    internal static double SafeShift(double value, double fallback) => (value >= 0 && value <= 1) || value == fallback ? value : fallback;

    /// <summary>A menu symbol 15 wide at its aspect ratio, or 15 tall when that would reach 16, the menu item's height.</summary>
    internal static (int Width, int Height) MenuSymbolSize(int width, int height)
    {
        var aspect = (float)width / height;
        var newHeight = (int)(15 / aspect);
        return newHeight >= 16 ? ((int)(15 * aspect), 15) : (15, newHeight);
    }
}

public sealed unsafe partial class NativeImage
{
    private readonly Lock _nsImageGate = new();
    private nint _nsImage;
    private int _nsImageVersion = -1;
    private int _version;

    /// <summary>An AppKit image by name (an <c>NSImageName</c> string such as <c>NSActionTemplate</c>) or an SF Symbol
    /// (any name without the NS or NX prefix), in the symbol configuration of <paramref name="options"/>; empty for an
    /// unknown name and off macOS. Electron's <c>nativeImage.createFromNamedImage()</c>.</summary>
    /// <remarks>Electron hands the shifted image through an NSImage and PNG again; Riftway keeps the shifted pixels, which can
    /// differ by one in a translucent pixel's premultiplied value.</remarks>
    public static NativeImage CreateFromNamedImage(string imageName, NamedImageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(imageName);
        if (!OperatingSystem.IsMacOS()) return CreateEmpty();
        options ??= new NamedImageOptions();
        var name = NamedImageRules.Normalize(imageName);
        var png = OnMain(() => NamedImageRules.IsSymbol(name)
            ? MacNamedImages.Symbol(name, options.PointSize, NamedImageRules.FontWeight(options.Weight), NamedImageRules.ImageSymbolScale(options.Scale))
            : MacNamedImages.Named(name));
        if (png is null) return CreateEmpty();
        var image = CreateFromBuffer(png);
        return options.HslShift is { Count: 3 } shift && !image.IsEmpty() ? image.HslShifted(shift) : image;
    }

    /// <summary><see cref="CreateFromNamedImage(string, NamedImageOptions?)"/> with only an HSL shift, Electron's deprecated
    /// <c>createFromNamedImage(name, hslShift)</c>.</summary>
    [Obsolete("createFromNamedImage(name, hslShift) is deprecated, use createFromNamedImage(name, { hslShift }) instead.")]
    public static NativeImage CreateFromNamedImage(string imageName, IReadOnlyList<double> hslShift)
        => CreateFromNamedImage(imageName, new NamedImageOptions { HslShift = hslShift });

    // CreateHSLShiftedImage of the 1x image Electron decodes the PNG into.
    internal NativeImage HslShifted(IReadOnlyList<double> shift)
    {
        var bitmap = Closest(1.0).Bitmap;
        var shifted = SkiaHslShift.Shift(bitmap, NamedImageRules.SafeShift(shift[0], -1), NamedImageRules.SafeShift(shift[1], 0.5), NamedImageRules.SafeShift(shift[2], 0.5));
        return CreateFromBitmap(shifted.Bgra, new NativeImageBitmapOptions { Width = shifted.Width, Height = shifted.Height });
    }

    /// <summary>An SF Symbol sized for a menu item (13 points, semibold, small, then 15 wide or 15 tall), as a template
    /// image; empty for an unknown name and off macOS. Electron's <c>nativeImage.createMenuSymbol()</c>.</summary>
    public static NativeImage CreateMenuSymbol(string imageName)
    {
        ArgumentNullException.ThrowIfNull(imageName);
        if (!OperatingSystem.IsMacOS()) return CreateEmpty();
        // NSFontWeightSemibold, NSImageSymbolScaleSmall.
        var png = OnMain(() => MacNamedImages.Symbol(imageName, 13, 0.3, 1));
        if (png is null) return CreateEmpty();
        var image = CreateFromBuffer(png);
        if (image.IsEmpty()) return image;
        var (width, height) = image.GetSize();
        var (newWidth, newHeight) = NamedImageRules.MenuSymbolSize(width, height);
        var sized = image.Resize(new ResizeOptions { Width = newWidth, Height = newHeight });
        sized.SetTemplateImage(true);
        return sized;
    }

    /// <summary>The address of the image's <c>NSImage</c>, as the bytes of a pointer (empty for an empty image); the
    /// NSImage lives as long as this image and is made again after the image changes (macOS). Electron's
    /// <c>image.getNativeHandle()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on macOS, where Electron throws "Not implemented".</exception>
    public byte[] GetNativeHandle()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Not implemented");
        if (IsEmpty()) return [];
        lock (_nsImageGate)
        {
            // Read before the snapshot, so a change made while building it is not taken as built.
            var version = Volatile.Read(ref _version);
            if (_nsImageVersion != version)
            {
                var old = _nsImage;
                var representations = Snapshot().Select(r => (ToPng(r.Scale), (double)r.Scale)).ToList();
                var template = IsTemplateImage();
                _nsImage = OnMain(() => MacImages.FromRepresentations(representations, template));
                _nsImageVersion = version;
                // Only an image that made an NSImage needs finalizing.
                if (old == 0) GC.ReRegisterForFinalize(this);
                if (old != 0) OnMain(() => { MacImages.Release(old); return 0; });
            }
            return BitConverter.GetBytes((long)_nsImage);
        }
    }

    // The cached NSImage itself, as gfx::Image::ToNSImage hands it out.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    internal nint NativeHandle() => GetNativeHandle() is { Length: 8 } bytes ? (nint)BitConverter.ToInt64(bytes) : 0;

    /// <summary>Releases the NSImage behind <see cref="GetNativeHandle"/>.</summary>
    ~NativeImage()
    {
        if (_nsImage != 0 && OperatingSystem.IsMacOS()) MacImages.Release(_nsImage);
    }

    // Representations and the template flag change the NSImage.
    private void Changed() => Interlocked.Increment(ref _version);

    private static T OnMain<T>(Func<T> func) => RiftwayApp.CurrentOrNull is { IsReady: true } && !CefThread.IsOnUiThread ? CefThread.InvokeOnUi(func) : func();
}
