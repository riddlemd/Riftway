using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>The Windows sources behind Electron's <c>systemPreferences</c>: system colours, the accent colour, animation
/// settings, device access, a hidden window for colour broadcasts and a registry watch for accent changes.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsSystemPreferences
{
    /// <summary>The DWM key Chromium's <c>AccentColorObserver</c> reads and watches, under HKEY_CURRENT_USER.</summary>
    public const string DwmKey = @"SOFTWARE\Microsoft\Windows\DWM";

    /// <summary>The class of the hidden window that hears colour broadcasts.</summary>
    public const string WindowClass = "RiftwaySystemPreferences";

    private const string UISettingsClass = "Windows.UI.ViewManagement.UISettings";
    private const string DeviceAccessClass = "Windows.Devices.Enumeration.DeviceAccessInformation";
    private static readonly Guid s_iidUISettings3 = new("03021BE4-5254-4781-8194-5168F7D06D7B");
    private static readonly Guid s_iidDeviceAccessStatics = new("574BD3D3-5F30-45CD-8A94-724FE5973084");
    private const int UIColorTypeAccent = 5;
    private const int DeviceAccessAllowed = 1;

    private static readonly Lock s_mtaGate = new();
    private static bool s_mtaJoined;
    private static Action<uint, nint, nint>? s_onMessage;
    private static nint s_window;

    /// <summary><c>GetSysColor</c>: a COLORREF (0x00BBGGRR).</summary>
    public static uint SysColor(int index) => GetSysColor(index);

    /// <summary>The accent colour as an opaque ARGB value, as Chromium's <c>AccentColorObserver::UpdateAccentColors</c>
    /// finds it: <c>UISettings</c> first, then the DWM key's <c>AccentColor</c>; null when neither has one.</summary>
    public static uint? AccentColor() => UISettingsAccent() ?? (ReadDword(DwmKey, "AccentColor") is { } c ? ColorRefToArgb(c) : null);

    // TryGetAccentColorFromUISettings: the coerced colour Windows actually shows, not the raw registry value.
    private static uint? UISettingsAccent()
    {
        JoinMta();
        void* settings = null, settings3 = null;
        try
        {
            if (Activate(UISettingsClass, &settings) < 0 || settings is null) return null;
            var iid = s_iidUISettings3;
            if (((delegate* unmanaged<void*, Guid*, void**, int>)Slot(settings, 0))(settings, &iid, &settings3) < 0 || settings3 is null) return null;
            // Windows.UI.Color is { A, R, G, B }.
            uint color;
            if (((delegate* unmanaged<void*, int, uint*, int>)Slot(settings3, 6))(settings3, UIColorTypeAccent, &color) < 0) return null;
            var bytes = (byte*)&color;
            return 0xFF000000u | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }
        finally
        {
            Release(settings3);
            Release(settings);
        }
    }

    /// <summary><c>SPI_GETCLIENTAREAANIMATION</c> ("Animation effects"), or null if the call fails.</summary>
    public static bool? ClientAreaAnimation()
    {
        int enabled = 1;
        return SystemParametersInfoW(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, &enabled, 0) != 0 ? enabled != 0 : null;
    }

    /// <summary>Chromium's <c>base::win::IsCurrentSessionRemote</c>.</summary>
    public static bool IsCurrentSessionRemote()
    {
        if (GetSystemMetrics(0x1000 /* SM_REMOTESESSION */) != 0) return true;
        uint session;
        if (ProcessIdToSessionId(GetCurrentProcessId(), &session) == 0) return false;
        return ReadDword(@"SYSTEM\CurrentControlSet\Control\Terminal Server", "GlassSessionId", localMachine: true) is { } glass && session != glass;
    }

    /// <summary>Electron's <c>GetDeviceAccessStatus</c>: the WinRT <c>DeviceAccessStatus</c> for a <c>DeviceClass</c>
    /// (1 audio capture, 4 video capture). Allowed (1) when WinRT can't answer, as Electron assumes.</summary>
    public static int DeviceAccessStatus(int deviceClass)
    {
        JoinMta();
        void* statics = null, info = null;
        try
        {
            if (Factory(DeviceAccessClass, s_iidDeviceAccessStatics, &statics) < 0 || statics is null) return DeviceAccessAllowed;
            // Slot 8: CreateFromDeviceClass, after CreateFromId and CreateFromDeviceClassId.
            if (((delegate* unmanaged<void*, int, void**, int>)Slot(statics, 8))(statics, deviceClass, &info) < 0 || info is null) return DeviceAccessAllowed;
            var status = 0;
            // Slot 8: get_CurrentStatus, after add_ and remove_AccessChanged. A failure leaves Unspecified, as in Electron.
            _ = ((delegate* unmanaged<void*, int*, int>)Slot(info, 8))(info, &status);
            return status;
        }
        finally
        {
            Release(info);
            Release(statics);
        }
    }

    /// <summary>Creates the hidden top-level window and calls <paramref name="onMessage"/> with every message it gets.
    /// The window of Chromium's <c>gfx::SingletonHwnd</c>. Call once, on the thread that pumps messages.</summary>
    /// <remarks>Top-level, not message-only: message-only windows miss broadcasts such as <c>WM_SYSCOLORCHANGE</c>.</remarks>
    public static nint CreateWindow(Action<uint, nint, nint> onMessage)
    {
        if (s_window != 0) return s_window;
        s_onMessage = onMessage;
        fixed (char* name = WindowClass)
        {
            var instance = GetModuleHandleW(null);
            var cls = new WndClass { WndProc = &WndProc, Instance = instance, ClassName = name };
            _ = RegisterClassW(&cls);
            // WindowImpl's default style for a parentless window, never shown.
            s_window = CreateWindowExW(0, name, name, 0x00CF0000 /* WS_OVERLAPPEDWINDOW */ | 0x02000000 /* WS_CLIPCHILDREN */,
                unchecked((int)0x80000000), unchecked((int)0x80000000), unchecked((int)0x80000000), unchecked((int)0x80000000), 0, 0, instance, 0);
        }
        return s_window;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try { s_onMessage?.Invoke(message, wParam, lParam); }
        // An exception must not cross into the window procedure.
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] A systemPreferences handler threw: {e}"); }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>Calls <paramref name="onChange"/> on a pool thread each time anything under HKEY_CURRENT_USER\<paramref name="subkey"/>
    /// changes, re-arming after each call as <c>base::win::RegKey::StartWatching</c> users do. Null if the key can't be watched.</summary>
    public static IDisposable? WatchKey(string subkey, Action onChange)
    {
        nint key;
        fixed (char* name = subkey)
            if (RegOpenKeyExW(HkeyCurrentUser, name, 0, 0x20019 /* KEY_READ */, &key) != 0) return null;
        var watch = new KeyWatch(key, onChange);
        if (watch.Arm()) return watch;
        watch.Dispose();
        return null;
    }

    private sealed class KeyWatch(nint key, Action onChange) : IDisposable
    {
        private readonly AutoResetEvent _signal = new(false);
        private RegisteredWaitHandle? _wait;
        private bool _disposed;

        internal bool Arm()
        {
            lock (_signal)
            {
                if (_disposed) return false;
                // base::win::RegKey::Watcher's filter: names, attributes, values and security, the whole subtree.
                const uint filter = 0x1 | 0x2 | 0x4 | 0x8 | 0x10000000 /* REG_NOTIFY_THREAD_AGNOSTIC */;
                if (RegNotifyChangeKeyValue(key, 1, filter, _signal.SafeWaitHandle.DangerousGetHandle(), 1) != 0) return false;
                _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => Fired(), null, Timeout.Infinite, executeOnlyOnce: true);
                return true;
            }
        }

        private void Fired()
        {
            try { onChange(); }
            catch (Exception e) { Console.Error.WriteLine($"[Riftway] A registry watch handler threw: {e}"); }
            _ = Arm();
        }

        public void Dispose()
        {
            lock (_signal)
            {
                if (_disposed) return;
                _disposed = true;
                _wait?.Unregister(null);
                _ = RegCloseKey(key);
                _signal.Dispose();
            }
        }
    }

    /// <summary>A DWORD value, or null when it is missing or not a DWORD.</summary>
    public static uint? ReadDword(string subkey, string value, bool localMachine = false)
    {
        uint data, size = 4;
        fixed (char* k = subkey)
        fixed (char* v = value)
            return RegGetValueW(localMachine ? HkeyLocalMachine : HkeyCurrentUser, k, v, 0x10 /* RRF_RT_REG_DWORD */, null, &data, &size) == 0 ? data : null;
    }

    /// <summary>skia's <c>COLORREFToSkColor</c>: 0x00BBGGRR to opaque 0xAARRGGBB.</summary>
    public static uint ColorRefToArgb(uint colorRef) =>
        0xFF000000u | ((colorRef & 0xFF) << 16) | (colorRef & 0xFF00) | ((colorRef >> 16) & 0xFF);

    // The implicit MTA serves threads that never initialized COM; WinRT activation needs an apartment.
    private static void JoinMta()
    {
        lock (s_mtaGate)
        {
            if (s_mtaJoined) return;
            nint cookie;
            s_mtaJoined = CoIncrementMTAUsage(&cookie) >= 0;
        }
    }

    private static int Activate(string className, void** instance)
    {
        nint hstring;
        fixed (char* c = className)
            if (WindowsCreateString(c, (uint)className.Length, &hstring) < 0) return -1;
        try { return RoActivateInstance(hstring, instance); }
        finally { _ = WindowsDeleteString(hstring); }
    }

    private static int Factory(string className, Guid iid, void** factory)
    {
        nint hstring;
        fixed (char* c = className)
            if (WindowsCreateString(c, (uint)className.Length, &hstring) < 0) return -1;
        try { return RoGetActivationFactory(hstring, &iid, factory); }
        finally { _ = WindowsDeleteString(hstring); }
    }

    private static void* Slot(void* obj, int index) => (*(void***)obj)[index];

    private static void Release(void* obj)
    {
        if (obj is not null) _ = ((delegate* unmanaged<void*, uint>)Slot(obj, 2))(obj);
    }

    private static readonly nint HkeyCurrentUser = unchecked((nint)0x80000001);
    private static readonly nint HkeyLocalMachine = unchecked((nint)0x80000002);

    private struct WndClass
    {
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClsExtra;
        public int WndExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
    }

    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern int SystemParametersInfoW(uint action, uint param, void* value, uint winIni);
    [DllImport("user32.dll")] private static extern ushort RegisterClassW(WndClass* cls);
    [DllImport("user32.dll")] private static extern nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(char* name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")] private static extern int ProcessIdToSessionId(uint processId, uint* sessionId);
    [DllImport("advapi32.dll")] private static extern int RegOpenKeyExW(nint key, char* subkey, uint options, uint access, nint* result);
    [DllImport("advapi32.dll")] private static extern int RegCloseKey(nint key);
    [DllImport("advapi32.dll")] private static extern int RegNotifyChangeKeyValue(nint key, int watchSubtree, uint filter, nint evt, int asynchronous);
    [DllImport("advapi32.dll")] private static extern int RegGetValueW(nint key, char* subkey, char* value, uint flags, uint* type, void* data, uint* size);
    [DllImport("combase.dll")] private static extern int WindowsCreateString(char* source, uint length, nint* hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")] private static extern int RoActivateInstance(nint classId, void** instance);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, Guid* iid, void** factory);
    [DllImport("combase.dll")] private static extern int CoIncrementMTAUsage(nint* cookie);
}
