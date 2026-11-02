using System.Runtime.InteropServices;

/// <summary>Reads monitors back from Windows on the test's side, without Riftway: GDI, DisplayConfig and WMI. Windows only.</summary>
static unsafe class WindowsDisplayProbe
{
    internal sealed record Monitor(string Device, int Left, int Top, int Right, int Bottom, int Frequency, string? EdidName, uint? OutputTechnology, string? IccPath);

    /// <summary>The monitor under a point in physical pixels.</summary>
    internal static Monitor At(int x, int y)
    {
        var handle = MonitorFromPoint(new Pt { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        _ = GetMonitorInfoW(handle, &info);
        var device = new string(info.Device);
        var mode = new byte[220];
        int frequency;
        fixed (byte* m = mode)
        {
            *(ushort*)(m + 68) = 220; // dmSize
            frequency = EnumDisplaySettingsW(info.Device, -1, m) != 0 ? *(int*)(m + 184) : 0; // dmDisplayFrequency
        }
        var (name, technology) = TargetFor(device);
        return new Monitor(device, info.Left, info.Top, info.Right, info.Bottom, frequency, name, technology, IccPath(info.Device));
    }

    // QDC_ONLY_ACTIVE_PATHS, then the path whose source GDI name is the device; its target's EDID name and output technology.
    private static (string? Name, uint? Technology) TargetFor(string device)
    {
        uint paths, modes;
        if (GetDisplayConfigBufferSizes(2, &paths, &modes) != 0) return (null, null);
        var pathData = new byte[paths * 72];
        var modeData = new byte[modes * 64];
        var source = stackalloc byte[84];
        var target = stackalloc byte[420];
        fixed (byte* p = pathData)
        fixed (byte* m = modeData)
        {
            if (QueryDisplayConfig(2, &paths, p, &modes, m, null) != 0) return (null, null);
            for (var i = 0; i < paths; i++)
            {
                var path = p + 72 * i;
                new Span<byte>(source, 84).Clear();
                *(int*)source = 1; *(int*)(source + 4) = 84; // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                *(long*)(source + 8) = *(long*)path; *(uint*)(source + 16) = *(uint*)(path + 8);
                if (DisplayConfigGetDeviceInfo(source) != 0 || new string((char*)(source + 20)) != device) continue;
                new Span<byte>(target, 420).Clear();
                *(int*)target = 2; *(int*)(target + 4) = 420; // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
                *(long*)(target + 8) = *(long*)(path + 20); *(uint*)(target + 16) = *(uint*)(path + 28);
                if (DisplayConfigGetDeviceInfo(target) != 0) return (null, *(uint*)(path + 36));
                var fromEdid = (*(uint*)(target + 20) & 1) != 0;
                return (fromEdid ? new string((char*)(target + 36)) : "", *(uint*)(path + 36));
            }
        }
        return (null, null);
    }

    private static string? IccPath(char* device)
    {
        var dc = CreateDCW(device, null, null, null);
        if (dc == 0) return null;
        var path = stackalloc char[261];
        uint length = 260;
        var found = GetICMProfileW(dc, &length, path) != 0;
        _ = DeleteDC(dc);
        return found ? new string(path) : null;
    }

    /// <summary>Runs a WMI query through PowerShell and returns its output lines.</summary>
    internal static string[] Wmi(string command)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{command}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(20000);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Riftway's display observer window in this process, or 0.</summary>
    internal static nint ObserverWindow()
    {
        for (nint hwnd = 0; (hwnd = FindWindowExW(0, hwnd, "RiftwayDisplayObserver", null)) != 0;)
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId) return hwnd;
        }
        return 0;
    }

    /// <summary>Whether the window is a top-level window (its parent is the desktop), not message-only.</summary>
    internal static bool IsTopLevel(nint hwnd) => GetAncestor(hwnd, 1 /* GA_PARENT */) == GetDesktopWindow();

    internal static bool IsVisible(nint hwnd) => IsWindowVisible(hwnd);

    /// <summary>Sends a message and waits for it to be handled.</summary>
    internal static void Send(nint hwnd, uint message, nint wParam, nint lParam)
    {
        nint result;
        _ = SendMessageTimeoutW(hwnd, message, wParam, lParam, 2 /* SMTO_ABORTIFHUNG */, 2000, &result);
    }

    /// <summary>The primary work area in physical pixels.</summary>
    internal static (int Left, int Top, int Right, int Bottom) WorkArea()
    {
        var rect = stackalloc int[4];
        _ = SystemParametersInfoW(0x0030 /* SPI_GETWORKAREA */, 0, rect, 0);
        return (rect[0], rect[1], rect[2], rect[3]);
    }

    /// <summary>Registers an app bar along the primary display's top edge, <paramref name="height"/> pixels tall, so
    /// Explorer shrinks the work area and broadcasts <c>WM_SETTINGCHANGE</c>; disposing removes it. A work area set with
    /// <c>SPI_SETWORKAREA</c> would not do: Explorer puts its own back during the broadcast.</summary>
    internal static IDisposable ReserveTopEdge(int width, int height)
    {
        nint hwnd = 0;
        uint thread = 0;
        using var ready = new ManualResetEventSlim();
        new Thread(() =>
        {
            thread = GetCurrentThreadId();
            hwnd = CreateWindowExW(0x80 /* WS_EX_TOOLWINDOW */, "Static", "Riftway app bar", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, 0, 0);
            ready.Set();
            Msg msg;
            while (GetMessageW(&msg, 0, 0, 0) > 0) DispatchMessageW(&msg);
            _ = DestroyWindow(hwnd);
        }) { IsBackground = true, Name = "app bar" }.Start();
        ready.Wait();
        var data = new AppBarData { Size = (uint)sizeof(AppBarData), Hwnd = hwnd, CallbackMessage = 0x8005, Edge = 1 /* ABE_TOP */, Right = width, Bottom = height };
        _ = SHAppBarMessage(0 /* ABM_NEW */, &data);
        _ = SHAppBarMessage(2 /* ABM_QUERYPOS */, &data);
        data.Bottom = data.Top + height;
        _ = SHAppBarMessage(3 /* ABM_SETPOS */, &data);
        return new AppBar(data, thread);
    }

    private sealed class AppBar(AppBarData data, uint thread) : IDisposable
    {
        public void Dispose()
        {
            var copy = data;
            _ = SHAppBarMessage(1 /* ABM_REMOVE */, &copy);
            _ = PostThreadMessageW(thread, 0x0012 /* WM_QUIT */, 0, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Hwnd;
        public uint CallbackMessage;
        public uint Edge;
        public int Left, Top, Right, Bottom;
        public nint LParam;
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

    [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint message, AppBarData* data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern int GetMessageW(Msg* msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(Msg* msg);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint thread, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct Pt
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public int Left, Top, Right, Bottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        public fixed char Device[32];
    }

    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Pt point, uint flags);
    [DllImport("user32.dll")] private static extern int GetMonitorInfoW(nint monitor, MonitorInfoEx* info);
    [DllImport("user32.dll")] private static extern int EnumDisplaySettingsW(char* device, int mode, byte* devMode);
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, uint* paths, uint* modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, uint* paths, byte* pathData, uint* modes, byte* modeData, void* topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(byte* header);
    [DllImport("gdi32.dll")] private static extern nint CreateDCW(char* driver, char* device, char* output, void* init);
    [DllImport("gdi32.dll")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int GetICMProfileW(nint dc, uint* length, char* path);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowExW(nint parent, nint after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, nint* result);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint action, uint param, void* data, uint winIni);
}
