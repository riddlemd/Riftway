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

    /// <summary>The names of every element under <paramref name="hwnd"/>, as UI Automation reports them.</summary>
    internal static List<string> ElementNames(nint hwnd)
    {
        var names = new List<string>();
        Walk(hwnd, (_, name) => names.Add(name));
        return names;
    }

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
