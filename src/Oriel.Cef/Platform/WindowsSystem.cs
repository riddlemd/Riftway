using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>System-wide hooks on Windows: global hotkeys, power and session notifications, idle time, power status and
/// power requests. UI thread only for the window-bound calls; the host window belongs to that thread.</summary>
/// <remarks>The host is a hidden top-level window: <c>WM_POWERBROADCAST</c> does not reach message-only windows. Like
/// Chromium's power window it is <c>WS_POPUP</c> and <c>WS_EX_NOACTIVATE</c>, registered for suspend and resume.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsSystem
{
    private const uint WmHotKey = 0x0312, WmPowerBroadcast = 0x0218, WmWtsSessionChange = 0x02B1;

    private static nint s_host;
    private static Action<int>? s_hotKey;
    private static Action<uint>? s_power;
    private static Action<uint>? s_session;

    /// <summary>Creates the host window if needed and routes its notifications: hotkey ids, <c>PBT_</c> power events and
    /// <c>WTS_</c> session events. Returns the window.</summary>
    public static nint Start(Action<int> hotKey, Action<uint> power, Action<uint> session)
    {
        (s_hotKey, s_power, s_session) = (hotKey, power, session);
        if (s_host != 0) return s_host;
        fixed (char* name = "OrielSystemHost")
        {
            var cls = new WndClass { WndProc = &WndProc, Instance = GetModuleHandleW(null), ClassName = name };
            RegisterClassW(&cls);
            s_host = CreateWindowExW(0x08000000 /* WS_EX_NOACTIVATE */, name, name, 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, cls.Instance, 0);
        }
        // Modern Standby machines send PBT_APMSUSPEND only to windows registered for it.
        RegisterSuspendResumeNotification(s_host, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        // Lock and unlock of this session only.
        WTSRegisterSessionNotification(s_host, 0 /* NOTIFY_FOR_THIS_SESSION */);
        return s_host;
    }

    /// <summary>Registers a system-wide hotkey; <paramref name="modifiers"/> are <c>MOD_</c> flags. False if another app (or
    /// this one) holds the combination.</summary>
    public static bool RegisterHotKey(int id, uint modifiers, uint virtualKey) => RegisterHotKeyNative(s_host, id, modifiers, virtualKey) != 0;

    /// <summary>Releases a hotkey registered with <see cref="RegisterHotKey"/>.</summary>
    public static bool UnregisterHotKey(int id) => UnregisterHotKeyNative(s_host, id) != 0;

    /// <summary>Seconds since the last keyboard or mouse input in this session.</summary>
    public static int IdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)sizeof(LastInputInfo) };
        return GetLastInputInfo(&info) != 0 ? (int)((uint)Environment.TickCount - info.Time) / 1000 : 0;
    }

    /// <summary>Whether the workstation is locked: Chromium's test, the input desktop is not the default one or cannot be
    /// opened.</summary>
    public static bool IsLocked()
    {
        var desktop = OpenInputDesktop(0, 0, 0x0001 /* DESKTOP_READOBJECTS */);
        if (desktop == 0) return true;
        try
        {
            var name = stackalloc char[64];
            uint needed;
            return GetUserObjectInformationW(desktop, 2 /* UOI_NAME */, name, 128, &needed) == 0 || !new ReadOnlySpan<char>(name, 64).TrimEnd('\0').SequenceEqual("Default");
        }
        finally { CloseDesktop(desktop); }
    }

    /// <summary>Whether the machine runs on battery: <c>ACLineStatus</c> 0, as Chromium's <c>GetBatteryPowerStatus</c> reads it
    /// (an unknown line status, 255, counts as external power); null when the call fails.</summary>
    public static bool? OnBattery()
    {
        PowerStatus status;
        if (GetSystemPowerStatus(&status) == 0) return null;
        return status.AcLineStatus == 0;
    }

    /// <summary>The CPU idleness percentage from <c>SystemPowerInformation</c>; null if the call fails.</summary>
    public static int? CpuIdleness()
    {
        SystemPowerInformation info;
        return CallNtPowerInformation(12 /* SystemPowerInformation */, null, 0, &info, (uint)sizeof(SystemPowerInformation)) >= 0 ? (int)info.Idleness : null;
    }

    /// <summary>Each logical CPU's <c>MaxIdleState</c> and <c>CurrentIdleState</c> from <c>ProcessorInformation</c>; null if the
    /// call fails.</summary>
    public static (uint Max, uint Current)[]? ProcessorIdleStates()
    {
        var count = (int)GetActiveProcessorCount(0xFFFF /* ALL_PROCESSOR_GROUPS */);
        var info = new ProcessorPowerInformation[count];
        fixed (ProcessorPowerInformation* p = info)
            if (CallNtPowerInformation(11 /* ProcessorInformation */, null, 0, p, (uint)(sizeof(ProcessorPowerInformation) * count)) < 0) return null;
        var states = new (uint, uint)[count];
        for (var i = 0; i < count; i++) states[i] = (info[i].MaxIdleState, info[i].CurrentIdleState);
        return states;
    }

    /// <summary>Starts a power request: <paramref name="type"/> is a <c>POWER_REQUEST_TYPE</c> (0 display, 1 system, 3
    /// execution); returns its handle, or 0 if Windows refused.</summary>
    public static nint StartPowerRequest(int type, string reason)
    {
        fixed (char* r = reason)
        {
            var context = new ReasonContext { Version = 0 /* POWER_REQUEST_CONTEXT_VERSION */, Flags = 0x1 /* POWER_REQUEST_CONTEXT_SIMPLE_STRING */, Reason = r };
            var request = PowerCreateRequest(&context);
            if (request == -1) return 0;
            if (PowerSetRequest(request, type) != 0) return request;
            CloseHandle(request);
            return 0;
        }
    }

    /// <summary>Ends a power request from <see cref="StartPowerRequest"/>.</summary>
    public static void StopPowerRequest(nint request, int type)
    {
        PowerClearRequest(request, type);
        CloseHandle(request);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WmHotKey:
                    s_hotKey?.Invoke((int)wParam);
                    return 0;
                case WmPowerBroadcast:
                    s_power?.Invoke((uint)wParam);
                    return 1; // TRUE: grant any request
                case WmWtsSessionChange:
                    s_session?.Invoke((uint)wParam);
                    return 0;
            }
        }
        // An exception must not cross into the window procedure.
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] A system notification handler threw: {e}"); }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerInformation
    {
        public uint MaxIdlenessAllowed;
        public uint Idleness;
        public uint TimeRemaining;
        public byte CoolingMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public char* Reason;
        // The detailed form's other union members are not used.
        public nint Unused1;
        public nint Unused2;
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

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey")] private static extern int RegisterHotKeyNative(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey")] private static extern int UnregisterHotKeyNative(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern int GetLastInputInfo(LastInputInfo* info);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, int inherit, uint access);
    [DllImport("user32.dll")] private static extern int CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern int GetUserObjectInformationW(nint obj, int index, void* info, uint length, uint* needed);
    [DllImport("kernel32.dll")] private static extern int GetSystemPowerStatus(PowerStatus* status);
    [DllImport("kernel32.dll")] private static extern nint PowerCreateRequest(ReasonContext* context);
    [DllImport("kernel32.dll")] private static extern int PowerSetRequest(nint request, int type);
    [DllImport("kernel32.dll")] private static extern int PowerClearRequest(nint request, int type);
    [DllImport("powrprof.dll")] private static extern int CallNtPowerInformation(int level, void* input, uint inputLength, void* output, uint outputLength);
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("user32.dll")] private static extern nint RegisterSuspendResumeNotification(nint recipient, uint flags);
    [DllImport("kernel32.dll")] private static extern int CloseHandle(nint handle);
    [DllImport("wtsapi32.dll")] private static extern int WTSRegisterSessionNotification(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern ushort RegisterClassW(WndClass* cls);
    [DllImport("user32.dll")] private static extern nint CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(char* name);
}
