using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>Window-manager hints for a top-level X11 window (CEF's <c>get_window_handle</c> on Linux).</summary>
/// <remarks>Uses Oriel's own connection to the X server; the window manager applies the requests asynchronously.</remarks>
[SupportedOSPlatform("linux")]
public static unsafe partial class X11Window
{
    private const string LibX11 = "libX11.so.6";
    private const long UrgencyHint = 1 << 8;
    private const int ClientMessage = 33;
    private const long SubstructureNotifyMask = 1 << 19;
    private const long SubstructureRedirectMask = 1 << 20;
    private const long NetWmStateRemove = 0;
    private const long NetWmStateAdd = 1;

    private static nint s_display;

    // Xlib's XWMHints on 64-bit platforms.
    [StructLayout(LayoutKind.Sequential)]
    private struct XWMHints
    {
        public long Flags;
        public int Input;
        public int InitialState;
        public nint IconPixmap;
        public nint IconWindow;
        public int IconX;
        public int IconY;
        public nint IconMask;
        public nint WindowGroup;
    }

    /// <summary>Sets or clears the urgency hint, which window managers show by flashing or highlighting the window's
    /// taskbar entry (Electron's <c>flashFrame</c> on Linux). False without an X display.</summary>
    public static bool SetUrgency(nint window, bool urgent)
    {
        var display = Display();
        if (display == 0 || window == 0) return false;
        var hints = XGetWMHints(display, window);
        var owned = hints == null;
        if (owned) hints = XAllocWMHints();
        hints->Flags = urgent ? hints->Flags | UrgencyHint : hints->Flags & ~UrgencyHint;
        _ = XSetWMHints(display, window, hints);
        _ = XFree(hints);
        _ = XFlush(display);
        return true;
    }

    /// <summary>Asks the window manager to leave the window off the taskbar (<c>_NET_WM_STATE_SKIP_TASKBAR</c>), or to
    /// put it back. The window must be mapped. False without an X display.</summary>
    public static bool SetSkipTaskbar(nint window, bool skip)
    {
        var display = Display();
        if (display == 0 || window == 0) return false;
        // An XClientMessageEvent inside XEvent's 192 bytes, at its 64-bit offsets.
        var e = stackalloc long[24];
        new Span<long>(e, 24).Clear();
        *(int*)e = ClientMessage;
        e[2] = 1;
        e[3] = display;
        e[4] = window;
        e[5] = Atom(display, "_NET_WM_STATE"u8);
        *(int*)&e[6] = 32;
        e[7] = skip ? NetWmStateAdd : NetWmStateRemove;
        e[8] = Atom(display, "_NET_WM_STATE_SKIP_TASKBAR"u8);
        e[9] = 0;
        // Source indication 1: a normal application.
        e[10] = 1;
        var sent = XSendEvent(display, XDefaultRootWindow(display), 0, SubstructureRedirectMask | SubstructureNotifyMask, e) != 0;
        _ = XFlush(display);
        return sent;
    }

    private static nint Display()
    {
        if (s_display == 0) s_display = XOpenDisplay(null);
        return s_display;
    }

    private static nint Atom(nint display, ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return XInternAtom(display, p, 0);
    }

    [LibraryImport(LibX11)] private static partial nint XOpenDisplay(byte* name);
    [LibraryImport(LibX11)] private static partial nint XDefaultRootWindow(nint display);
    [LibraryImport(LibX11)] private static partial nint XInternAtom(nint display, byte* name, int onlyIfExists);
    [LibraryImport(LibX11)] private static partial XWMHints* XGetWMHints(nint display, nint window);
    [LibraryImport(LibX11)] private static partial XWMHints* XAllocWMHints();
    [LibraryImport(LibX11)] private static partial int XSetWMHints(nint display, nint window, XWMHints* hints);
    [LibraryImport(LibX11)] private static partial int XFree(void* data);
    [LibraryImport(LibX11)] private static partial int XFlush(nint display);
    [LibraryImport(LibX11)] private static partial int XSendEvent(nint display, nint window, int propagate, long eventMask, long* e);
}
