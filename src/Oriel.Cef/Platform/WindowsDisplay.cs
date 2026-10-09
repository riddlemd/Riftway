using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>A monitor as Chromium's <c>ScreenWin</c> reads it from Windows (ui/display/win/screen_win.cc).</summary>
/// <param name="Handle">The <c>HMONITOR</c>.</param>
/// <param name="DeviceName">The GDI device name, e.g. <c>\\.\DISPLAY1</c>.</param>
/// <param name="Left">The left edge of <c>rcMonitor</c>, in physical pixels.</param>
/// <param name="Top">The top edge of <c>rcMonitor</c>, in physical pixels.</param>
/// <param name="Right">The right edge of <c>rcMonitor</c>, in physical pixels.</param>
/// <param name="Bottom">The bottom edge of <c>rcMonitor</c>, in physical pixels.</param>
/// <param name="Frequency"><c>dmDisplayFrequency</c> of the current mode; 0 when Windows would not say.</param>
/// <param name="Label">The EDID friendly name; empty without one.</param>
/// <param name="OutputTechnology">The path's <c>DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY</c>; <c>OTHER</c> (-1) without a path.</param>
/// <param name="IccProfile">The bytes of the monitor's ICC profile; null without one.</param>
/// <param name="HdrEnabled">DXGI reports the output in <c>DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020</c>.</param>
/// <param name="SdrWhiteLevel">The SDR white level in nits; Chromium's 200 without a path.</param>
/// <param name="MaxLuminance">DXGI's maximum luminance in nits; 0 without an output.</param>
/// <param name="Primaries">DXGI's red, green, blue and white xy; null without an output.</param>
public sealed record WindowsMonitor(
    nint Handle, string DeviceName, int Left, int Top, int Right, int Bottom, int Frequency, string Label,
    uint OutputTechnology, byte[]? IccProfile, bool HdrEnabled, float SdrWhiteLevel, float MaxLuminance, float[]? Primaries);

