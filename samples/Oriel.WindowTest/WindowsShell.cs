using System.Collections.Concurrent;
using System.Runtime.InteropServices;

/// <summary>Reads the shell's side of taskbar calls back: flash notifications through a shell hook window,
/// and the taskbar's buttons through UI Automation. Windows only.</summary>
static unsafe class WindowsShell
{
    internal const nint FlashCode = 0x8006; // HSHELL_FLASH

    private static readonly ConcurrentQueue<(nint Code, nint Hwnd)> s_events = new();
    private static uint s_shellMessage;
    private static Thread? s_thread;

    /// <summary>Starts a hidden window that receives shell hook notifications.</summary>
    internal static void StartShellHook()
    {
        if (s_thread is not null) return;
        using var ready = new ManualResetEventSlim();
        s_thread = new Thread(() =>
        {
            fixed (char* name = "OrielWindowTestShellHook")
            {
                var cls = new WndClass { WndProc = (delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc, Instance = GetModuleHandleW(null), ClassName = name };
                RegisterClassW(&cls);
                var hwnd = CreateWindowExW(0, name, name, 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, cls.Instance, 0);
                fixed (char* message = "SHELLHOOK") s_shellMessage = RegisterWindowMessageW(message);
                if (RegisterShellHookWindow(hwnd) == 0) Console.Error.WriteLine("WindowTest: RegisterShellHookWindow failed");
                ready.Set();
                Msg msg;
                while (GetMessageW(&msg, 0, 0, 0) > 0) DispatchMessageW(&msg);
            }
        }) { IsBackground = true, Name = "shell hook" };
        s_thread.Start();
        ready.Wait();
    }

    /// <summary>How many times the shell reported <paramref name="code"/> for <paramref name="hwnd"/>.</summary>
    internal static int Count(nint code, nint hwnd) => s_events.Count(e => e.Code == code && e.Hwnd == hwnd);

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == s_shellMessage && s_shellMessage != 0) s_events.Enqueue((wParam, lParam));
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static readonly Guid s_clsidAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid s_iidAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");

    /// <summary>The names of every element under the taskbar (Shell_TrayWnd), as UI Automation reports them.</summary>
    internal static List<string> TaskbarNames()
    {
        var names = new List<string>();
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return names;
        void* automation;
        var clsid = s_clsidAutomation;
        var iid = s_iidAutomation;
        // A worker thread: UI Automation clients belong in the MTA, which .NET gives pool threads.
        if (CoCreateInstance(&clsid, null, 1, &iid, &automation) < 0) return names;
        void* root = null, condition = null, all = null;
        try
        {
            if (((delegate* unmanaged<void*, nint, void**, int>)Slot(automation, 6))(automation, tray, &root) < 0) return names; // ElementFromHandle
            if (((delegate* unmanaged<void*, void**, int>)Slot(automation, 21))(automation, &condition) < 0) return names; // CreateTrueCondition
            if (((delegate* unmanaged<void*, int, void*, void**, int>)Slot(root, 6))(root, 4 /* TreeScope_Descendants */, condition, &all) < 0) return names; // FindAll
            int length;
            ((delegate* unmanaged<void*, int*, int>)Slot(all, 3))(all, &length);                 // get_Length
            for (var i = 0; i < length; i++)
            {
                void* element;
                if (((delegate* unmanaged<void*, int, void**, int>)Slot(all, 4))(all, i, &element) < 0) continue; // GetElement
                nint name;
                if (((delegate* unmanaged<void*, nint*, int>)Slot(element, 23))(element, &name) >= 0 && name != 0) // get_CurrentName
                {
                    names.Add(Marshal.PtrToStringBSTR(name));
                    Marshal.FreeBSTR(name);
                }
                Release(element);
            }
        }
        finally
        {
            Release(all);
            Release(condition);
            Release(root);
            Release(automation);
        }
        return names;
    }

