using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

// webrtc's X11 desktop capturers (screen_capturer_x11.cc, window_capturer_x11.cc, window_list_utils.cc) and Electron's
// monitor-to-display mapping, on a connection of their own so a capture never holds the UI thread's.
public static unsafe partial class X11Window
{
    private const string LibXrandr = "libXrandr.so.2";
    private const string LibXcomposite = "libXcomposite.so.1";
    private const int NormalState = 1, IconicState = 3;
    private const int ZPixmap = 2;

    /// <summary>Opens an X connection for one desktop capture; 0 without an X display. Close it with
    /// <see cref="CloseCaptureConnection"/>.</summary>
    public static nint OpenCaptureConnection() => XOpenDisplay(null);

    /// <summary>Closes a <see cref="OpenCaptureConnection"/> connection.</summary>
    public static void CloseCaptureConnection(nint display)
    {
        if (display != 0) _ = XCloseDisplay(display);
    }

    /// <summary>webrtc's <c>ScreenCapturerX11::GetSourceList</c>: one source per XRandR monitor (XRandR 1.5), its id the
    /// monitor's name atom; without XRandR 1.5, one whole-desktop source with id 0. Each carries Electron's
    /// <c>display_id</c> for it.</summary>
    public static IReadOnlyList<CaptureSource> CaptureScreens(nint display)
    {
        if (!HasRandr15(display)) return [new CaptureSource(0, "")];
        var root = XDefaultRootWindow(display);
        var displayIds = MonitorAtomToDisplayId(display, root);
        var sources = new List<CaptureSource>();
        var monitors = XRRGetMonitors(display, root, 1, out var count);
        if (monitors == null) return sources;
        for (var i = 0; i < count; i++)
        {
            var atom = monitors[i].Name;
            sources.Add(new CaptureSource(atom, "", displayIds.TryGetValue((int)atom, out var id) ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""));
        }
        XRRFreeMonitors(monitors);
        return sources;
    }

    /// <summary>A screen source's area on the root window: the monitor's rectangle, or the whole root for id 0.</summary>
    public static (int X, int Y, int Width, int Height)? CaptureScreenRect(nint display, long id)
    {
        var root = XDefaultRootWindow(display);
        if (id == 0 || !HasRandr15(display))
        {
            XWindowAttributes a;
            return XGetWindowAttributes(display, root, &a) != 0 ? (0, 0, a.Width, a.Height) : null;
        }
        var monitors = XRRGetMonitors(display, root, 1, out var count);
        if (monitors == null) return null;
        try
        {
            for (var i = 0; i < count; i++)
                if (monitors[i].Name == id) return (monitors[i].X, monitors[i].Y, monitors[i].Width, monitors[i].Height);
            return null;
        }
        finally { XRRFreeMonitors(monitors); }
    }

    private static bool HasRandr15(nint display)
    {
        try
        {
            if (XRRQueryExtension(display, out _, out _) == 0 || XRRQueryVersion(display, out var major, out var minor) == 0) return false;
            return major > 1 || (major == 1 && minor >= 5);
        }
        catch (DllNotFoundException) { return false; }
    }

    // Electron's MonitorAtomIdToDisplayId: connected outputs with a CRTC, keyed by the name atom of the monitor holding them.
    private static Dictionary<int, uint> MonitorAtomToDisplayId(nint display, nint root)
    {
        var map = new Dictionary<int, uint>();
        var resources = XRRGetScreenResourcesCurrent(display, root);
        if (resources == null) return map;
        var outputToMonitor = new Dictionary<nint, int>();
        var monitors = XRRGetMonitors(display, root, 0, out var monitorCount);
        if (monitors != null)
            for (var m = 0; m < monitorCount; m++)
                for (var o = 0; o < monitors[m].OutputCount; o++)
                    outputToMonitor[monitors[m].Outputs[o]] = m;
        var edidAtom = Atom(display, "EDID"u8);
        try
        {
            for (var i = 0; i < resources->OutputCount; i++)
            {
                var output = resources->Outputs[i];
                var info = XRRGetOutputInfo(display, resources, output);
                if (info == null) continue;
                var usable = info->Connection == 0 && info->Crtc != 0;
                var crtc = usable ? XRRGetCrtcInfo(display, resources, info->Crtc) : null;
                XRRFreeOutputInfo(info);
                if (crtc == null) continue;
                XRRFreeCrtcInfo(crtc);
                var displayId = DesktopMedia.X11DisplayId(OutputEdid(display, output, edidAtom), (uint)output, i);
                if (outputToMonitor.TryGetValue(output, out var monitor))
                    map[(int)monitors[monitor].Name] = displayId;
            }
        }
        finally
        {
            if (monitors != null) XRRFreeMonitors(monitors);
            XRRFreeScreenResources(resources);
        }
        return map;
    }

