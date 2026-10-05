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

    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, nint* result);
    [DllImport("user32.dll")] private static extern int GetIconInfo(nint icon, IconInfo* info);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint obj, int size, void* buffer);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, BitmapInfoHeader* info, uint usage);
    [DllImport("gdi32.dll")] private static extern int DeleteObject(nint obj);

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
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result);
}
