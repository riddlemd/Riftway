using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The application's Dock tile: a progress bar over its icon, and bouncing for attention. Main thread only.</summary>
/// <remarks>The Dock tile belongs to the application, so every window shares one progress bar, as in Electron.</remarks>
[SupportedOSPlatform("macos")]
public static unsafe class MacDock
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    // NSInformationalRequest: one bounce, as Electron's flashFrame.
    private const nint InformationalRequest = 10;
    // NSProgressIndicatorStyleBar.
    private const nuint BarStyle = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize
    {
        public double Width, Height;
    }

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static nint s_indicator;

    /// <summary>Shows progress over the Dock icon: <paramref name="value"/> from 0 to 1, a cycling bar when
    /// <paramref name="indeterminate"/>, or removes it when <paramref name="value"/> is null and not indeterminate.</summary>
    public static void SetProgress(double? value, bool indeterminate)
    {
        var tile = Send(App(), Sel("dockTile"u8));
        if (value is null && !indeterminate)
        {
            Send(tile, Sel("setContentView:"u8), 0);
            Send(tile, Sel("display"u8));
            s_indicator = 0;
            return;
        }
        if (s_indicator == 0) s_indicator = CreateTileView(tile);
        SendBool(s_indicator, Sel("setIndeterminate:"u8), indeterminate);
        if (indeterminate) Send(s_indicator, Sel("startAnimation:"u8), 0);
        else SendDouble(s_indicator, Sel("setDoubleValue:"u8), Math.Clamp(value ?? 0, 0, 1));
        Send(tile, Sel("display"u8));
    }

    /// <summary>The Dock tile's progress: null without a bar, else its value and whether it cycles.</summary>
    public static (double Value, bool Indeterminate)? Progress()
    {
        // Read from the tile itself, not from what was last set.
        var view = Send(Send(App(), Sel("dockTile"u8)), Sel("contentView"u8));
        if (view == 0) return null;
        var subviews = Send(view, Sel("subviews"u8));
        var indicatorClass = Class("NSProgressIndicator"u8);
        for (nint i = 0, n = Send(subviews, Sel("count"u8)); i < n; i++)
        {
            var subview = Send(subviews, Sel("objectAtIndex:"u8), i);
            if (Send(subview, Sel("isKindOfClass:"u8), indicatorClass) == 0) continue;
            return (ReadDouble(subview, Sel("doubleValue"u8)), (byte)Send(subview, Sel("isIndeterminate"u8)) != 0);
        }
        return null;
    }

    /// <summary>Bounces the Dock icon once, if the application is not active; returns the request to cancel, or 0.</summary>
    public static nint RequestAttention() => SendNInt(App(), Sel("requestUserAttention:"u8), InformationalRequest);

    /// <summary>Stops a <see cref="RequestAttention"/> bounce.</summary>
    public static void CancelAttention(nint request)
    {
        if (request != 0) SendNIntVoid(App(), Sel("cancelUserAttentionRequest:"u8), request);
    }

    // The tile shows its content view instead of the icon, so the view draws the icon with the bar along its bottom.
    private static nint CreateTileView(nint tile)
    {
        var size = ((delegate* unmanaged<nint, nint, NSSize>)s_msgSend)(tile, Sel("size"u8));
        var frame = new NSRect { Width = size.Width, Height = size.Height };
        var view = SendRect(Send(Class("NSImageView"u8), Sel("alloc"u8)), Sel("initWithFrame:"u8), frame);
        Send(view, Sel("setImage:"u8), Send(App(), Sel("applicationIconImage"u8)));

        var barFrame = new NSRect { X = size.Width * 0.1, Y = size.Height * 0.05, Width = size.Width * 0.8, Height = size.Height * 0.15 };
        var indicator = SendRect(Send(Class("NSProgressIndicator"u8), Sel("alloc"u8)), Sel("initWithFrame:"u8), barFrame);
        SendNUInt(indicator, Sel("setStyle:"u8), BarStyle);
        SendDouble(indicator, Sel("setMinValue:"u8), 0);
        SendDouble(indicator, Sel("setMaxValue:"u8), 1);
        SendBool(indicator, Sel("setDisplayedWhenStopped:"u8), true);
        Send(view, Sel("addSubview:"u8), indicator);
        Send(tile, Sel("setContentView:"u8), view);
        // The view holds the indicator and the tile holds the view.
        Send(indicator, Sel("release"u8));
        Send(view, Sel("release"u8));
        return indicator;
    }

    private static nint App() => Send(Class("NSApplication"u8), Sel("sharedApplication"u8));

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static nint SendRect(nint receiver, nint selector, NSRect rect) => ((delegate* unmanaged<nint, nint, NSRect, nint>)s_msgSend)(receiver, selector, rect);

    private static void SendBool(nint receiver, nint selector, bool value) => ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(receiver, selector, value ? (byte)1 : (byte)0);

    private static void SendDouble(nint receiver, nint selector, double value) => ((delegate* unmanaged<nint, nint, double, void>)s_msgSend)(receiver, selector, value);

    private static void SendNUInt(nint receiver, nint selector, nuint value) => ((delegate* unmanaged<nint, nint, nuint, void>)s_msgSend)(receiver, selector, value);

    private static nint SendNInt(nint receiver, nint selector, nint value) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, value);

    private static void SendNIntVoid(nint receiver, nint selector, nint value) => ((delegate* unmanaged<nint, nint, nint, void>)s_msgSend)(receiver, selector, value);

    private static double ReadDouble(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, double>)s_msgSend)(receiver, selector);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
}
