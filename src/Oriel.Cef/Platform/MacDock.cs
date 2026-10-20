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

    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_msgSendStret = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? 0 : NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend_stret");

    /// <summary>Electron's macOS <c>SetProgressBar</c>: the tile's icon with Electron's bar along its bottom, made the first
    /// time or when something else replaced the tile's view; hidden when <paramref name="hidden"/>, else full and
    /// indeterminate, else at <paramref name="value"/>.</summary>
    public static void SetProgress(bool hidden, bool indeterminate, double value)
    {
        var tile = Send(App(), Sel("dockTile"u8));
        var indicator = TileIndicator(tile);
        if (indicator == 0) indicator = CreateTileView(tile);
        if (hidden) SendBool(indicator, Sel("setHidden:"u8), true);
        else
        {
            SendBool(indicator, Sel("setHidden:"u8), false);
            // Never set back, as in Electron: once above 1, the bar stays indeterminate.
            if (indeterminate) SendBool(indicator, Sel("setIndeterminate:"u8), true);
            SendDouble(indicator, Sel("setDoubleValue:"u8), value);
        }
        Send(tile, Sel("display"u8));
    }

    /// <summary>The Dock tile's progress: null without a shown bar, else its value and whether it is indeterminate.</summary>
    public static (double Value, bool Indeterminate)? Progress()
    {
        // Read from the tile itself, not from what was last set.
        var indicator = TileIndicator(Send(App(), Sel("dockTile"u8)));
        if (indicator == 0 || (byte)Send(indicator, Sel("isHidden"u8)) != 0) return null;
        return (ReadDouble(indicator, Sel("doubleValue"u8)), (byte)Send(indicator, Sel("isIndeterminate"u8)) != 0);
    }

    // Electron's first_time test: the bar is the last subview of the tile's view; macOS sometimes installs a view of its own.
    private static nint TileIndicator(nint tile)
    {
        var view = Send(tile, Sel("contentView"u8));
        if (view == 0) return 0;
        var last = Send(Send(view, Sel("subviews"u8)), Sel("lastObject"u8));
        return last != 0 && (byte)Send(last, Sel("isKindOfClass:"u8), Class("NSProgressIndicator"u8)) != 0 ? last : 0;
    }

    /// <summary>Bounces the Dock icon once, if the application is not active; returns the request to cancel, or 0.</summary>
    public static nint RequestAttention() => SendNInt(App(), Sel("requestUserAttention:"u8), InformationalRequest);

    /// <summary>Stops a <see cref="RequestAttention"/> bounce.</summary>
    public static void CancelAttention(nint request)
    {
        if (request != 0) SendNIntVoid(App(), Sel("cancelUserAttentionRequest:"u8), request);
    }

    // The tile shows its content view instead of the icon, so the view draws the icon, and the bar runs along its bottom.
    private static nint CreateTileView(nint tile)
    {
        var view = Send(Send(Class("NSImageView"u8), Sel("alloc"u8)), Sel("init"u8));
        Send(view, Sel("setImage:"u8), Send(App(), Sel("applicationIconImage"u8)));
        Send(tile, Sel("setContentView:"u8), view);
        var size = ((delegate* unmanaged<nint, nint, NSSize>)s_msgSend)(tile, Sel("size"u8));
        var indicator = SendRect(Send(ProgressBarClass(), Sel("alloc"u8)), Sel("initWithFrame:"u8), new NSRect { Width = size.Width, Height = 15 });
        SendNUInt(indicator, Sel("setStyle:"u8), BarStyle);
        SendBool(indicator, Sel("setIndeterminate:"u8), false);
        SendBool(indicator, Sel("setBezeled:"u8), true);
        SendDouble(indicator, Sel("setMinValue:"u8), 0);
        SendDouble(indicator, Sel("setMaxValue:"u8), 1);
        SendBool(indicator, Sel("setHidden:"u8), false);
        Send(view, Sel("addSubview:"u8), indicator);
        // The view holds the indicator and the tile holds the view.
        Send(indicator, Sel("release"u8));
        Send(view, Sel("release"u8));
        return indicator;
    }

    // Electron's ElectronProgressBar: an NSProgressIndicator that draws its own rounded bar.
    private static nint ProgressBarClass()
    {
        fixed (byte* name = "OrielProgressBar"u8)
        {
            var cls = objc_getClass(name);
            if (cls != 0) return cls;
            cls = objc_allocateClassPair(Class("NSProgressIndicator"u8), name, 0);
            fixed (byte* types = "v@:{CGRect={CGPoint=dd}{CGSize=dd}}"u8)
                class_addMethod(cls, Sel("drawRect:"u8), (delegate* unmanaged<nint, nint, NSRect, void>)&DrawBar, types);
            objc_registerClassPair(cls);
            return cls;
        }
    }

    // A grey rounded outline, then the rounded inside filled blue up to the value; an indeterminate bar is at 1.
    [UnmanagedCallersOnly]
    private static void DrawBar(nint self, nint cmd, NSRect dirty)
    {
        try
        {
            if ((nuint)Send(self, Sel("style"u8)) != BarStyle) return;
            var bounds = GetRect(self, Sel("bounds"u8));
            var geometry = MacProgressBarGeometry.Of(bounds.Width, bounds.Height, ReadDouble(self, Sel("doubleValue"u8)), ReadDouble(self, Sel("maxValue"u8)));
            var outline = RoundedPath(geometry.Outline);
            SendDouble(outline, Sel("setLineWidth:"u8), 2);
            Send(Send(Class("NSColor"u8), Sel("grayColor"u8)), Sel("set"u8));
            Send(outline, Sel("stroke"u8));
            var inside = RoundedPath(geometry.Inside);
            SendDouble(inside, Sel("setLineWidth:"u8), 1);
            Send(inside, Sel("addClip"u8));
            var blue = ((delegate* unmanaged<nint, nint, double, double, double, double, nint>)s_msgSend)(Class("NSColor"u8),
                Sel("colorWithSRGBRed:green:blue:alpha:"u8), 0.2, 0.6, 1, 1);
            Send(blue, Sel("set"u8));
            var fill = geometry.Fill;
            NSRectFill(new NSRect { X = fill.X, Y = fill.Y, Width = fill.Width, Height = fill.Height });
        }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception drawing the Dock progress bar: {e}"); }
    }

    private static nint RoundedPath(MacProgressBarGeometry.Rect rect)
        => ((delegate* unmanaged<nint, nint, NSRect, double, double, nint>)s_msgSend)(Class("NSBezierPath"u8), Sel("bezierPathWithRoundedRect:xRadius:yRadius:"u8),
            new NSRect { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height }, rect.Height / 2, rect.Height / 2);

    // NSRect is four doubles: arm64 returns it in registers, x64 through a hidden pointer (_stret).
    private static NSRect GetRect(nint obj, nint selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return ((delegate* unmanaged<nint, nint, NSRect>)s_msgSend)(obj, selector);
        NSRect rect;
        ((delegate* unmanaged<NSRect*, nint, nint, void>)s_msgSendStret)(&rect, obj, selector);
        return rect;
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
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport(AppKit)] private static extern void NSRectFill(NSRect rect);
}
