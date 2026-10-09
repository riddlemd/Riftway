using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The X events that make Chromium re-read the displays (<c>XDisplayManager</c>, ui/base/x/x11_display_manager.cc),
/// heard on an Xlib connection of Oriel's own that only its own thread uses.</summary>
[SupportedOSPlatform("linux")]
public static unsafe partial class X11Displays
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXrandr = "libXrandr.so.2";
    private const long PropertyChangeMask = 1 << 22;
    // RRScreenChangeNotifyMask | RRCrtcChangeNotifyMask | RROutputChangeNotifyMask, as XDisplayManager::Init selects.
    private const int RandRMask = 1 | 2 | 4;

    /// <summary>Selects RandR and root property events as <c>XDisplayManager::Init</c> and its workspace handler do,
    /// then calls <paramref name="changed"/> on a background thread for each event <see cref="X11DisplayRules.UpdatesDisplays"/>
    /// accepts. False, watching nothing, without an X display, libXrandr or RandR 1.3.</summary>
    public static bool Start(Action changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        nint display;
        try { display = XOpenDisplay(null); }
        catch (DllNotFoundException) { return false; }
        if (display == 0) return false;
        try
        {
            int eventBase, errorBase, major, minor;
            if (XRRQueryExtension(display, &eventBase, &errorBase) == 0 || XRRQueryVersion(display, &major, &minor) == 0 || !X11DisplayRules.RandRUsable(major, minor))
            {
                _ = XCloseDisplay(display);
                return false;
            }
            var root = XDefaultRootWindow(display);
            XRRSelectInput(display, root, RandRMask);
            _ = XSelectInput(display, root, PropertyChangeMask);
            nint workArea;
            fixed (byte* name = "_NET_WORKAREA"u8) workArea = XInternAtom(display, name, 0);
            _ = XFlush(display);
            var randrBase = eventBase;
            new Thread(() => Events(display, randrBase, workArea, changed)) { IsBackground = true, Name = "Oriel X11 displays" }.Start();
            return true;
        }
        catch (DllNotFoundException)
        {
            _ = XCloseDisplay(display);
            return false;
        }
    }

    private static void Events(nint display, int eventBase, nint workArea, Action changed)
    {
        // XEvent is 192 bytes; XPropertyEvent's atom sits at offset 40 on 64-bit platforms.
        var e = stackalloc long[24];
        while (true)
        {
            _ = XNextEvent(display, e);
            try
            {
                if (X11DisplayRules.UpdatesDisplays(*(int*)e, (nint)e[5], eventBase, workArea)) changed();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Console.Error.WriteLine($"Oriel X11 displays: {ex}"); }
        }
    }

    [LibraryImport(LibX11)] private static partial nint XOpenDisplay(byte* name);
    [LibraryImport(LibX11)] private static partial int XCloseDisplay(nint display);
    [LibraryImport(LibX11)] private static partial nint XDefaultRootWindow(nint display);
    [LibraryImport(LibX11)] private static partial nint XInternAtom(nint display, byte* name, int onlyIfExists);
    [LibraryImport(LibX11)] private static partial int XSelectInput(nint display, nint window, long mask);
    [LibraryImport(LibX11)] private static partial int XFlush(nint display);
    [LibraryImport(LibX11)] private static partial int XNextEvent(nint display, long* e);
    [LibraryImport(LibXrandr)] private static partial int XRRQueryExtension(nint display, int* eventBase, int* errorBase);
    [LibraryImport(LibXrandr)] private static partial int XRRQueryVersion(nint display, int* major, int* minor);
    [LibraryImport(LibXrandr)] private static partial void XRRSelectInput(nint display, nint window, int mask);
}

/// <summary>Which X events and RandR versions <c>XDisplayManager</c> acts on.</summary>
public static class X11DisplayRules
{
    private const int PropertyNotify = 28;
    // RRNotify, the event x11::RandR::NotifyEvent reads: CRTC, output and output property changes.
    private const int RandRNotify = 1;

    /// <summary>XDisplayManager's <c>kMinXrandrVersion</c> test: RandR 1.3 or later.</summary>
    public static bool RandRUsable(int major, int minor) => major > 1 || (major == 1 && minor >= 3);

    /// <summary><c>XDisplayManager::OnEvent</c>: an <c>RRNotify</c>, or a change to <c>_NET_WORKAREA</c>. A bare
    /// <c>RRScreenChangeNotify</c> does not count.</summary>
    public static bool UpdatesDisplays(int type, nint atom, int randrEventBase, nint workAreaAtom)
        => type == randrEventBase + RandRNotify || (type == PropertyNotify && atom == workAreaAtom);
}
