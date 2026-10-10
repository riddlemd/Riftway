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
    internal static List<string> TaskbarNames() => FindWindowW("Shell_TrayWnd", null) is var tray and not 0 ? ElementNames(tray) : [];

    /// <summary>The names of every element under <paramref name="hwnd"/>, as UI Automation reports them.</summary>
    internal static List<string> ElementNames(nint hwnd)
    {
        var names = new List<string>();
        void* automation;
        var clsid = s_clsidAutomation;
        var iid = s_iidAutomation;
        // A worker thread: UI Automation clients belong in the MTA, which .NET gives pool threads.
        if (CoCreateInstance(&clsid, null, 1, &iid, &automation) < 0) return names;
        void* root = null, condition = null, all = null;
        try
        {
            if (((delegate* unmanaged<void*, nint, void**, int>)Slot(automation, 6))(automation, hwnd, &root) < 0) return names; // ElementFromHandle
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

    /// <summary>Selects the list items named <paramref name="names"/> under <paramref name="hwnd"/> through UI
    /// Automation's SelectionItem pattern, as Ctrl+clicks would; how many were selected.</summary>
    internal static int SelectItems(nint hwnd, IReadOnlyList<string> names)
    {
        var selected = 0;
        Walk(hwnd, (element, name) =>
        {
            var e = (void*)element;
            int type;
            if (!names.Contains(name) || ((delegate* unmanaged<void*, int*, int>)Slot(e, 21))(e, &type) < 0 || type != 50007 /* UIA_ListItemControlTypeId */) return;
            void* pattern;
            if (((delegate* unmanaged<void*, int, void**, int>)Slot(e, 16))(e, 10010 /* UIA_SelectionItemPatternId */, &pattern) < 0 || pattern is null) return;
            // Select replaces the selection; AddToSelection extends it.
            if (((delegate* unmanaged<void*, int>)Slot(pattern, selected == 0 ? 3 : 4))(pattern) >= 0) selected++;
            Release(pattern);
        });
        return selected;
    }

    // Visits every element under the window with its name. UI Automation clients belong in the MTA, which .NET gives pool threads.
    private static void Walk(nint hwnd, Action<nint, string> visit)
    {
        if (hwnd == 0) return;
        void* automation;
        var clsid = s_clsidAutomation;
        var iid = s_iidAutomation;
        if (CoCreateInstance(&clsid, null, 1, &iid, &automation) < 0) return;
        void* root = null, condition = null, all = null;
        try
        {
            if (((delegate* unmanaged<void*, nint, void**, int>)Slot(automation, 6))(automation, hwnd, &root) < 0) return;
            if (((delegate* unmanaged<void*, void**, int>)Slot(automation, 21))(automation, &condition) < 0) return;
            if (((delegate* unmanaged<void*, int, void*, void**, int>)Slot(root, 6))(root, 4, condition, &all) < 0) return;
            int length;
            ((delegate* unmanaged<void*, int*, int>)Slot(all, 3))(all, &length);
            for (var i = 0; i < length; i++)
            {
                void* element;
                if (((delegate* unmanaged<void*, int, void**, int>)Slot(all, 4))(all, i, &element) < 0) continue;
                nint name;
                if (((delegate* unmanaged<void*, nint*, int>)Slot(element, 23))(element, &name) >= 0 && name != 0)
                {
                    var text = Marshal.PtrToStringBSTR(name);
                    Marshal.FreeBSTR(name);
                    visit((nint)element, text);
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
    }
    /// <summary>The visible top-level file dialog (<c>#32770</c>) titled <paramref name="title"/>, or 0.</summary>
    internal static nint FileDialog(string title)
    {
        var hwnd = FindWindowW("#32770", title);
        return hwnd != 0 && IsWindowVisible(hwnd) != 0 ? hwnd : 0;
    }

    /// <summary>The window that owns <paramref name="hwnd"/>, or 0.</summary>
    internal static nint Owner(nint hwnd) => GetWindow(hwnd, 4 /* GW_OWNER */);

    /// <summary>The id of the thread that created <paramref name="hwnd"/>.</summary>
    internal static uint ThreadOf(nint hwnd) => GetWindowThreadProcessId(hwnd, null);

    /// <summary>Whether <paramref name="hwnd"/> takes input.</summary>
    internal static bool IsEnabled(nint hwnd) => IsWindowEnabled(hwnd) != 0;

    /// <summary>The text of the dialog's control <paramref name="id"/>; <c>IDOK</c> (1) is the accept button.</summary>
    internal static string DialogItemText(nint dialog, int id)
    {
        var buffer = stackalloc char[512];
        var length = GetDlgItemTextW(dialog, id, buffer, 512);
        return new string(buffer, 0, (int)length);
    }

    /// <summary>The file name box: the visible <c>Edit</c> inside a <c>ComboBox</c>; 0 when there is none.</summary>
    internal static nint NameBox(nint dialog)
        => Descendants(dialog).FirstOrDefault(h => ClassOf(h) == "Edit" && IsWindowVisible(h) != 0 && ClassOf(GetParent(h)) == "ComboBox");

    /// <summary>The window's text, through <c>WM_GETTEXT</c>.</summary>
    internal static string Text(nint hwnd)
    {
        if (hwnd == 0) return "";
        var buffer = new char[1024];
        nint length;
        fixed (char* b = buffer)
            if (SendMessageTimeoutW(hwnd, 0x000D /* WM_GETTEXT */, buffer.Length, (nint)b, 2, 1000, &length) == 0) return "";
        return new string(buffer, 0, (int)length);
    }

    /// <summary>Replaces the window's text, through <c>WM_SETTEXT</c>, as typing would leave it.</summary>
    internal static bool SetText(nint hwnd, string text)
    {
        nint result;
        fixed (char* t = text)
            return hwnd != 0 && SendMessageTimeoutW(hwnd, 0x000C /* WM_SETTEXT */, 0, (nint)t, 2, 1000, &result) != 0;
    }

    /// <summary>The folder the dialog shows: its breadcrumb toolbar's "Address: " text; null when not found.</summary>
    internal static string? Address(nint dialog)
        => Descendants(dialog).Where(h => ClassOf(h) == "ToolbarWindow32").Select(Text)
            .FirstOrDefault(t => t.StartsWith("Address: ", StringComparison.Ordinal))?["Address: ".Length..];

    /// <summary>The selected file type's text: the visible drop-down list <c>ComboBox</c> without an edit box.</summary>
    internal static string? FileType(nint dialog)
        => Descendants(dialog).Where(h => ClassOf(h) == "ComboBox" && IsWindowVisible(h) != 0 && FindWindowExW(h, 0, "Edit", null) == 0)
            .Select(Text).FirstOrDefault();

    private static string ClassOf(nint hwnd)
    {
        if (hwnd == 0) return "";
        var buffer = stackalloc char[256];
        return new string(buffer, 0, GetClassNameW(hwnd, buffer, 256));
    }

    private static List<nint> Descendants(nint parent)
    {
        var all = new List<nint>();
        for (var child = FindWindowExW(parent, 0, null, null); child != 0; child = FindWindowExW(parent, child, null, null))
        {
            all.Add(child);
            all.AddRange(Descendants(child));
        }
        return all;
    }

    [DllImport("user32.dll")] private static extern int IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern int IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, uint* process);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetDlgItemTextW(nint dialog, int id, char* text, int max);
    [DllImport("user32.dll")] private static extern int GetClassNameW(nint hwnd, char* name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string? cls, string? name);

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
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PointNative point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowRect(nint hwnd, RectNative* rect);
    [DllImport("user32.dll")] private static extern int SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nint extra);

    /// <summary>Sends <paramref name="message"/> and waits for the window procedure to return.</summary>
    internal static void Send(nint hwnd, uint message, nint wParam, nint lParam)
    {
        nint result;
        _ = SendMessageTimeoutW(hwnd, message, wParam, lParam, 2, 2000, &result);
    }

    /// <summary>The window text, through <c>WM_GETTEXT</c>.</summary>
    internal static string GetText(nint hwnd)
    {
        var buffer = stackalloc char[256];
        nint length;
        _ = SendMessageTimeoutW(hwnd, 0x000D /* WM_GETTEXT */, 256, (nint)buffer, 2, 2000, &length);
        return new string(buffer, 0, (int)length);
    }

    /// <summary>The NUL-terminated UTF-16 string at <paramref name="pointer"/>.</summary>
    internal static string? ReadString(nint pointer) => pointer == 0 ? null : new string((char*)pointer);

    /// <summary>Whether the calling thread is the one that owns <paramref name="hwnd"/>.</summary>
    internal static bool OnWindowThread(nint hwnd) => GetWindowThreadProcessId(hwnd, null) == GetCurrentThreadId();

    /// <summary>The window's <c>GWL_STYLE</c>.</summary>
    internal static uint Style(nint hwnd) => (uint)(long)GetWindowLongPtrW(hwnd, -16 /* GWL_STYLE */);

    /// <summary>A 32-bit DWM window attribute, or null if DWM would not say.</summary>
    internal static uint? Dwm(nint hwnd, uint attribute) => OperatingSystem.IsWindows() ? Oriel.Cef.Platform.WindowsWindow.GetDwmAttribute(hwnd, attribute) : null;

    /// <summary>A screen pixel of the window's visible frame (<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>): the caption's middle,
    /// clear of the title and the buttons, or the 1px border at the middle of the bottom edge.</summary>
    internal static (byte R, byte G, byte B) FramePixel(nint hwnd, bool caption)
    {
        RectNative r;
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, &r, (uint)sizeof(RectNative)) < 0) return default;
        return caption ? ScreenPixel(r.Left + (r.Right - r.Left) * 45 / 100, r.Top + 10) : ScreenPixel((r.Left + r.Right) / 2, r.Bottom - 1);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);

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

    /// <summary>Sets the window's <c>GWL_STYLE</c> as another party (Chromium) would.</summary>
    internal static void SetStyle(nint hwnd, uint style) => SetWindowLongPtrW(hwnd, -16, (nint)style);

    /// <summary>Puts the window in or out of the topmost band as another program would; true if <c>WS_EX_TOPMOST</c> agrees.</summary>
    internal static bool SetTopmost(nint hwnd, bool topmost)
    {
        const uint zOrderOnly = 0x0001 | 0x0002 | 0x0010; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        // HWND_TOPMOST alone can be ignored for a window under the foreground one; HWND_NOTOPMOST first lifts it.
        _ = SetWindowPos(hwnd, -2 /* HWND_NOTOPMOST */, 0, 0, 0, 0, zOrderOnly);
        if (topmost) _ = SetWindowPos(hwnd, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, zOrderOnly);
        return ((GetWindowLongPtrW(hwnd, -20) & 0x8 /* WS_EX_TOPMOST */) != 0) == topmost;
    }

    [DllImport("user32.dll")] private static extern int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetClientRect(nint hwnd, int* rect);
    [DllImport("user32.dll")] private static extern int GetWindowRect(nint hwnd, int* rect);
    [DllImport("user32.dll")] private static extern int ClientToScreen(nint hwnd, long* point);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
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
    /// <summary>Oriel's hidden system host window, or 0.</summary>
    internal static nint SystemHost() => FindWindowW("OrielSystemHost", null);

    /// <summary>Sends a message and waits for it.</summary>
    internal static void Send(nint hwnd, uint message, nint wParam)
    {
        nint result;
        _ = SendMessageTimeoutW(hwnd, message, wParam, 0, 2, 2000, &result);
    }

    /// <summary>The system execution state (<c>ES_</c> flags), which active power requests set.</summary>
    internal static uint ExecutionState()
    {
        uint state;
        return CallNtPowerInformation(16 /* SystemExecutionState */, null, 0, &state, 4) == 0 ? state : uint.MaxValue;
    }

    /// <summary>Whether this test can register the hotkey itself; if it can, it lets it go again.</summary>
    internal static bool HotKeyFree(uint modifiers, uint virtualKey)
    {
        if (RegisterHotKey(0, 0xBEEF, modifiers, virtualKey) == 0) return false;
        _ = UnregisterHotKey(0, 0xBEEF);
        return true;
    }

    /// <summary>Presses the keys in order with the real keyboard input, then releases them in reverse.</summary>
    internal static void Chord(params byte[] keys)
    {
        foreach (var key in keys) keybd_event(key, 0, 0, 0);
        Thread.Sleep(50);
        foreach (var key in keys.Reverse()) keybd_event(key, 0, 0x2 /* KEYEVENTF_KEYUP */, 0);
        Thread.Sleep(100);
    }

    /// <summary>Presses the keys, then sends the last key's key-down <paramref name="repeats"/> more times as a held key
    /// does, then releases them in reverse.</summary>
    internal static void Hold(int repeats, params byte[] keys)
    {
        foreach (var key in keys) keybd_event(key, 0, 0, 0);
        for (var i = 0; i < repeats; i++) { Thread.Sleep(30); keybd_event(keys[^1], 0, 0, 0); }
        Thread.Sleep(50);
        foreach (var key in keys.Reverse()) keybd_event(key, 0, 0x2 /* KEYEVENTF_KEYUP */, 0);
        Thread.Sleep(100);
    }

    /// <summary>The entries of <c>powercfg /requests</c> by section (<c>DISPLAY</c>, <c>SYSTEM</c>, <c>EXECUTION</c>, ...);
    /// null when powercfg refuses, which it does without elevation.</summary>
    internal static Dictionary<string, List<string>>? PowerRequests()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powercfg.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("/requests");
        using var process = System.Diagnostics.Process.Start(psi)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) return null;
        var sections = new Dictionary<string, List<string>>();
        List<string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 1 && line.EndsWith(':') && !line.Any(char.IsLower)) sections[line[..^1]] = current = [];
            else if (current is not null && line.Length > 0 && line != "None.") current.Add(line);
        }
        return sections;
    }

    /// <summary>A real mouse move, which counts as user input.</summary>
    internal static void Nudge()
    {
        mouse_event(0x0001 /* MOUSEEVENTF_MOVE */, 1, 0, 0, 0);
        mouse_event(0x0001, -1, 0, 0, 0);
    }

    [DllImport("powrprof.dll")] private static extern uint CallNtPowerInformation(int level, void* input, uint inputLength, uint* output, uint outputLength);
    [DllImport("user32.dll")] private static extern int RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern int UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
    /// <summary>Oriel's hidden tray host window, or 0.</summary>
    internal static nint TrayHost() => FindWindowW("OrielTrayHost", null);

    /// <summary>Sends a version 0 tray callback to the host, as the shell does without <c>NIM_SETVERSION</c>: the icon's id
    /// in wParam, the mouse or <c>NIN_</c> message in lParam.</summary>
    internal static void TrayCallback(nint host, uint id, uint message)
    {
        nint result;
        _ = SendMessageTimeoutW(host, 0x8001, (nint)id, (nint)message, 2, 2000, &result);
    }

    /// <summary>The visible Chromium Views menu of this process: a <c>Chrome_WidgetWin_</c> popup whose owner is hidden
    /// (a tray menu's), or 0.</summary>
    internal static nint ViewsMenuWindow()
    {
        nint found = 0;
        var name = stackalloc char[64];
        for (var hwnd = FindWindowExW(0, 0, null, null); hwnd != 0; hwnd = FindWindowExW(0, hwnd, null, null))
        {
            uint pid;
            _ = GetWindowThreadProcessId(hwnd, &pid);
            if (pid != (uint)Environment.ProcessId || IsWindowVisible(hwnd) == 0) continue;
            var owner = GetWindow(hwnd, 4 /* GW_OWNER */);
            if (owner == 0 || IsWindowVisible(owner) != 0) continue;
            if ((GetWindowLongW(hwnd, -16 /* GWL_STYLE */) & 0x80000000 /* WS_POPUP */) == 0) continue;
            var length = GetClassNameW(hwnd, name, 64);
            if (new string(name, 0, length).StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)) found = hwnd;
        }
        return found;
    }

    /// <summary>Moves the pointer to a point in screen pixels.</summary>
    internal static bool MoveCursor(int x, int y) => SetCursorPos(x, y) != 0;

    /// <summary>The pointer's position in screen pixels.</summary>
    internal static (int X, int Y) CursorPos()
    {
        var p = stackalloc int[2];
        _ = GetCursorPos(p);
        return (p[0], p[1]);
    }

    /// <summary>Sends the host a registered window message by name, such as <c>TaskbarCreated</c>.</summary>
    internal static void SendRegistered(nint hwnd, string name)
    {
        uint message;
        fixed (char* n = name) message = RegisterWindowMessageW(n);
        nint result;
        _ = SendMessageTimeoutW(hwnd, message, 0, 0, 2, 2000, &result);
    }

    /// <summary>Whether the shell holds an icon (host, id, or its GUID), asked by modifying its tooltip; or deletes it.</summary>
    internal static bool NotifyIcon(nint host, uint id, bool delete, Guid? guid = null)
    {
        var data = new NotifyIconData { Size = (uint)sizeof(NotifyIconData), Hwnd = host, Id = id, Flags = delete ? 0u : 0x4u /* NIF_TIP */ };
        if (guid is { } g) (data.Flags, data.Guid) = (data.Flags | 0x20 /* NIF_GUID */, g);
        return Shell_NotifyIconW(delete ? 2u : 1u, &data) != 0;
    }

    /// <summary>The visible popup menu window (<c>#32768</c>), or 0.</summary>
    internal static nint MenuWindow()
    {
        var menu = FindWindowW("#32768", null);
        return menu != 0 && IsWindowVisible(menu) != 0 ? menu : 0;
    }

    /// <summary>The items of the menu a <c>#32768</c> window shows: text (accelerator after a tab) and <c>MF_</c> state.</summary>
    internal static List<(string Text, uint State)> MenuItems(nint menuWindow)
    {
        var items = new List<(string, uint)>();
        nint result;
        if (SendMessageTimeoutW(menuWindow, 0x01E1 /* MN_GETHMENU */, 0, 0, 2, 1000, &result) == 0 || result == 0) return items;
        var count = GetMenuItemCount(result);
        var buffer = stackalloc char[256];
        for (var i = 0; i < count; i++)
        {
            var length = GetMenuStringW(result, (uint)i, buffer, 256, 0x400 /* MF_BYPOSITION */);
            items.Add((new string(buffer, 0, Math.Max(0, length)), GetMenuState(result, (uint)i, 0x400)));
        }
        return items;
    }

    /// <summary>Posts a key to a window, as a menu's loop reads keys from its thread's queue.</summary>
    internal static void PostKey(nint hwnd, int virtualKey)
    {
        _ = PostMessageW(hwnd, 0x0100 /* WM_KEYDOWN */, virtualKey, 0);
        _ = PostMessageW(hwnd, 0x0101 /* WM_KEYUP */, virtualKey, unchecked((nint)0xC0000001));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Hwnd;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        public fixed char Tip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint Version;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconW(uint message, NotifyIconData* data);
    [DllImport("user32.dll")] private static extern int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int GetCursorPos(int* point);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll")] private static extern int GetMenuStringW(nint menu, uint item, char* text, int max, uint flags);
    [DllImport("user32.dll")] private static extern uint GetMenuState(nint menu, uint item, uint flags);

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

    /// <summary>The process's explicit AppUserModelID straight from shell32, or null when none is set.</summary>
    internal static string? ProcessAppUserModelId()
    {
        char* id;
        if (GetCurrentProcessExplicitAppUserModelID(&id) < 0) return null;
        var s = new string(id);
        Marshal.FreeCoTaskMem((nint)id);
        return s;
    }

    /// <summary>Does what Windows does when a toast is pressed: activates <paramref name="clsid"/> as a local server and
    /// calls its <c>INotificationActivationCallback.Activate</c>. Returns the HRESULT of the first call that failed, or 0.</summary>
    internal static int ActivateToast(Guid clsid, string appUserModelId, string arguments, params (string Key, string Value)[] inputs)
    {
        var iid = new Guid("53E31837-6600-4A81-9395-75CFFE746F94");
        void* callback;
        var hr = CoCreateInstance(&clsid, null, 4 /* CLSCTX_LOCAL_SERVER */, &iid, &callback);
        if (hr < 0) return hr;
        var strings = new List<nint>();
        try
        {
            var data = stackalloc nint[Math.Max(1, inputs.Length * 2)];
            for (var i = 0; i < inputs.Length; i++)
            {
                strings.Add(data[2 * i] = Marshal.StringToCoTaskMemUni(inputs[i].Key));
                strings.Add(data[2 * i + 1] = Marshal.StringToCoTaskMemUni(inputs[i].Value));
            }
            fixed (char* aumid = appUserModelId)
            fixed (char* args = arguments)
                hr = ((delegate* unmanaged<void*, char*, char*, nint*, uint, int>)(*(void***)callback)[3])(callback, aumid, args, data, (uint)inputs.Length);
            return hr;
        }
        finally
        {
            foreach (var s in strings) Marshal.FreeCoTaskMem(s);
            ((delegate* unmanaged<void*, uint>)(*(void***)callback)[2])(callback);
        }
    }

    [DllImport("shell32.dll")] private static extern int GetCurrentProcessExplicitAppUserModelID(char** id);

    /// <summary>Whether a contrast theme is on, from <c>SPI_GETHIGHCONTRAST</c>; and the registry's copy of the flags.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static (bool Api, bool Registry) HighContrastOn()
    {
        // HIGHCONTRASTW: cbSize, dwFlags, lpszDefaultScheme.
        var info = stackalloc byte[16];
        *(uint*)info = 16;
        var api = SystemParametersInfoW(0x0042, 16, info, 0) != 0 && (((uint*)info)[1] & 1) != 0;
        var flags = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Accessibility\HighContrast", "Flags", "0") as string;
        return (api, uint.TryParse(flags, out var f) && (f & 1) != 0);
    }

    /// <summary>A window's process, class, visibility and whether its parent is the desktop (so it gets broadcasts).</summary>
    internal static (int ProcessId, string ClassName, bool Visible, bool TopLevel) WindowFacts(nint hwnd)
    {
        uint pid = 0;
        _ = GetWindowThreadProcessId(hwnd, &pid);
        var name = stackalloc char[256];
        var length = GetClassNameW(hwnd, name, 256);
        return ((int)pid, new string(name, 0, Math.Max(length, 0)), IsWindowVisible(hwnd) != 0, GetAncestor(hwnd, 1 /* GA_PARENT */) == GetDesktopWindow());
    }

    /// <summary>DWM's immersive dark mode attribute (20) as <c>DwmGetWindowAttribute</c> reads it back; null if it fails.</summary>
    internal static bool? ImmersiveDarkMode(nint hwnd)
    {
        int value = 0;
        return DwmGetWindowAttribute(hwnd, 20, &value, sizeof(int)) >= 0 ? value != 0 : null;
    }

    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();

    /// <summary>The icon <c>LoadImage(IMAGE_ICON, size, size, LR_LOADFROMFILE)</c> makes of <paramref name="path"/>, as
    /// straight BGRA with the AND mask applied to icons without alpha; null if it cannot be loaded.</summary>
    internal static (int Width, int Height, byte[] Bgra)? LoadIcon(string path, int size)
    {
        var icon = LoadImageW(0, path, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        if (icon == 0) return null;
        IconInfo info;
        try
        {
            if (GetIconInfo(icon, &info) == 0 || info.Color == 0) return null;
        }
        finally { _ = DestroyIcon(icon); }
        try
        {
            BitmapHeader bitmap;
            if (GetObjectW(info.Color, sizeof(BitmapHeader), &bitmap) == 0) return null;
            var (w, h) = (bitmap.Width, bitmap.Height);
            var colour = Bits(info.Color, w, h);
            var mask = Bits(info.Mask, w, h);
            if (colour is null || mask is null) return null;
            var anyAlpha = false;
            for (var i = 3; i < colour.Length; i += 4) anyAlpha |= colour[i] != 0;
            if (!anyAlpha)
                for (var i = 0; i < colour.Length; i += 4) colour[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0;
            return (w, h, colour);
        }
        finally
        {
            _ = DeleteObject(info.Color);
            if (info.Mask != 0) _ = DeleteObject(info.Mask);
        }

        static byte[]? Bits(nint bmp, int w, int h)
        {
            var header = new BitmapInfoHeader { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
            var pixels = new byte[w * h * 4];
            var dc = GetDC(0);
            try
            {
                fixed (byte* data = pixels)
                    return GetDIBits(dc, bmp, 0, (uint)h, data, &header, 0) == 0 ? null : pixels;
            }
            finally { _ = ReleaseDC(0, dc); }
        }
    }

    /// <summary>The composited screen pixel at (<paramref name="x"/>, <paramref name="y"/>) DIPs into the window's client area.</summary>
    internal static (byte R, byte G, byte B) ClientPixel(nint hwnd, int x, int y)
    {
        long origin = 0;
        _ = ClientToScreen(hwnd, &origin);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var dc = GetDC(0);
        var c = GetPixel(dc, (int)origin + (int)(x * scale), (int)(origin >> 32) + (int)(y * scale));
        _ = ReleaseDC(0, dc);
        return ((byte)c, (byte)(c >> 8), (byte)(c >> 16));
    }

    /// <summary>Records every window that becomes the foreground window (<c>EVENT_SYSTEM_FOREGROUND</c>) until disposed,
    /// however briefly: a window handing activation straight back is still seen.</summary>
    internal sealed class ForegroundWatch : IDisposable
    {
        private static readonly ConcurrentQueue<(nint Hwnd, int ProcessId)> s_seen = new();
        private readonly Thread _thread;
        private uint _threadId;

        internal ForegroundWatch()
        {
            s_seen.Clear();
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                var hook = SetWinEventHook(3 /* EVENT_SYSTEM_FOREGROUND */, 3, 0, &OnEvent, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
                ready.Set();
                Msg msg;
                while (GetMessageW(&msg, 0, 0, 0) > 0) DispatchMessageW(&msg);
                _ = UnhookWinEvent(hook);
            }) { IsBackground = true, Name = "foreground watch" };
            _thread.Start();
            ready.Wait();
        }

        internal IReadOnlyList<(nint Hwnd, int ProcessId)> Seen => [.. s_seen];

        public void Dispose()
        {
            _ = PostThreadMessageW(_threadId, 0x0012 /* WM_QUIT */, 0, 0);
            _thread.Join(2000);
        }

        [UnmanagedCallersOnly]
        // The owner is read now: a window that is already gone by the time the test looks has no process.
        private static void OnEvent(nint hook, uint e, nint hwnd, int obj, int child, uint thread, uint time) => s_seen.Enqueue((hwnd, ProcessOf(hwnd)));
    }

    /// <summary>The process that owns <paramref name="hwnd"/>.</summary>
    internal static int ProcessOf(nint hwnd)
    {
        uint pid;
        _ = GetWindowThreadProcessId(hwnd, &pid);
        return (int)pid;
    }

    [DllImport("user32.dll")] private static extern int SystemParametersInfoW(uint action, uint param, void* data, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImageW(nint instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern int UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern int PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);
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
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result);
}
