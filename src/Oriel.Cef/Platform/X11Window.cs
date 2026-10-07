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

    /// <summary>Asks GTK window managers for the dark or light theme variant of the window's frame
    /// (<c>_GTK_THEME_VARIANT</c>, a UTF8_STRING), as Electron's darkTheme does. False without an X display.</summary>
    public static bool SetGtkThemeVariant(nint window, bool dark)
    {
        var display = Display();
        if (display == 0 || window == 0) return false;
        var value = dark ? "dark"u8 : "light"u8;
        // Format 8, PropModeReplace.
        fixed (byte* p = value) _ = XChangeProperty(display, window, Atom(display, "_GTK_THEME_VARIANT"u8), Atom(display, "UTF8_STRING"u8), 8, 0, p, value.Length);
        _ = XFlush(display);
        return true;
    }

    /// <summary>Puts the window on every workspace, or back on the current one, as Chromium's X11Window does: the sticky
    /// state, then <c>_NET_WM_DESKTOP</c>. False without an X display.</summary>
    public static bool SetVisibleOnAllWorkspaces(nint window, bool visible)
    {
        var display = Display();
        if (display == 0 || window == 0) return false;
        var sticky = Atom(display, "_NET_WM_STATE_STICKY"u8);
        Send(display, window, Atom(display, "_NET_WM_STATE"u8), visible ? NetWmStateAdd : NetWmStateRemove, sticky, 0, 1);
        long desktop;
        if (visible) desktop = AllWorkspaces;
        else if (ReadCardinal(display, XDefaultRootWindow(display), Atom(display, "_NET_CURRENT_DESKTOP"u8)) is { } current) desktop = current;
        else return false;
        Send(display, window, Atom(display, "_NET_WM_DESKTOP"u8), desktop, 1, 0, 0);
        _ = XFlush(display);
        return true;
    }

    /// <summary>Whether the window is on every workspace: its <c>_NET_WM_DESKTOP</c> is 0xFFFFFFFF.</summary>
    public static bool IsVisibleOnAllWorkspaces(nint window)
    {
        var display = Display();
        return display != 0 && window != 0 && ReadCardinal(display, window, Atom(display, "_NET_WM_DESKTOP"u8)) == AllWorkspaces;
    }

    private const long AllWorkspaces = 0xFFFFFFFF;

    // Xlib's XSetWindowAttributes on 64-bit platforms.
    [StructLayout(LayoutKind.Sequential)]
    private struct XSetWindowAttributes
    {
        public nint BackgroundPixmap;
        public nuint BackgroundPixel;
        public nint BorderPixmap;
        public nuint BorderPixel;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public nuint BackingPlanes;
        public nuint BackingPixel;
        public int SaveUnder;
        public long EventMask;
        public long DoNotPropagateMask;
        public int OverrideRedirect;
        public nint Colormap;
        public nint Cursor;
    }

    // XIEventMask on 64-bit platforms.
    [StructLayout(LayoutKind.Sequential)]
    private struct XIEventMask
    {
        public int DeviceId;
        public int MaskLength;
        public byte* Mask;
    }

    private const int InputOnly = 2;
    private const nuint CWDontPropagate = 1 << 12;
    // Button and every motion mask: pointer events have a core type, so the server stops them at a window whose
    // do-not-propagate mask holds them, XI2 deliveries included (dix EventIsDeliverable).
    private const long BlockedEvents = 0x4 | 0x8 | 0x40 | 0x100 | 0x200 | 0x400 | 0x800 | 0x1000 | 0x2000;
    private const int XIAllMasterDevices = 1;
    private const uint CoverSize = 32767;

    /// <summary>Covers the window with an input-only child that keeps pointer, wheel and touch events from the
    /// window (not keys, which go to the focus window): the X11 stand-in for Chromium's <c>DisableEventListening</c>. Returns the
    /// child for <see cref="UnblockInput"/>, or 0 without an X display.</summary>
    public static nint BlockInput(nint window)
    {
        var display = Display();
        if (display == 0 || window == 0) return 0;
        var attributes = new XSetWindowAttributes { DoNotPropagateMask = BlockedEvents };
        // Child windows clip to their parent, so one larger than any screen needs no resizing with the window.
        var cover = XCreateWindow(display, window, 0, 0, CoverSize, CoverSize, 0, 0, InputOnly, 0, CWDontPropagate, &attributes);
        if (cover == 0) return 0;
        // Touch has no core type, so it is stopped by taking it: the first window selecting a touch gets it.
        var mask = stackalloc byte[3];
        mask[0] = 0;
        mask[1] = 0;
        mask[2] = 1 << (18 - 16) | 1 << (19 - 16) | 1 << (20 - 16);
        var touch = new XIEventMask { DeviceId = XIAllMasterDevices, MaskLength = 3, Mask = mask };
        try { _ = XISelectEvents(display, cover, &touch, 1); }
        catch (DllNotFoundException) { }
        _ = XMapRaised(display, cover);
        _ = XFlush(display);
        return cover;
    }

    /// <summary>Removes a <see cref="BlockInput"/> cover. The window must still exist.</summary>
    public static void UnblockInput(nint cover)
    {
        var display = Display();
        if (display == 0 || cover == 0) return;
        _ = XDestroyWindow(display, cover);
        // Drops the touch events the cover took; nothing else on this connection selects events.
        _ = XSync(display, 1);
    }

    // An XClientMessageEvent to the root window, inside XEvent's 192 bytes at its 64-bit offsets.
    private static void Send(nint display, nint window, nint type, long l0, long l1, long l2, long l3)
    {
        var e = stackalloc long[24];
        new Span<long>(e, 24).Clear();
        *(int*)e = ClientMessage;
        e[2] = 1;
        e[3] = display;
        e[4] = window;
        e[5] = type;
        *(int*)&e[6] = 32;
        e[7] = l0;
        e[8] = l1;
        e[9] = l2;
        e[10] = l3;
        _ = XSendEvent(display, XDefaultRootWindow(display), 0, SubstructureRedirectMask | SubstructureNotifyMask, e);
    }

    private static long? ReadCardinal(nint display, nint window, nint property)
    {
        // XA_CARDINAL is 6.
        if (XGetWindowProperty(display, window, property, 0, 1, 0, 6, out _, out var format, out var count, out _, out var data) != 0 || data is null)
            return null;
        long? value = format == 32 && count > 0 ? (long)(uint)*(nuint*)data : null;
        _ = XFree(data);
        return value;
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
    [LibraryImport(LibX11)] private static partial int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, byte* data, int count);
    [LibraryImport(LibX11)] private static partial int XSendEvent(nint display, nint window, int propagate, long eventMask, long* e);
    [LibraryImport(LibX11)] private static partial nint XCreateWindow(nint display, nint parent, int x, int y, uint width, uint height, uint borderWidth,
        int depth, uint windowClass, nint visual, nuint valueMask, XSetWindowAttributes* attributes);
    [LibraryImport(LibX11)] private static partial int XMapRaised(nint display, nint window);
    [LibraryImport(LibX11)] private static partial int XDestroyWindow(nint display, nint window);
    [LibraryImport(LibX11)] private static partial int XSync(nint display, int discard);
    [LibraryImport("libXi.so.6")] private static partial int XISelectEvents(nint display, nint window, XIEventMask* masks, int count);
    [LibraryImport(LibX11)] private static partial int XGetWindowProperty(nint display, nint window, nint property, long offset, long length, int delete,
        nint requestedType, out nint actualType, out int actualFormat, out nuint itemCount, out nuint bytesAfter, out byte* data);
}
