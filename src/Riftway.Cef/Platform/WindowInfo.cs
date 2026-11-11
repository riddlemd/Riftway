using System.Runtime.InteropServices;

namespace Riftway.Cef.Interop;

/// <summary>This OS's <c>cef_window_info_t</c>; pass its address as a <see cref="cef_window_info_t"/> pointer.</summary>
[StructLayout(LayoutKind.Explicit)]
public unsafe struct cef_window_info_native_t
{
    [FieldOffset(0)] public cef_window_info_mac_t mac;
    [FieldOffset(0)] public cef_window_info_linux_t linux;
    [FieldOffset(0)] public cef_window_info_win_t win;

    /// <summary>A zeroed window info in this OS's layout. CEF reads the whole struct even where it ignores the
    /// values, so the opaque <see cref="cef_window_info_t"/> stand-in must never be passed itself.</summary>
    /// <exception cref="PlatformNotSupportedException">Not yet supported on this OS.</exception>
    public static cef_window_info_native_t Empty()
    {
        var info = new cef_window_info_native_t();
        if (OperatingSystem.IsMacOS()) info.mac.size = (nuint)sizeof(cef_window_info_mac_t);
        else if (OperatingSystem.IsLinux()) info.linux.size = (nuint)sizeof(cef_window_info_linux_t);
        else if (OperatingSystem.IsWindows()) info.win.size = (nuint)sizeof(cef_window_info_win_t);
        else throw new PlatformNotSupportedException($"Riftway does not run on {Environment.OSVersion} yet.");
        return info;
    }

    /// <summary>A window info for a windowless (offscreen) browser: Alloy style, as CEF allows windowless rendering
    /// with no other.</summary>
    /// <exception cref="PlatformNotSupportedException">Not yet supported on this OS.</exception>
    public static cef_window_info_native_t Windowless()
    {
        var info = Empty();
        if (OperatingSystem.IsMacOS()) (info.mac.windowless_rendering_enabled, info.mac.runtime_style) = (1, cef_runtime_style_t.CEF_RUNTIME_STYLE_ALLOY);
        else if (OperatingSystem.IsLinux()) (info.linux.windowless_rendering_enabled, info.linux.runtime_style) = (1, cef_runtime_style_t.CEF_RUNTIME_STYLE_ALLOY);
        else (info.win.windowless_rendering_enabled, info.win.runtime_style) = (1, cef_runtime_style_t.CEF_RUNTIME_STYLE_ALLOY);
        return info;
    }
}
