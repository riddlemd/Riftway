using System.Runtime.InteropServices;

namespace Riftway.Cef.Interop;

/// <summary>This OS's <c>cef_main_args_t</c>; pass its address as a <see cref="cef_main_args_t"/> pointer.</summary>
/// <remarks>Callers take whichever OS's struct this is without naming it, so one call site serves every OS.</remarks>
[StructLayout(LayoutKind.Explicit)]
public struct cef_main_args_native_t
{
    [FieldOffset(0)] public cef_main_args_posix_t posix;
    [FieldOffset(0)] public cef_main_args_win_t win;
}

/// <summary><c>cef_main_args_t</c> on Windows.</summary>
public struct cef_main_args_win_t
{
    /// <summary>HINSTANCE.</summary>
    public nint instance;
}

/// <summary><c>cef_window_info_t</c> on Windows (include/internal/cef_types_win.h).</summary>
public struct cef_window_info_win_t
{
    public nuint size;
    public uint ex_style;
    public cef_string_utf16_t window_name;
    public uint style;
    public cef_rect_t bounds;
    /// <summary>HWND.</summary>
    public nint parent_window;
    /// <summary>HMENU.</summary>
    public nint menu;
    public int windowless_rendering_enabled;
    public int shared_texture_enabled;
    public int external_begin_frame_enabled;
    /// <summary>HWND.</summary>
    public nint window;
    public cef_runtime_style_t runtime_style;
}
