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
    private const uint WindowCornerPreference = 33; // DWMWA_WINDOW_CORNER_PREFERENCE
    private const uint BorderColor = 34; // DWMWA_BORDER_COLOR
    private const uint CaptionColor = 35; // DWMWA_CAPTION_COLOR

    /// <summary>Writes each of <paramref name="values"/> to the window's property store and commits it, going on past a
    /// value the store refuses, as Chromium's <c>SetAppDetailsForWindow</c>. False if the store could not be opened or
    /// refused any value.</summary>
    public static bool SetAppUserModel(nint hwnd, IEnumerable<(AppUserModelProperty Property, string Value)> values)
    {
        var store = OpenStore(hwnd);
        if (store is null) return false;
        try
        {
            var all = true;
            foreach (var (property, value) in values)
            {
                var key = Key(property);
                var variant = new PropVariant { Vt = VtLpwstr, Pointer = (char*)Marshal.StringToCoTaskMemUni(value) };
                var hr = ((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, SetValueSlot))(store, &key, &variant);
                PropVariantClear(&variant);
                // base::win::SetPropVariantValueForPropertyStore commits only after S_OK.
                if (hr == 0) hr = ((delegate* unmanaged<void*, int>)Slot(store, CommitSlot))(store);
                all &= hr >= 0;
            }
            return all;
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

    /// <summary>Whether this is Windows 11 (build 22000 or later), Chromium's <c>base::win::Version::WIN11</c>.</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Sets <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>: <c>DWMWCP_ROUND</c> or <c>DWMWCP_DONOTROUND</c>.
    /// False if DWM refused, which it does before Windows 11.</summary>
    public static bool SetRoundedCorners(nint hwnd, bool rounded)
    {
        var value = rounded ? 2 /* DWMWCP_ROUND */ : 1 /* DWMWCP_DONOTROUND */;
        return DwmSetWindowAttribute(hwnd, WindowCornerPreference, &value, sizeof(int)) >= 0;
    }

    /// <summary>Sets the window's border colour (<c>DWMWA_BORDER_COLOR</c>) and, with <paramref name="caption"/>, its
    /// caption colour (<c>DWMWA_CAPTION_COLOR</c>) to the COLORREF <paramref name="colorRef"/>. False if DWM refused either.</summary>
    public static bool SetBorderAndCaptionColor(nint hwnd, uint colorRef, bool caption)
    {
        var ok = !caption || DwmSetWindowAttribute(hwnd, CaptionColor, &colorRef, sizeof(uint)) >= 0;
        return DwmSetWindowAttribute(hwnd, BorderColor, &colorRef, sizeof(uint)) >= 0 && ok;
    }

    /// <summary>A 32-bit DWM window attribute, or null if DWM would not say.</summary>
    public static uint? GetDwmAttribute(nint hwnd, uint attribute)
    {
        uint value;
        return DwmGetWindowAttribute(hwnd, attribute, &value, sizeof(uint)) >= 0 ? value : null;
    }

    /// <summary>Whether Windows shows the accent colour on title bars and borders (<c>DWM\ColorPrevalence</c> is 1);
    /// Chromium's <c>AccentColorObserver::ShouldUseAccentColorForWindowFrame</c>.</summary>
    public static bool AccentColorOnTitleBars()
        => Microsoft.Win32.Registry.GetValue(DwmKey, "ColorPrevalence", null) is int prevalence && prevalence == 1;

    /// <summary>The system accent colour as opaque ARGB, or null if there is none; Chromium's
    /// <c>AccentColorObserver::accent_color</c>: <c>UISettings</c>, then the <c>DWM\AccentColor</c> registry value.</summary>
    public static uint? SystemAccentColor()
    {
        if (UISettingsAccent() is { } accent) return accent;
        // The registry holds a COLORREF, 0x00BBGGRR; skia::COLORREFToSkColor ignores the top byte.
        return Microsoft.Win32.Registry.GetValue(DwmKey, "AccentColor", null) is int value
            ? 0xFF000000 | (((uint)value & 0xFF) << 16) | ((uint)value & 0xFF00) | (((uint)value >> 16) & 0xFF)
            : null;
    }

    // Windows coerces unsupported registry colours; UISettings reports the colour actually used.
    private static uint? UISettingsAccent()
    {
        nint name = 0;
        void* inspectable = null, settings3 = null;
        try
        {
            fixed (char* className = "Windows.UI.ViewManagement.UISettings")
                if (WindowsCreateString(className, 36, &name) < 0) return null;
            if (RoActivateInstance(name, &inspectable) < 0 || inspectable is null) return null;
            var iid = s_iidUISettings3;
            if (((delegate* unmanaged<void*, Guid*, void**, int>)Slot(inspectable, 0))(inspectable, &iid, &settings3) < 0 || settings3 is null) return null;
            UIColor color;
            // IUISettings3::GetColorValue follows IInspectable's six slots; UIColorType_Accent is 5.
            if (((delegate* unmanaged<void*, int, UIColor*, int>)Slot(settings3, 6))(settings3, 5, &color) < 0) return null;
            return 0xFF000000 | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        }
        finally
        {
            if (settings3 is not null) Release(settings3);
            if (inspectable is not null) Release(inspectable);
            if (name != 0) WindowsDeleteString(name);
        }
    }

    private const string DwmKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM";
    private static readonly Guid s_iidUISettings3 = new("03021BE4-5254-4781-8194-5168F7D06D7B");

    [StructLayout(LayoutKind.Sequential)]
    private struct UIColor
    {
        public byte A, R, G, B;
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
    [DllImport("combase.dll")] private static extern int WindowsCreateString(char* source, uint length, nint* hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")] private static extern int RoActivateInstance(nint classId, void** instance);
}
