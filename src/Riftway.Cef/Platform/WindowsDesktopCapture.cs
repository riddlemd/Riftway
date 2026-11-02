using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>The Windows side of Chromium's desktop media lists: webrtc's window list (<c>EnumerateCapturableWindows</c>)
/// with Chromium's own-process windows merged in z-order, webrtc's screen list (<c>GetScreenList</c>), GDI captures,
/// and <c>GetWindowIcon</c>.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsDesktopCapture
{
    private const int GwOwner = 4, GwlExStyle = -20;
    private const uint WmNull = 0x0000, WmGetIcon = 0x007F;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ResponseTimeoutMs = 50;
    private const int DwmwaCloaked = 14;
    private const uint DisplayDeviceActive = 0x1;
    private const uint PwRenderFullContent = 0x2;
    private const uint SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000;

    private static readonly Guid s_virtualDesktopManager = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");
    private static readonly Guid s_iidVirtualDesktopManager = new("a5cd92ff-29be-454c-8d04-d82879fb3f1b");

    /// <summary>Capturable windows front to back: other processes' through webrtc's rules, this process's through
    /// Chromium's <c>GetCurrentProcessWindows</c>, merged by <c>EnumWindows</c> z-order.</summary>
    public static IReadOnlyList<CaptureSource> CaptureWindows()
    {
        var zOrder = new List<long>();
        var gc = GCHandle.Alloc(zOrder);
        bool listed;
        try { listed = EnumWindows(&Collect, GCHandle.ToIntPtr(gc)) != 0; }
        finally { gc.Free(); }

        var manager = VirtualDesktopManager();
        var others = new List<CaptureSource>();
        var own = new List<CaptureSource>();
        try
        {
            foreach (var hwnd in zOrder)
            {
                var facts = Facts((nint)hwnd, manager);
                if (DesktopMedia.IsWindowsCapturable(facts)) others.Add(new CaptureSource(hwnd, facts.Title));
                else if (DesktopMedia.IsWindowsOwnCapturable(facts)) own.Add(new CaptureSource(hwnd, facts.Title));
            }
        }
        finally { if (manager != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)manager)[2])(manager); }
        return DesktopMedia.MergeByZOrder(others, own, listed ? zOrder : null);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int Collect(nint hwnd, nint param)
    {
        ((List<long>)GCHandle.FromIntPtr(param).Target!).Add(hwnd);
        return 1;
    }

    private static DesktopMedia.WindowsWindowFacts Facts(nint hwnd, nint manager)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, &pid);
        var ownedByUs = pid == (uint)Environment.ProcessId;
        var responding = SendMessageTimeoutW(hwnd, WmNull, 0, 0, SmtoAbortIfHung, ResponseTimeoutMs, null) != 0;
        var title = "";
        // GetWindowText on a hung window of this process would block, so webrtc skips it.
        if ((!ownedByUs || responding) && GetWindowTextLengthW(hwnd) is var length && length != 0)
        {
            var size = DesktopMedia.WindowsTitleBuffer(ownedByUs, length);
            var buffer = new char[size];
            fixed (char* b = buffer)
                if (GetWindowTextW(hwnd, b, size) is var n && n > 0) title = new string(b, 0, n);
        }
        var className = stackalloc char[256];
        var classLength = GetClassNameW(hwnd, className, 256);
        var onDesktop = true;
        if (manager != 0)
        {
            int on;
            var check = (delegate* unmanaged[Stdcall]<nint, nint, int*, int>)(*(nint**)manager)[3];
            if (check(manager, hwnd, &on) >= 0 && on == 0) onDesktop = false;
        }
        int cloaked = 0;
        var cloakRead = DwmGetWindowAttribute(hwnd, DwmwaCloaked, &cloaked, sizeof(int)) == 0;
        return new DesktopMedia.WindowsWindowFacts(
            IsWindowVisible(hwnd) != 0, IsIconic(hwnd) != 0, GetWindow(hwnd, GwOwner) != 0, GetWindowLongW(hwnd, GwlExStyle), responding,
            ownedByUs, title, classLength > 0 ? new string(className, 0, classLength) : "", onDesktop, cloakRead && cloaked != 0);
    }

    private static nint VirtualDesktopManager()
    {
        nint manager;
        var clsid = s_virtualDesktopManager;
        var iid = s_iidVirtualDesktopManager;
        // CLSCTX_ALL; null when COM is not initialised on this thread, which webrtc also tolerates.
        return CoCreateInstance(&clsid, 0, 0x17, &iid, &manager) >= 0 ? manager : 0;
    }

    /// <summary>webrtc's <c>GetScreenList</c>: active display devices, each id its <c>EnumDisplayDevices</c> index, with
    /// Electron's DirectX-path <c>display_id</c> where a monitor shows that device.</summary>
    public static IReadOnlyList<CaptureSource> CaptureScreens()
    {
        var monitors = WindowsDisplay.Monitors();
        var screens = new List<CaptureSource>();
        for (var index = 0; ; index++)
        {
            var device = new DisplayDevice { Size = (uint)sizeof(DisplayDevice) };
            if (EnumDisplayDevicesW(null, (uint)index, &device, 0) == 0) break;
            if ((device.StateFlags & DisplayDeviceActive) == 0) continue;
            var name = new string(device.DeviceName);
            var displayId = monitors.Any(m => m.DeviceName == name)
                ? DesktopMedia.WindowsDisplayId(name, WindowsDisplay.PathTarget(name)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "";
            screens.Add(new CaptureSource(index, "", displayId));
        }
        return screens;
    }

    /// <summary>The screen's pixels: its monitor's rectangle of the desktop, copied with GDI. Null without a monitor.</summary>
    public static CaptureBitmap? CaptureScreenImage(long index)
    {
        var device = new DisplayDevice { Size = (uint)sizeof(DisplayDevice) };
        if (EnumDisplayDevicesW(null, (uint)index, &device, 0) == 0) return null;
        var name = new string(device.DeviceName);
        if (WindowsDisplay.Monitors().FirstOrDefault(m => m.DeviceName == name) is not { } monitor) return null;
        var screen = GetDC(0);
        try
        {
            return Grab(monitor.Right - monitor.Left, monitor.Bottom - monitor.Top,
                dc => BitBlt(dc, 0, 0, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top, screen, monitor.Left, monitor.Top, SrcCopy | CaptureBlt) != 0);
        }
        finally { ReleaseDC(0, screen); }
    }

    /// <summary>The window's pixels through <c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c>, which also reads
    /// DirectComposition content. Null when the window is gone or refuses.</summary>
    public static CaptureBitmap? CaptureWindowImage(long hwnd)
    {
        Rect r;
        if (GetWindowRect((nint)hwnd, &r) == 0) return null;
        return Grab(r.Right - r.Left, r.Bottom - r.Top, dc => PrintWindow((nint)hwnd, dc, PwRenderFullContent) != 0);
    }

    private static CaptureBitmap? Grab(int width, int height, Func<nint, bool> draw)
    {
        if (width <= 0 || height <= 0) return null;
        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        ReleaseDC(0, screen);
        var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        void* bits;
        var bitmap = CreateDIBSection(dc, &header, 0, &bits, 0, 0);
        if (bitmap == 0)
        {
            DeleteDC(dc);
            return null;
        }
        var old = SelectObject(dc, bitmap);
        try
        {
            if (!draw(dc)) return null;
            return new CaptureBitmap(width, height, new ReadOnlySpan<byte>(bits, width * height * 4).ToArray());
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    /// <summary>Chromium's <c>GetWindowIcon</c> on Windows: <c>WM_GETICON</c> big, the class icon, <c>WM_GETICON</c>
    /// small and small2, then the small class icon; null without any.</summary>
    public static CaptureBitmap? WindowIcon(long hwnd)
    {
        var h = (nint)hwnd;
        nuint icon = 0;
        SendMessageTimeoutW(h, WmGetIcon, 1, 0, SmtoAbortIfHung, 5, &icon);
        if (icon == 0) icon = GetClassLongPtrW(h, -14);
        if (icon == 0) SendMessageTimeoutW(h, WmGetIcon, 0, 0, SmtoAbortIfHung, 5, &icon);
        if (icon == 0) SendMessageTimeoutW(h, WmGetIcon, 2, 0, SmtoAbortIfHung, 5, &icon);
        if (icon == 0) icon = GetClassLongPtrW(h, -34);
        if (icon == 0) return null;
        return WindowsFileIcon.Pixels((nint)icon) is { } p ? new CaptureBitmap(p.Width, p.Height, p.Bgra) : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];
        public uint StateFlags;
        public fixed char DeviceId[128];
        public fixed char DeviceKey[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll")] private static extern int EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, uint* pid);
    [DllImport("user32.dll")] private static extern int IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern int IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nuint GetClassLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextW(nint hwnd, char* text, int max);
    [DllImport("user32.dll")] private static extern int GetClassNameW(nint hwnd, char* name, int max);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, nuint* result);
    [DllImport("user32.dll")] private static extern int EnumDisplayDevicesW(char* device, uint index, DisplayDevice* info, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowRect(nint hwnd, Rect* rect);
    [DllImport("user32.dll")] private static extern int PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, BitmapInfoHeader* info, uint usage, void** bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern int BitBlt(nint dest, int x, int y, int width, int height, nint src, int srcX, int srcY, uint rop);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, void* value, int size);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);
}