/// <summary>Reads monitors the way Chromium's <c>ScreenWin</c> does and hosts the window that hears display changes.</summary>
/// <remarks>The host is a hidden top-level window: <c>WM_DISPLAYCHANGE</c> and <c>WM_SETTINGCHANGE</c> are broadcast to
/// top-level windows only, which is why Chromium's <c>SingletonHwnd</c> is one too. UI thread only.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsDisplay
{
    /// <summary>The host window's class name.</summary>
    public const string ObserverClassName = "OrielDisplayObserver";

    private const uint QdcOnlyActivePaths = 0x2;
    private const int ErrorSuccess = 0, ErrorInsufficientBuffer = 122;
    private const int GetSourceName = 1, GetTargetName = 2, GetSdrWhiteLevel = 11; // DISPLAYCONFIG_DEVICE_INFO_TYPE
    private const uint OutputTechnologyOther = 0xFFFFFFFF;
    private const int DxgiColorSpaceHdr10 = 12; // DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020

    private static readonly Guid s_iidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid s_iidOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    private static nint s_observer;
    private static Action<uint, nint>? s_onMessage;

    /// <summary>Creates the hidden host window if needed; every message it gets is passed to <paramref name="onMessage"/>
    /// with its <c>wParam</c> before default handling. Returns the window.</summary>
    public static nint StartObserver(Action<uint, nint> onMessage)
    {
        s_onMessage = onMessage;
        if (s_observer != 0) return s_observer;
        fixed (char* name = ObserverClassName)
        {
            var cls = new WndClass { WndProc = &WndProc, Instance = GetModuleHandleW(null), ClassName = name };
            RegisterClassW(&cls);
            s_observer = CreateWindowExW(0x08000000 /* WS_EX_NOACTIVATE */, name, name, 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, 0, 0, cls.Instance, 0);
        }
        // Broadcasts go down the z-order: at the bottom the window hears them after Chromium's SingletonHwnd has re-read
        // the displays, so CEF already answers with the new ones.
        SetWindowPos(s_observer, 1 /* HWND_BOTTOM */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE */);
        return s_observer;
    }

    /// <summary>Every monitor, in <c>EnumDisplayMonitors</c> order as Chromium lists displays.</summary>
    public static IReadOnlyList<WindowsMonitor> Monitors()
    {
        var handles = new List<nint>();
        var gc = GCHandle.Alloc(handles);
        try { EnumDisplayMonitors(0, null, &CollectMonitor, GCHandle.ToIntPtr(gc)); }
        finally { gc.Free(); }

        var paths = ActivePaths();
        var dxgi = DxgiOutputs();
        var monitors = new List<WindowsMonitor>(handles.Count);
        foreach (var handle in handles)
        {
            var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
            if (GetMonitorInfoW(handle, &info) == 0) continue;
            var device = new string(info.Device);
            var path = PathFor(paths, device);
            dxgi.TryGetValue(handle, out var output);
            monitors.Add(new WindowsMonitor(
                handle, device, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom,
                Frequency(info.Device), path is { } p ? FriendlyName(p) : "",
                path is { } t ? t.TargetOutputTechnology : OutputTechnologyOther,
                IccProfile(info.Device), output.Hdr, path is { } w ? SdrWhiteLevel(w) : 200f, output.MaxLuminance, output.Primaries));
        }
        return monitors;
    }

    [UnmanagedCallersOnly]
    private static int CollectMonitor(nint monitor, nint dc, Rect* rect, nint data)
    {
        ((List<nint>)GCHandle.FromIntPtr(data).Target!).Add(monitor);
        return 1;
    }

    private static int Frequency(char* device)
    {
        var mode = new DevMode { Size = (ushort)sizeof(DevMode) };
        return EnumDisplaySettingsW(device, unchecked((uint)-1) /* ENUM_CURRENT_SETTINGS */, &mode) != 0 ? (int)mode.DisplayFrequency : 0;
    }

    // As Chromium's display_config_helper: the active path whose source GDI name is the monitor's device.
    private static PathInfo[] ActivePaths()
    {
        for (var result = ErrorInsufficientBuffer; result == ErrorInsufficientBuffer;)
        {
            uint pathCount, modeCount;
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, &pathCount, &modeCount) != ErrorSuccess) return [];
            var paths = new PathInfo[pathCount];
            var modes = new byte[modeCount * 64]; // DISPLAYCONFIG_MODE_INFO
            fixed (PathInfo* p = paths)
            fixed (byte* m = modes)
                result = QueryDisplayConfig(QdcOnlyActivePaths, &pathCount, p, &modeCount, m, null);
            if (result == ErrorSuccess) return paths[..(int)pathCount];
        }
        return [];
    }

    /// <summary>The target of the active path whose source is <paramref name="device"/>, which Chromium's
    /// <c>DisplayIdFromMonitorInfo</c> hashes; null without one.</summary>
    public static (uint AdapterLow, int AdapterHigh, uint TargetId)? PathTarget(string device)
        => PathFor(ActivePaths(), device) is { } p ? (p.TargetAdapterLow, p.TargetAdapterHigh, p.TargetId) : null;

    private static PathInfo? PathFor(PathInfo[] paths, string device)
    {
        foreach (var path in paths)
        {
            var name = new SourceDeviceName { Header = new() { Type = GetSourceName, Size = (uint)sizeof(SourceDeviceName), AdapterLow = path.SourceAdapterLow, AdapterHigh = path.SourceAdapterHigh, Id = path.SourceId } };
            if (DisplayConfigGetDeviceInfo(&name.Header) == ErrorSuccess && new string(name.GdiDeviceName) == device) return path;
        }
        return null;
    }

    // Chromium's GetFriendlyDeviceName: the name only when it came from the EDID.
    private static string FriendlyName(PathInfo path)
    {
        var name = new TargetDeviceName { Header = new() { Type = GetTargetName, Size = (uint)sizeof(TargetDeviceName), AdapterLow = path.TargetAdapterLow, AdapterHigh = path.TargetAdapterHigh, Id = path.TargetId } };
        return DisplayConfigGetDeviceInfo(&name.Header) == ErrorSuccess && (name.Flags & 1) != 0 ? new string(name.FriendlyName) : "";
    }

    private static float SdrWhiteLevel(PathInfo path)
    {
        var level = new SdrWhite { Header = new() { Type = GetSdrWhiteLevel, Size = (uint)sizeof(SdrWhite), AdapterLow = path.TargetAdapterLow, AdapterHigh = path.TargetAdapterHigh, Id = path.TargetId } };
        return DisplayConfigGetDeviceInfo(&level.Header) == ErrorSuccess ? (float)(level.Level * 80.0 / 1000.0) : 200f;
    }

    // As Chromium's ColorProfileReader: GetICMProfile on a DC for the device, then the file's bytes.
    private static byte[]? IccProfile(char* device)
    {
        var dc = CreateDCW(device, null, null, null);
        if (dc == 0) return null;
        var path = stackalloc char[261];
        uint length = 260;
        var found = GetICMProfileW(dc, &length, path) != 0;
        DeleteDC(dc);
        if (!found) return null;
        try { return File.ReadAllBytes(new string(path)) is { Length: > 0 } bytes ? bytes : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // What Chromium's GPU process reports per output (ui/gl/direct_composition_support.cc), keyed by HMONITOR.
    private static Dictionary<nint, (bool Hdr, float MaxLuminance, float[]? Primaries)> DxgiOutputs()
    {
        var outputs = new Dictionary<nint, (bool, float, float[]?)>();
        void* factory;
        var iidFactory = s_iidFactory1;
        if (CreateDXGIFactory1(&iidFactory, &factory) < 0) return outputs;
        try
        {
            void* adapter;
            for (uint a = 0; ((delegate* unmanaged<void*, uint, void**, int>)Slot(factory, 12))(factory, a, &adapter) >= 0; a++) // EnumAdapters1
            {
                void* output;
                for (uint o = 0; ((delegate* unmanaged<void*, uint, void**, int>)Slot(adapter, 7))(adapter, o, &output) >= 0; o++) // EnumOutputs
                {
                    void* output6;
                    var iidOutput6 = s_iidOutput6;
                    if (((delegate* unmanaged<void*, Guid*, void**, int>)Slot(output, 0))(output, &iidOutput6, &output6) >= 0) // QueryInterface
                    {
                        OutputDesc1 desc;
                        if (((delegate* unmanaged<void*, OutputDesc1*, int>)Slot(output6, 27))(output6, &desc) >= 0) // GetDesc1
                            outputs[desc.Monitor] = (desc.ColorSpace == DxgiColorSpaceHdr10, desc.MaxLuminance,
                                [desc.Primaries[0], desc.Primaries[1], desc.Primaries[2], desc.Primaries[3], desc.Primaries[4], desc.Primaries[5], desc.Primaries[6], desc.Primaries[7]]);
                        Release(output6);
                    }
                    Release(output);
                }
                Release(adapter);
            }
        }
        finally { Release(factory); }
        return outputs;
    }

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    private static void Release(void* com) => ((delegate* unmanaged<void*, uint>)Slot(com, 2))(com);

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try { s_onMessage?.Invoke(message, wParam); }
        // An exception must not cross into the window procedure.
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] A display change handler threw: {e}"); }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        public fixed char Device[32];
    }

    // DEVMODEW, 220 bytes; only the size and the display frequency are touched.
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DevMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(184)] public uint DisplayFrequency;
    }

    // DISPLAYCONFIG_PATH_INFO, 72 bytes: source info (20), target info (48), flags.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct PathInfo
    {
        [FieldOffset(0)] public uint SourceAdapterLow;
        [FieldOffset(4)] public int SourceAdapterHigh;
        [FieldOffset(8)] public uint SourceId;
        [FieldOffset(20)] public uint TargetAdapterLow;
        [FieldOffset(24)] public int TargetAdapterHigh;
        [FieldOffset(28)] public uint TargetId;
        [FieldOffset(36)] public uint TargetOutputTechnology;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public int Type;
        public uint Size;
        public uint AdapterLow;
        public int AdapterHigh;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        public fixed char GdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort ManufactureId;
        public ushort ProductCodeId;
        public uint ConnectorInstance;
        public fixed char FriendlyName[64];
        public fixed char DevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdrWhite
    {
        public DeviceInfoHeader Header;
        public uint Level;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputDesc1
    {
        public fixed char DeviceName[32];
        public Rect DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public nint Monitor;
        public uint BitsPerColor;
        public int ColorSpace;
        public fixed float Primaries[8];
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
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

    [DllImport("user32.dll")] private static extern int EnumDisplayMonitors(nint dc, Rect* clip, delegate* unmanaged<nint, nint, Rect*, nint, int> callback, nint data);
    [DllImport("user32.dll")] private static extern int GetMonitorInfoW(nint monitor, MonitorInfoEx* info);
    [DllImport("user32.dll")] private static extern int EnumDisplaySettingsW(char* device, uint mode, DevMode* devMode);
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, uint* paths, uint* modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, uint* pathCount, PathInfo* paths, uint* modeCount, byte* modes, void* topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(DeviceInfoHeader* header);
    [DllImport("gdi32.dll")] private static extern nint CreateDCW(char* driver, char* device, char* output, void* init);
    [DllImport("gdi32.dll")] private static extern int DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int GetICMProfileW(nint dc, uint* length, char* path);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(Guid* iid, void** factory);
    [DllImport("user32.dll")] private static extern ushort RegisterClassW(WndClass* cls);
    [DllImport("user32.dll")] private static extern nint CreateWindowExW(uint exStyle, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern int SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(char* name);
}
