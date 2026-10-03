using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>macOS "simple" full screen: the window fills its display without moving to a new Space.</summary>
/// <remarks>Native full screen gives the window its own Space, which blacks out the other displays;
/// this is Electron's <c>setSimpleFullScreen</c> instead (pre-Lion style).</remarks>
[SupportedOSPlatform("macos")]
public static unsafe class MacWindow
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    private const nuint TitledStyle = 1 << 0;
    // NSApplicationPresentationHideDock | NSApplicationPresentationHideMenuBar.
    private const nuint HideDockAndMenuBar = (1 << 1) | (1 << 3);

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    private sealed record Saved(NSRect Frame, nuint StyleMask, nuint PresentationOptions);

    private static readonly Dictionary<nint, Saved> s_saved = [];

    /// <summary>Whether the window behind <paramref name="contentView"/> is in simple full screen.</summary>
    public static bool IsSimpleFullScreen(nint contentView) => s_saved.ContainsKey(NSWindowOf(contentView));

    /// <summary>Enters or leaves simple full screen for the window behind <paramref name="contentView"/>
    /// (CEF's <c>get_window_handle</c> on macOS). Call on the main thread.</summary>
    public static void SetSimpleFullScreen(nint contentView, bool flag)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        var app = Send(Class("NSApplication"u8), Sel("sharedApplication"u8));

        if (flag && !s_saved.ContainsKey(window))
        {
            var saved = new Saved(Frame(window), (nuint)Send(window, Sel("styleMask"u8)), (nuint)Send(app, Sel("presentationOptions"u8)));
            s_saved[window] = saved;

            SendNUInt(window, Sel("setStyleMask:"u8), saved.StyleMask & ~TitledStyle);
            SendNUInt(app, Sel("setPresentationOptions:"u8), HideDockAndMenuBar);
            SetFrame(window, Frame(Send(window, Sel("screen"u8))));
        }
        else if (!flag && s_saved.Remove(window, out var saved))
        {
            SendNUInt(app, Sel("setPresentationOptions:"u8), saved.PresentationOptions);
            SendNUInt(window, Sel("setStyleMask:"u8), saved.StyleMask);
            SetFrame(window, saved.Frame);
        }
    }

    private static nint NSWindowOf(nint contentView) => contentView == 0 ? 0 : Send(contentView, Sel("window"u8));

    // NSRect is four doubles: arm64 returns it in registers, x64 through a hidden pointer (_stret).
    private static NSRect Frame(nint obj)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return SendRect(obj, Sel("frame"u8));
        NSRect rect;
        SendRectStret(&rect, obj, Sel("frame"u8));
        return rect;
    }

    private static void SetFrame(nint window, NSRect frame) => SendSetFrame(window, Sel("setFrame:display:"u8), frame, 1);

    // u8 literals are null-terminated in memory, as the runtime expects.
    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendNUInt(nint receiver, nint selector, nuint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRect(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void SendRectStret(NSRect* result, nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSetFrame(nint receiver, nint selector, NSRect frame, byte display);
}
