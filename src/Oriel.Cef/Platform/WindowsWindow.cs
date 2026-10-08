using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>A window's App User Model properties; the values are the <c>PKEY_AppUserModel_*</c> property ids.</summary>
public enum AppUserModelProperty
{
    /// <summary><c>PKEY_AppUserModel_RelaunchCommand</c>.</summary>
    RelaunchCommand = 2,

    /// <summary><c>PKEY_AppUserModel_RelaunchIconResource</c>: <c>path,index</c>.</summary>
    RelaunchIconResource = 3,

    /// <summary><c>PKEY_AppUserModel_RelaunchDisplayNameResource</c>.</summary>
    RelaunchDisplayNameResource = 4,

    /// <summary><c>PKEY_AppUserModel_ID</c>.</summary>
    Id = 5,
}

/// <summary>What DWM draws behind a window; the values are Win32's <c>DWMSBT_</c> constants.</summary>
public enum WindowsBackdrop
{
    /// <summary>DWM decides; <c>DWMSBT_AUTO</c>.</summary>
    Auto = 0,

    /// <summary>No backdrop; <c>DWMSBT_NONE</c>.</summary>
    None = 1,

    /// <summary>Mica; <c>DWMSBT_MAINWINDOW</c>.</summary>
    MainWindow = 2,

    /// <summary>Acrylic; <c>DWMSBT_TRANSIENTWINDOW</c>.</summary>
    TransientWindow = 3,

    /// <summary>Mica Alt; <c>DWMSBT_TABBEDWINDOW</c>.</summary>
    TabbedWindow = 4,
}

/// <summary>Per-window shell and DWM properties on Windows: App User Model details and the system backdrop.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsWindow
{
    private static readonly Guid s_iidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid s_appUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    // IPropertyStore vtable slots, after IUnknown's three.
    private const int GetValueSlot = 5;
    private const int SetValueSlot = 6;
    private const int CommitSlot = 7;

    private const ushort VtLpwstr = 31;
    private const uint SystemBackdropType = 38; // DWMWA_SYSTEMBACKDROP_TYPE
    private const int GwlExStyle = -20;
    private const nint WsExTopmost = 0x00000008;
    private const nint HwndTopmost = -1;
    private const nint HwndNoTopmost = -2;
    private const uint SwpZOrderOnly = 0x0001 | 0x0002 | 0x0010; // NOSIZE, NOMOVE, NOACTIVATE

    /// <summary>Writes <paramref name="values"/> to the window's property store and commits them.
    /// False if the store could not be opened or refused a value.</summary>
    public static bool SetAppUserModel(nint hwnd, IEnumerable<(AppUserModelProperty Property, string Value)> values)
    {
        var store = OpenStore(hwnd);
        if (store is null) return false;
        try
        {
            foreach (var (property, value) in values)
            {
                var key = Key(property);
                var variant = new PropVariant { Vt = VtLpwstr, Pointer = (char*)Marshal.StringToCoTaskMemUni(value) };
                var hr = ((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, SetValueSlot))(store, &key, &variant);
                PropVariantClear(&variant);
                if (hr < 0) return false;
            }
            return ((delegate* unmanaged<void*, int>)Slot(store, CommitSlot))(store) >= 0;
        }
        finally { Release(store); }
    }

    /// <summary>The string the window's property store holds for <paramref name="property"/>, or null if none.</summary>
    public static string? GetAppUserModel(nint hwnd, AppUserModelProperty property)
    {
        var store = OpenStore(hwnd);
        if (store is null) return null;
        try
        {
            var key = Key(property);
            var variant = default(PropVariant);
            if (((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, GetValueSlot))(store, &key, &variant) < 0) return null;
            var value = variant.Vt == VtLpwstr ? new string(variant.Pointer) : null;
            PropVariantClear(&variant);
            return value;
        }
        finally { Release(store); }
    }

    /// <summary>Sets the system backdrop. False if DWM refused, which it does before Windows 11 22H2.</summary>
    public static bool SetBackdrop(nint hwnd, WindowsBackdrop backdrop)
    {
        var value = (int)backdrop;
        return DwmSetWindowAttribute(hwnd, SystemBackdropType, &value, sizeof(int)) >= 0;
    }

    /// <summary>The system backdrop DWM holds for the window, or null if it would not say.</summary>
    public static WindowsBackdrop? GetBackdrop(nint hwnd)
    {
        int value;
        return DwmGetWindowAttribute(hwnd, SystemBackdropType, &value, sizeof(int)) >= 0 ? (WindowsBackdrop)value : null;
    }

    /// <summary>Whether the window has <c>WS_EX_TOPMOST</c>.</summary>
    public static bool IsTopmost(nint hwnd) => (GetWindowLongPtrW(hwnd, GwlExStyle) & WsExTopmost) != 0;

    /// <summary>Makes the window topmost where a plain <c>HWND_TOPMOST</c> did not take. True if it is topmost now.</summary>
    public static bool EnsureTopmost(nint hwnd)
    {
        if (IsTopmost(hwnd)) return true;
        // Windows ignores HWND_TOPMOST, while reporting success, for a background thread's active window that lies under
        // the foreground window (CEF activates a window starting a load); HWND_NOTOPMOST is allowed and lifts it first.
        _ = SetWindowPos(hwnd, HwndNoTopmost, 0, 0, 0, 0, SwpZOrderOnly);
        _ = SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpZOrderOnly);
        return IsTopmost(hwnd);
    }

    /// <summary>The window's DPI scale: 1 at 96 DPI.</summary>
    public static double ScaleFactor(nint hwnd) => GetDpiForWindow(hwnd) is var dpi and > 0 ? dpi / 96.0 : 1.0;

    private static void* OpenStore(nint hwnd)
    {
        void* store;
        var iid = s_iidPropertyStore;
        return SHGetPropertyStoreForWindow(hwnd, &iid, &store) < 0 ? null : store;
    }

    private static PropertyKey Key(AppUserModelProperty property) => new() { Format = s_appUserModelFormat, Id = (uint)property };

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    private static void Release(void* com) => ((delegate* unmanaged<void*, uint>)Slot(com, 2))(com);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    // PROPVARIANT: a VARTYPE and three reserved words, then a 16-byte union; only the string pointer is used.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public char* Pointer;
    }

    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint hwnd, Guid* iid, void** store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(PropVariant* variant);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