    /// <summary>The taskbar button whose name starts with <paramref name="prefix"/>: its help text, which the shell
    /// fills from the overlay icon's description, and its screen bounds; null if there is none.</summary>
    internal static (string Help, (int X, int Y, int Width, int Height) Bounds)? TaskbarButton(string prefix)
    {
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return null;
        void* automation;
        var clsid = s_clsidAutomation;
        var iid = s_iidAutomation;
        if (CoCreateInstance(&clsid, null, 1, &iid, &automation) < 0) return null;
        void* root = null, condition = null, all = null;
        try
        {
            if (((delegate* unmanaged<void*, nint, void**, int>)Slot(automation, 6))(automation, tray, &root) < 0) return null;
            if (((delegate* unmanaged<void*, void**, int>)Slot(automation, 21))(automation, &condition) < 0) return null;
            if (((delegate* unmanaged<void*, int, void*, void**, int>)Slot(root, 6))(root, 4, condition, &all) < 0) return null;
            int length;
            ((delegate* unmanaged<void*, int*, int>)Slot(all, 3))(all, &length);
            for (var i = 0; i < length; i++)
            {
                void* element;
                if (((delegate* unmanaged<void*, int, void**, int>)Slot(all, 4))(all, i, &element) < 0) continue;
                try
                {
                    var name = Bstr(element, 23); // get_CurrentName
                    if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var help = Bstr(element, 31) ?? ""; // get_CurrentHelpText
                    UiaRect rect;
                    ((delegate* unmanaged<void*, UiaRect*, int>)Slot(element, 43))(element, &rect); // get_CurrentBoundingRectangle
                    return (help, (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
                }
                finally { Release(element); }
            }
            return null;
        }
        finally
        {
            Release(all);
            Release(condition);
            Release(root);
            Release(automation);
        }
    }

    private static string? Bstr(void* element, int slot)
    {
        nint value;
        if (((delegate* unmanaged<void*, nint*, int>)Slot(element, slot))(element, &value) < 0 || value == 0) return null;
        try { return Marshal.PtrToStringBSTR(value); }
        finally { Marshal.FreeBSTR(value); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UiaRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>The screen's pixels in <paramref name="bounds"/> as (R, G, B), row by row.</summary>
    internal static (byte R, byte G, byte B)[] ScreenPixels((int X, int Y, int Width, int Height) bounds)
    {
        var (w, h) = (Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        var pixels = new (byte, byte, byte)[w * h];
        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        var header = new BitmapInfoHeader { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
        void* bits;
        var bitmap = CreateDIBSection(dc, &header, 0, &bits, 0, 0);
        try
        {
            if (bitmap == 0) return pixels;
            var old = SelectObject(dc, bitmap);
            _ = BitBlt(dc, 0, 0, w, h, screen, bounds.X, bounds.Y, 0x00CC0020 /* SRCCOPY */);
            _ = SelectObject(dc, old);
            var data = (byte*)bits;
            for (var i = 0; i < pixels.Length; i++) pixels[i] = (data[i * 4 + 2], data[i * 4 + 1], data[i * 4]);
        }
        finally
        {
            if (bitmap != 0) _ = DeleteObject(bitmap);
            _ = DeleteDC(dc);
            _ = ReleaseDC(0, screen);
        }
        return pixels;
    }

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, BitmapInfoHeader* header, uint usage, void** bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern int BitBlt(nint dc, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);

    /// <summary>The window's App User Model property, read back from its property store.</summary>
    internal static string? AppUserModel(nint hwnd, Oriel.Cef.Platform.AppUserModelProperty property)
        => OperatingSystem.IsWindows() ? Oriel.Cef.Platform.WindowsWindow.GetAppUserModel(hwnd, property) : null;

    /// <summary>The window's system backdrop, read back from DWM.</summary>
    internal static Oriel.Cef.Platform.WindowsBackdrop? Backdrop(nint hwnd)
        => OperatingSystem.IsWindows() ? Oriel.Cef.Platform.WindowsWindow.GetBackdrop(hwnd) : null;
    /// <summary>The centre pixel of the window's big or small icon as (B, G, R, A), from <c>WM_GETICON</c>; null if
    /// the window has none.</summary>
    internal static (byte B, byte G, byte R, byte A)? IconCentre(nint hwnd, bool big)
    {
        nint icon;
        if (SendMessageTimeoutW(hwnd, 0x7F /* WM_GETICON */, big ? 1 : 2 /* ICON_BIG : ICON_SMALL2 */, 0, 2 /* SMTO_ABORTIFHUNG */, 1000, &icon) == 0 || icon == 0)
            return null;
        IconInfo info;
        if (GetIconInfo(icon, &info) == 0) return null;
        try
        {
            if (info.Color == 0) return null;
            BitmapHeader bitmap;
            if (GetObjectW(info.Color, sizeof(BitmapHeader), &bitmap) == 0) return null;
            var header = new BitmapInfoHeader { Size = (uint)sizeof(BitmapInfoHeader), Width = bitmap.Width, Height = -bitmap.Height, Planes = 1, BitCount = 32 };
            var pixels = new byte[bitmap.Width * bitmap.Height * 4];
            var dc = GetDC(0);
            try
            {
                fixed (byte* data = pixels)
                    if (GetDIBits(dc, info.Color, 0, (uint)bitmap.Height, data, &header, 0) == 0) return null;
            }
            finally { _ = ReleaseDC(0, dc); }
            var i = ((bitmap.Height / 2) * bitmap.Width + bitmap.Width / 2) * 4;
            return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
        }
        finally
        {
            if (info.Color != 0) _ = DeleteObject(info.Color);
            if (info.Mask != 0) _ = DeleteObject(info.Mask);
        }
    }

    /// <summary>The screen pixel at (<paramref name="x"/>, <paramref name="y"/>) as R, G, B, read the way screen capture
    /// reads it.</summary>
    internal static (byte R, byte G, byte B) ScreenPixel(int x, int y)
    {
        var dc = GetDC(0);
        try
        {
            var c = GetPixel(dc, x, y);
            return ((byte)c, (byte)(c >> 8), (byte)(c >> 16));
        }
        finally { _ = ReleaseDC(0, dc); }
    }

    /// <summary>The window's layered alpha, read back from Win32.</summary>
    internal static byte? Alpha(nint hwnd) => OperatingSystem.IsWindows() ? Oriel.Cef.Platform.WindowsWindowStyle.GetAlpha(hwnd) : null;

    /// <summary>Whether the window has <c>WS_EX_TRANSPARENT</c>.</summary>
    internal static bool ClickThrough(nint hwnd) => OperatingSystem.IsWindows() && Oriel.Cef.Platform.WindowsWindowStyle.IsClickThrough(hwnd);

    /// <summary>Whether the window forwards mouse moves to its page, read back from Oriel's hook set.</summary>
    internal static bool Forwarding(nint hwnd) => OperatingSystem.IsWindows() && Oriel.Cef.Platform.WindowsSubclass.IsForwardingMouseMoves(hwnd);

    /// <summary>Puts the real cursor at a screen point and wiggles it a mickey, so low-level mouse hooks see moves.</summary>
    internal static void MoveMouse(int x, int y)
    {
        _ = SetCursorPos(x, y);
        mouse_event(0x0001 /* MOVE */, 1, 0, 0, 0);
        Thread.Sleep(40);
        mouse_event(0x0001 /* MOVE */, -1, 0, 0, 0);
        Thread.Sleep(40);
    }

    /// <summary>Whether DWM renders the window's frame.</summary>
    internal static bool NcRendering(nint hwnd) => OperatingSystem.IsWindows() && Oriel.Cef.Platform.WindowsWindowStyle.IsDwmNonClientRendering(hwnd);

    /// <summary>The foreground window.</summary>
    internal static nint Foreground() => GetForegroundWindow();

    /// <summary>Whether Win32 has the window visible (<c>IsWindowVisible</c>).</summary>
    internal static bool Visible(nint hwnd) => IsWindowVisible(hwnd) != 0;

    /// <summary>The top-level window that takes the mouse at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal static nint RootAt(int x, int y) => GetAncestor(WindowFromPoint(new PointNative { X = x, Y = y }), 2 /* GA_ROOT */);

    /// <summary>The window's bounds in screen pixels.</summary>
    internal static (int X, int Y, int Width, int Height) Bounds(nint hwnd)
    {
        RectNative r;
        _ = GetWindowRect(hwnd, &r);
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>What the window answers <c>WM_NCHITTEST</c> for a screen point: 1 client, 2 caption.</summary>
    internal static int HitTest(nint hwnd, int x, int y)
    {
        nint result;
        _ = SendMessageTimeoutW(hwnd, 0x0084 /* WM_NCHITTEST */, 0, (nint)((y << 16) | (x & 0xFFFF)), 2, 1000, &result);
        return (int)result;
    }

    /// <summary>Clicks the real left mouse button at a screen point.</summary>
    internal static void Click(int x, int y)
    {
        _ = SetCursorPos(x, y);
        Thread.Sleep(200);
        mouse_event(0x0002 /* LEFTDOWN */, 0, 0, 0, 0);
        Thread.Sleep(80);
        mouse_event(0x0004 /* LEFTUP */, 0, 0, 0, 0);
        Thread.Sleep(400);
    }

    /// <summary>Moves the real mouse to a screen point without waiting.</summary>
    internal static void MovePointer(int x, int y) => _ = SetCursorPos(x, y);

    /// <summary>Moves the real mouse to a screen point.</summary>
    internal static void Hover(int x, int y)
    {
        _ = SetCursorPos(x, y);
        Thread.Sleep(400);
    }

    /// <summary>Drags the real mouse with the left button from one screen point to another, in steps.</summary>
    internal static void Drag(int x0, int y0, int x1, int y1)
    {
        _ = SetCursorPos(x0, y0);
        Thread.Sleep(150);
        mouse_event(0x0002 /* LEFTDOWN */, 0, 0, 0, 0);
        Thread.Sleep(150);
        for (var i = 1; i <= 10; i++)
        {
            _ = SetCursorPos(x0 + (x1 - x0) * i / 10, y0 + (y1 - y0) * i / 10);
            Thread.Sleep(40);
        }
        Thread.Sleep(150);
        mouse_event(0x0004 /* LEFTUP */, 0, 0, 0, 0);
        Thread.Sleep(300);
    }

    /// <summary>Sends <c>WM_SIZING</c> (edge <paramref name="wmsz"/>) or, when null, <c>WM_MOVING</c> with a proposed
    /// rectangle, and returns the rectangle the window left in it.</summary>
    internal static (int Left, int Top, int Right, int Bottom) SendDrag(nint hwnd, int? wmsz, (int Left, int Top, int Right, int Bottom) proposed)
    {
        var r = new RectNative { Left = proposed.Left, Top = proposed.Top, Right = proposed.Right, Bottom = proposed.Bottom };
        nint result;
        _ = SendMessageTimeoutW(hwnd, wmsz is null ? 0x0216u : 0x0214u, wmsz ?? 0, (nint)(&r), 2, 1000, &result);
        return (r.Left, r.Top, r.Right, r.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointNative point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowRect(nint hwnd, RectNative* rect);
    [DllImport("user32.dll")] private static extern int SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nint extra);

    /// <summary>Sends the window the <c>WM_COMMAND</c> a click on thumbnail button <paramref name="id"/> sends.</summary>
    internal static void ClickThumbButton(nint hwnd, int id)
    {
        nint result;
        _ = SendMessageTimeoutW(hwnd, 0x0111 /* WM_COMMAND */, (0x1800 /* THBN_CLICKED */ << 16) | id, 0, 2, 1000, &result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
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
        // Room for the colour table GetDIBits may write after the header.
        public fixed uint Colors[256];
    }

    /// <summary>The screen pixel at the centre of the window's client area, as composited by DWM.</summary>
    internal static (byte R, byte G, byte B) ClientCentrePixel(nint hwnd)
    {
        // POINT packed in a long: x in the low half, y in the high.
        long origin = 0;
        var client = new int[4];
        fixed (int* r = client) _ = GetClientRect(hwnd, r);
        _ = ClientToScreen(hwnd, &origin);
        var dc = GetDC(0);
        var c = GetPixel(dc, (int)origin + client[2] / 2, (int)(origin >> 32) + client[3] / 2);
        _ = ReleaseDC(0, dc);
        return ((byte)c, (byte)(c >> 8), (byte)(c >> 16));
    }

    /// <summary>What the window answers <c>WM_NCHITTEST</c> two pixels inside its left edge, half way down
    /// (<c>HTLEFT</c> is 10, <c>HTCLIENT</c> 1).</summary>
    internal static nint LeftEdgeHitTest(nint hwnd)
    {
        var rect = new int[4];
        fixed (int* r = rect) _ = GetWindowRect(hwnd, r);
        var x = rect[0] + 2;
        var y = (rect[1] + rect[3]) / 2;
        nint result;
        _ = SendMessageTimeoutW(hwnd, 0x84 /* WM_NCHITTEST */, 0, (nint)((y << 16) | (x & 0xFFFF)), 2 /* SMTO_ABORTIFHUNG */, 1000, &result);
        return result;
    }

    /// <summary>The window's <c>GWL_STYLE</c>.</summary>
    internal static uint Style(nint hwnd) => (uint)GetWindowLongPtrW(hwnd, -16);

    /// <summary>Sets the window's <c>GWL_STYLE</c> as another party (Chromium) would.</summary>
    internal static void SetStyle(nint hwnd, uint style) => SetWindowLongPtrW(hwnd, -16, (nint)style);

    [DllImport("user32.dll")] private static extern int GetClientRect(nint hwnd, int* rect);
    [DllImport("user32.dll")] private static extern int GetWindowRect(nint hwnd, int* rect);
    [DllImport("user32.dll")] private static extern int ClientToScreen(nint hwnd, long* point);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, nint* result);
    [DllImport("user32.dll")] private static extern int GetIconInfo(nint icon, IconInfo* info);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint obj, int size, void* buffer);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, BitmapInfoHeader* info, uint usage);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);

    /// <summary>Open Explorer windows as (folder path, selected item names, window object), from Shell.Application.</summary>
    internal static List<(string Folder, List<string> Selected, dynamic Window)> ExplorerWindows()
    {
        var found = new List<(string, List<string>, dynamic)>();
        dynamic shell = Activator.CreateInstance(OperatingSystem.IsWindows() ? Type.GetTypeFromProgID("Shell.Application")! : throw new PlatformNotSupportedException())!;
        foreach (var window in shell.Windows())
        {
            try
            {
                string? url = window.LocationURL;
                if (url is null || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
                var folder = new Uri(url).LocalPath;
                var selected = new List<string>();
                foreach (var item in window.Document.SelectedItems()) selected.Add((string)item.Name);
                found.Add((folder, selected, window));
            }
            // Windows that are closing, or not Explorer, answer with errors.
            catch (Exception) { }
        }
        return found;
    }

    /// <summary>Closes every Explorer window showing <paramref name="folder"/>.</summary>
    internal static void CloseExplorerAt(string folder)
    {
        bool At(string path) => string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);
        foreach (var (path, _, window) in ExplorerWindows())
            if (At(path)) window.Quit();
        // Quit returns before the window is gone, and the shell hands the next open of that folder to the dying window.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && ExplorerWindows().Any(w => At(w.Folder))) Thread.Sleep(100);
        Thread.Sleep(500);
    }

    /// <summary>The Recycle Bin's file for an item that was <paramref name="originalPath"/>, or null.</summary>
    internal static string? RecycledCopyOf(string originalPath)
    {
        dynamic shell = Activator.CreateInstance(OperatingSystem.IsWindows() ? Type.GetTypeFromProgID("Shell.Application")! : throw new PlatformNotSupportedException())!;
        var bin = shell.Namespace(10 /* ssfBITBUCKET */);
        foreach (var item in bin.Items())
        {
            // Detail 1 is the folder the item was deleted from.
            string name = item.Name, from = bin.GetDetailsOf(item, 1);
            if (string.Equals(Path.Combine(from, name), originalPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.Combine(from, name), Path.ChangeExtension(originalPath, null), StringComparison.OrdinalIgnoreCase))
                return (string)item.Path;
        }
        return null;
    }

    /// <summary>The clipboard owner window's process id and class name, read straight from Win32; (0, "") with no owner.</summary>
    internal static (int ProcessId, string ClassName) ClipboardOwner()
    {
        var owner = GetClipboardOwner();
        if (owner == 0) return (0, "");
        uint pid = 0;
        if (GetWindowThreadProcessId(owner, &pid) == 0) return (0, "");
        var name = stackalloc char[256];
        var length = GetClassNameW(owner, name, 256);
        return ((int)pid, new string(name, 0, Math.Max(length, 0)));
    }

    /// <summary>A .lnk file's fields as WScript.Shell reads them, independently of Oriel's IShellLink code.</summary>
    internal static (string Target, string Arguments, string WorkingDirectory, string Description, string IconLocation) ShortcutFields(string path)
    {
        dynamic shell = Activator.CreateInstance(OperatingSystem.IsWindows() ? Type.GetTypeFromProgID("WScript.Shell")! : throw new PlatformNotSupportedException())!;
        var link = shell.CreateShortcut(path);
        return ((string)link.TargetPath, (string)link.Arguments, (string)link.WorkingDirectory, (string)link.Description, (string)link.IconLocation);
    }

    /// <summary>Deletes a recycled item for good: its <c>$R</c> file and the <c>$I</c> record beside it.</summary>
    internal static void Purge(string recycledPath)
    {
        File.Delete(recycledPath);
        var info = Path.Combine(Path.GetDirectoryName(recycledPath)!, "$I" + Path.GetFileName(recycledPath)[2..]);
        File.Delete(info);
    }

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    private static void Release(void* com)
    {
        if (com is not null) ((delegate* unmanaged<void*, uint>)Slot(com, 2))(com);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClass
    {
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll")] private static extern ushort RegisterClassW(WndClass* cls);
    [DllImport("user32.dll")] private static extern nint CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern uint RegisterWindowMessageW(char* name);
    [DllImport("user32.dll")] private static extern int RegisterShellHookWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(Msg* msg);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? cls, string? name);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(char* name);
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, uint* pid);
    [DllImport("user32.dll")] private static extern int GetClassNameW(nint hwnd, char* name, int capacity);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result);
}
