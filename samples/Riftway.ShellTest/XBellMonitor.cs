using System.Runtime.InteropServices;

/// <summary>Counts XKB bell notifications on the X display, which <c>gdk_display_beep</c> rings through <c>XkbBell</c>.</summary>
sealed unsafe partial class XBellMonitor : IDisposable
{
    private const string LibX11 = "libX11.so.6";
    private readonly nint _display;
    private readonly int _eventBase;

    private XBellMonitor(nint display, int eventBase)
    {
        _display = display;
        _eventBase = eventBase;
    }

    /// <summary>Selects bell notifications on its own connection; null without a display or XKB.</summary>
    public static XBellMonitor? Start()
    {
        var display = XOpenDisplay(null);
        if (display == 0) return null;
        int opcode, eventBase, errorBase, major = 1, minor = 0;
        if (XkbQueryExtension(display, &opcode, &eventBase, &errorBase, &major, &minor) == 0)
        {
            _ = XCloseDisplay(display);
            return null;
        }
        // XkbUseCoreKbd, XkbBellNotifyMask.
        _ = XkbSelectEvents(display, 0x100, 1 << 8, 1 << 8);
        _ = XSync(display, 0);
        return new XBellMonitor(display, eventBase);
    }

    /// <summary>The bell notifications that arrived since the last call.</summary>
    public int Drain()
    {
        _ = XSync(_display, 0);
        var count = 0;
        var e = stackalloc byte[192];
        while (XPending(_display) > 0)
        {
            _ = XNextEvent(_display, e);
            // XkbAnyEvent: type at 0, xkb_type after serial, send_event, display and time.
            if (*(int*)e == _eventBase && *(int*)(e + 40) == 8 /* XkbBellNotify */) count++;
        }
        return count;
    }

    public void Dispose() => _ = XCloseDisplay(_display);

    [LibraryImport(LibX11)] private static partial nint XOpenDisplay(byte* name);
    [LibraryImport(LibX11)] private static partial int XCloseDisplay(nint display);
    [LibraryImport(LibX11)] private static partial int XkbQueryExtension(nint display, int* opcode, int* eventBase, int* errorBase, int* major, int* minor);
    [LibraryImport(LibX11)] private static partial int XkbSelectEvents(nint display, uint device, nuint affect, nuint values);
    [LibraryImport(LibX11)] private static partial int XSync(nint display, int discard);
    [LibraryImport(LibX11)] private static partial int XPending(nint display);
    [LibraryImport(LibX11)] private static partial int XNextEvent(nint display, byte* e);
}
