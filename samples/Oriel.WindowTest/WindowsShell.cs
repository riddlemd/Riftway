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
