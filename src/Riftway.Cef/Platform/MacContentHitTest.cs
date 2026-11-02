using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>Electron's <c>ContentViewTargeterDelegate</c> on macOS: a click on a window's content view where none of
/// its children is goes to the page below it.</summary>
/// <remarks>Views on macOS picks the NSView for a mouse event in <c>BridgedContentView -hitTest:</c>, which asks Views
/// what is under the point. A full-window content view above the page answers for every point, so the click goes to
/// Views' root view instead of the page's NSView. CEF exposes no event targeter, so the method is wrapped instead: when
/// Chromium picks the root view and the window says the point is not covered, AppKit's own <c>NSView</c> hit test runs,
/// which finds the page's view under the point as Chromium does for a native subview.</remarks>
[SupportedOSPlatform("macos")]
public static unsafe class MacContentHitTest
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    // Keyed by NSWindow. Main thread only, which on macOS is CEF's UI thread.
    private static readonly Dictionary<nint, Func<int, int, bool>> s_windows = [];
    private static delegate* unmanaged<nint, nint, NSPoint, nint> s_original;
    private static delegate* unmanaged<nint, nint, NSPoint, nint> s_plainHitTest;
    private static bool s_installed;

    /// <summary>Whether the hit test is wrapped: false if Chromium's view class is not there to wrap.</summary>
    public static bool IsInstalled => s_installed;

    /// <summary>UI thread: <paramref name="passesThrough"/> is asked, with a point in the window's content area (top-left
    /// origin, points), whether a click there may go past Views to the native view below.</summary>
    public static void Register(nint nsWindow, Func<int, int, bool> passesThrough)
    {
        if (nsWindow == 0) return;
        Install();
        s_windows[nsWindow] = passesThrough;
    }

    /// <summary>UI thread: forgets a window.</summary>
    public static void Unregister(nint nsWindow) => s_windows.Remove(nsWindow);

    private static void Install()
    {
        if (s_installed) return;
        var selector = Sel("hitTest:");
        var bridged = Class("BridgedContentView");
        var view = Class("NSView");
        if (bridged == 0 || view == 0) return;
        var method = class_getInstanceMethod(bridged, selector);
        var plain = class_getInstanceMethod(view, selector);
        // Only a hitTest: of BridgedContentView's own: wrapping an inherited one would change every NSView.
        if (method == 0 || plain == 0 || method == plain) return;
        s_plainHitTest = (delegate* unmanaged<nint, nint, NSPoint, nint>)method_getImplementation(plain);
        s_original = (delegate* unmanaged<nint, nint, NSPoint, nint>)method_setImplementation(method, (nint)(delegate* unmanaged<nint, nint, NSPoint, nint>)&HitTest);
        s_installed = true;
    }

    [UnmanagedCallersOnly]
    private static nint HitTest(nint self, nint cmd, NSPoint point)
    {
        var hit = s_original(self, cmd, point);
        // Chromium answers self for its root view, nil for a draggable caption, and the NSView under the point otherwise.
        if (hit != self) return hit;
        try
        {
            if (!s_windows.TryGetValue(Send(self, Sel("window")), out var passesThrough)) return hit;
            // As Chromium: the point is in the superview's coordinates, Views' are top-down from the content view's top.
            var local = ((delegate* unmanaged<nint, nint, NSPoint, nint, NSPoint>)s_send)(self, Sel("convertPoint:fromView:"), point, Send(self, Sel("superview")));
            var x = (int)Math.Floor(point.X);
            var y = (int)Math.Floor(Frame(self).Height - local.Y);
            return passesThrough(x, y) ? s_plainHitTest(self, cmd, point) : hit;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Riftway] Unhandled exception in the content view hit test: {e}");
            return hit;
        }
    }

    // NSRect is four doubles: arm64 returns it in registers, x64 through a hidden pointer (_stret).
    private static NSRect Frame(nint obj)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return SendRect(obj, Sel("frame"));
        NSRect rect;
        SendRectStret(&rect, obj, Sel("frame"));
        return rect;
    }

    private static readonly nint s_send = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    private static readonly Dictionary<string, nint> s_selectors = [];

    private static nint Sel(string name)
    {
        if (s_selectors.TryGetValue(name, out var selector)) return selector;
        fixed (byte* p = Utf8(name)) selector = sel_registerName(p);
        return s_selectors[name] = selector;
    }

    private static nint Class(string name)
    {
        fixed (byte* p = Utf8(name)) return objc_getClass(p);
    }

    private static byte[] Utf8(string value) => System.Text.Encoding.UTF8.GetBytes(value + '\0');

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint class_getInstanceMethod(nint cls, nint selector);
    [DllImport(ObjC)] private static extern nint method_getImplementation(nint method);
    [DllImport(ObjC)] private static extern nint method_setImplementation(nint method, nint imp);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRect(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void SendRectStret(NSRect* result, nint receiver, nint selector);
}