    private static byte[] OutputEdid(nint display, nint output, nint edidAtom)
    {
        // Electron asks for 128 32-bit units of any type and keeps 8-bit data.
        if (XRRGetOutputProperty(display, output, edidAtom, 0, 128, 0, 0, 0, out var type, out var format, out var count, out _, out var data) != 0 || data == null)
            return [];
        try { return format == 8 && type != 0 ? new ReadOnlySpan<byte>(data, (int)count).ToArray() : []; }
        finally { _ = XFree(data); }
    }

    /// <summary>webrtc's <c>WindowCapturerX11::GetSourceList</c>: for each screen's root, its children front to back, each
    /// mapped to its client window (the first in <c>NormalState</c> by <c>WM_STATE</c>, minimized ones dropped), desktop
    /// elements left out, and only windows whose <c>WM_NAME</c> converts to text.</summary>
    public static IReadOnlyList<CaptureSource> CaptureWindows(nint display)
    {
        var sources = new List<CaptureSource>();
        using var trap = new ErrorTrap();
        for (var screen = 0; screen < XScreenCount(display); screen++)
        {
            var root = XRootWindow(display, screen);
            if (XQueryTree(display, root, out _, out _, out var children, out var count) == 0 || trap.Failed) continue;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var app = ApplicationWindow(display, children[count - 1 - i]);
                    if (app == 0 || IsDesktopElement(display, app)) continue;
                    if (WindowTitle(display, app) is { } title) sources.Add(new CaptureSource(app, title));
                }
            }
            finally { if (children != null) _ = XFree(children); }
        }
        return sources;
    }

    private static nint ApplicationWindow(nint display, nint window)
    {
        var state = WindowState(display, window);
        if (state == NormalState) return window;
        if (state == IconicState) return 0;
        if (XQueryTree(display, window, out _, out _, out var children, out var count) == 0) return 0;
        try
        {
            for (var i = 0; i < count; i++)
                if (ApplicationWindow(display, children[i]) is var app && app != 0) return app;
            return 0;
        }
        finally { if (children != null) _ = XFree(children); }
    }

    // A missing WM_STATE counts as WithdrawnState (0).
    private static long WindowState(nint display, nint window)
        => ReadLongs(display, window, Atom(display, "WM_STATE"u8)) is { Length: > 0 } state ? state[0] : 0;

    private static bool IsDesktopElement(nint display, nint window)
    {
        var types = ReadLongs(display, window, Atom(display, "_NET_WM_WINDOW_TYPE"u8));
        string? resName = null;
        if (types is not { Length: > 0 })
        {
            XClassHint hint;
            if (XGetClassHint(display, window, &hint) != 0)
            {
                resName = Marshal.PtrToStringUTF8((nint)hint.ResName);
                if (hint.ResName != null) _ = XFree(hint.ResName);
                if (hint.ResClass != null) _ = XFree(hint.ResClass);
            }
        }
        return DesktopMedia.IsX11DesktopElement(types, Atom(display, "_NET_WM_WINDOW_TYPE_NORMAL"u8), resName);
    }

    private static string? WindowTitle(nint display, nint window)
    {
        XTextProperty name;
        if (XGetWMName(display, window, &name) == 0 || name.Value == null) return null;
        try
        {
            if (name.ItemCount == 0) return null;
            byte** list = null;
            var status = Xutf8TextPropertyToTextList(display, &name, &list, out var count);
            try { return status >= 0 && count > 0 && list != null && *list != null ? Marshal.PtrToStringUTF8((nint)(*list)) : null; }
            finally { if (list != null) XFreeStringList(list); }
        }
        finally { _ = XFree(name.Value); }
    }

    // A format-32 property of any type, which Xlib hands back as C longs.
    private static long[]? ReadLongs(nint display, nint window, nint property)
    {
        if (XGetWindowProperty(display, window, property, 0, 0x7fffffff / 4, 0, 0, out _, out var format, out var count, out _, out var data) != 0 || data == null)
            return null;
        try
        {
            if (format != 32) return null;
            var values = new long[(int)count];
            for (var i = 0; i < values.Length; i++) values[i] = ((long*)data)[i];
            return values;
        }
        finally { _ = XFree(data); }
    }

    /// <summary>Whether the server has XComposite 0.2, which webrtc's window capturer requires.</summary>
    public static bool HasComposite(nint display)
    {
        try
        {
            return XCompositeQueryExtension(display, out _, out _) != 0 && XCompositeQueryVersion(display, out var major, out var minor) != 0 && (major > 0 || minor >= 2);
        }
        catch (DllNotFoundException) { return false; }
    }

    /// <summary>webrtc's X11 window capture: the window redirected off-screen with XComposite, then read whole with
    /// <c>XGetImage</c>. Null when it cannot be read.</summary>
    public static CaptureBitmap? CaptureWindowImage(nint display, nint window)
    {
        if (!HasComposite(display)) return null;
        using var trap = new ErrorTrap();
        XWindowAttributes a;
        if (XGetWindowAttributes(display, window, &a) == 0 || trap.Failed || a.Width <= 0 || a.Height <= 0) return null;
        if (WindowState(display, window) == IconicState) return new CaptureBitmap(1, 1, new byte[4]);
        XCompositeRedirectWindow(display, window, 0);
        return GetImage(display, window, 0, 0, a.Width, a.Height, trap);
    }

    /// <summary>webrtc's X11 screen capture: the monitor's rectangle of the root window.</summary>
    public static CaptureBitmap? CaptureScreenImage(nint display, long id)
    {
        if (CaptureScreenRect(display, id) is not { } r) return null;
        using var trap = new ErrorTrap();
        return GetImage(display, XDefaultRootWindow(display), r.X, r.Y, r.Width, r.Height, trap);
    }

    private static CaptureBitmap? GetImage(nint display, nint drawable, int x, int y, int width, int height, ErrorTrap trap)
    {
        var image = XGetImage(display, drawable, x, y, (uint)width, (uint)height, ~0UL, ZPixmap);
        if (image == null || trap.Failed) return null;
        try { return ToBgra(image); }
        finally { image->DestroyImage(image); }
    }

    // webrtc's XServerPixelBuffer: a fast copy for 32-bit 0xRRGGBB pixels in LSB order, else per-pixel through the masks.
    private static CaptureBitmap ToBgra(XImage* image)
    {
        var (w, h) = (image->Width, image->Height);
        var bgra = new byte[w * h * 4];
        var fast = image->BitsPerPixel == 32 && image->RedMask == 0xff0000 && image->GreenMask == 0xff00 && image->BlueMask == 0xff && image->ByteOrder == 0;
        for (var row = 0; row < h; row++)
        {
            var line = image->Data + (long)row * image->BytesPerLine;
            if (fast)
            {
                new ReadOnlySpan<byte>(line, w * 4).CopyTo(bgra.AsSpan(row * w * 4));
                continue;
            }
            var bytes = image->BitsPerPixel / 8;
            for (var col = 0; col < w; col++)
            {
                ulong pixel = 0;
                var p = line + col * bytes;
                for (var b = 0; b < bytes; b++)
                    pixel |= (ulong)p[image->ByteOrder == 0 ? b : bytes - 1 - b] << (8 * b);
                var o = (row * w + col) * 4;
                bgra[o] = Channel(pixel, image->BlueMask);
                bgra[o + 1] = Channel(pixel, image->GreenMask);
                bgra[o + 2] = Channel(pixel, image->RedMask);
            }
        }
        return new CaptureBitmap(w, h, bgra);
    }

    private static byte Channel(ulong pixel, nuint mask)
    {
        if (mask == 0) return 0;
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var max = (ulong)mask >> shift;
        return (byte)(((pixel & mask) >> shift) * 255 / max);
    }

    /// <summary>Chromium's <c>ui::GetNativeWindowIcon</c>: the largest icon in the window's <c>_NET_WM_ICON</c>.</summary>
    public static CaptureBitmap? CaptureWindowIcon(nint display, nint window)
    {
        using var trap = new ErrorTrap();
        var values = ReadLongs(display, window, Atom(display, "_NET_WM_ICON"u8));
        return values is null ? null : DesktopMedia.PickX11Icon([.. values.Select(v => unchecked((uint)v))]);
    }

    // webrtc's XErrorTrap: errors from a window that vanished mid-capture are swallowed, not reported.
    private sealed class ErrorTrap : IDisposable
    {
        [ThreadStatic] private static int t_errors;
        private readonly nint _previous;
        private readonly int _start;

        public ErrorTrap()
        {
            _start = t_errors;
            _previous = XSetErrorHandler((nint)(delegate* unmanaged<nint, nint, int>)&OnError);
        }

        public bool Failed => t_errors != _start;

        public void Dispose() => XSetErrorHandler(_previous);

        [UnmanagedCallersOnly]
        private static int OnError(nint display, nint error)
        {
            t_errors++;
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRRMonitorInfo
    {
        public nint Name;
        public int Primary;
        public int Automatic;
        public int OutputCount;
        public int X, Y, Width, Height, MWidth, MHeight;
        public nint* Outputs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRRScreenResources
    {
        public nint Timestamp;
        public nint ConfigTimestamp;
        public int CrtcCount;
        public nint* Crtcs;
        public int OutputCount;
        public nint* Outputs;
        public int ModeCount;
        public void* Modes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRROutputInfo
    {
        public nint Timestamp;
        public nint Crtc;
        public byte* Name;
        public int NameLength;
        public nuint MmWidth;
        public nuint MmHeight;
        public ushort Connection;
        public ushort SubpixelOrder;
        public int CrtcCount;
        public nint* Crtcs;
        public int CloneCount;
        public nint* Clones;
        public int ModeCount;
        public int PreferredCount;
        public nint* Modes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XClassHint
    {
        public byte* ResName;
        public byte* ResClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XTextProperty
    {
        public byte* Value;
        public nint Encoding;
        public int Format;
        public nuint ItemCount;
    }

    // Xlib's XWindowAttributes on 64-bit platforms.
    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        public int X, Y, Width, Height, BorderWidth, Depth;
        public nint Visual;
        public nint Root;
        public int Class, BitGravity, WinGravity, BackingStore;
        public nuint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nint Colormap;
        public int MapInstalled, MapState;
        public long AllEventMasks, YourEventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public nint Screen;
    }

    // Xlib's XImage on 64-bit platforms, through its function table.
    [StructLayout(LayoutKind.Sequential)]
    private struct XImage
    {
        public int Width, Height, XOffset, Format;
        public byte* Data;
        public int ByteOrder, BitmapUnit, BitmapBitOrder, BitmapPad, Depth, BytesPerLine, BitsPerPixel;
        public nuint RedMask, GreenMask, BlueMask;
        public nint ObData;
        public nint CreateImage;
        public delegate* unmanaged<XImage*, int> DestroyImageFn;
        public nint GetPixel, PutPixel, SubImage, AddPixel;

        public readonly void DestroyImage(XImage* self) => _ = DestroyImageFn(self);
    }

    [LibraryImport(LibX11)] private static partial int XCloseDisplay(nint display);
    [LibraryImport(LibX11)] private static partial int XScreenCount(nint display);
    [LibraryImport(LibX11)] private static partial nint XRootWindow(nint display, int screen);
    [LibraryImport(LibX11)] private static partial int XQueryTree(nint display, nint window, out nint root, out nint parent, out nint* children, out int count);
    [LibraryImport(LibX11)] private static partial int XGetClassHint(nint display, nint window, XClassHint* hint);
    [LibraryImport(LibX11)] private static partial int XGetWMName(nint display, nint window, XTextProperty* name);
    [LibraryImport(LibX11)] private static partial int Xutf8TextPropertyToTextList(nint display, XTextProperty* property, byte*** list, out int count);
    [LibraryImport(LibX11)] private static partial void XFreeStringList(byte** list);
    [LibraryImport(LibX11)] private static partial int XGetWindowAttributes(nint display, nint window, XWindowAttributes* attributes);
    [LibraryImport(LibX11)] private static partial XImage* XGetImage(nint display, nint drawable, int x, int y, uint width, uint height, ulong planeMask, int format);
    [LibraryImport(LibX11)] private static partial nint XSetErrorHandler(nint handler);
    [LibraryImport(LibXrandr)] private static partial int XRRQueryExtension(nint display, out int eventBase, out int errorBase);
    [LibraryImport(LibXrandr)] private static partial int XRRQueryVersion(nint display, out int major, out int minor);
    [LibraryImport(LibXrandr)] private static partial XRRMonitorInfo* XRRGetMonitors(nint display, nint window, int getActive, out int count);
    [LibraryImport(LibXrandr)] private static partial void XRRFreeMonitors(XRRMonitorInfo* monitors);
    [LibraryImport(LibXrandr)] private static partial XRRScreenResources* XRRGetScreenResourcesCurrent(nint display, nint window);
    [LibraryImport(LibXrandr)] private static partial void XRRFreeScreenResources(XRRScreenResources* resources);
    [LibraryImport(LibXrandr)] private static partial XRROutputInfo* XRRGetOutputInfo(nint display, XRRScreenResources* resources, nint output);
    [LibraryImport(LibXrandr)] private static partial void XRRFreeOutputInfo(XRROutputInfo* info);
    [LibraryImport(LibXrandr)] private static partial void* XRRGetCrtcInfo(nint display, XRRScreenResources* resources, nint crtc);
    [LibraryImport(LibXrandr)] private static partial void XRRFreeCrtcInfo(void* info);
    [LibraryImport(LibXrandr)] private static partial int XRRGetOutputProperty(nint display, nint output, nint property, long offset, long length, int delete,
        int pending, nint requestedType, out nint actualType, out int actualFormat, out nuint itemCount, out nuint bytesAfter, out byte* data);
    [LibraryImport(LibXcomposite)] private static partial int XCompositeQueryExtension(nint display, out int eventBase, out int errorBase);
    [LibraryImport(LibXcomposite)] private static partial int XCompositeQueryVersion(nint display, out int major, out int minor);
    [LibraryImport(LibXcomposite)] private static partial void XCompositeRedirectWindow(nint display, nint window, int update);
}
