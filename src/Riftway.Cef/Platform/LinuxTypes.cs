namespace Riftway.Cef.Interop;

/// <summary><c>cef_window_info_t</c> on Linux (include/internal/cef_types_linux.h).</summary>
public struct cef_window_info_linux_t
{
    public nuint size;
    public cef_string_utf16_t window_name;
    public cef_rect_t bounds;
    /// <summary>X11 Window.</summary>
    public nuint parent_window;
    public int windowless_rendering_enabled;
    public int shared_texture_enabled;
    public int external_begin_frame_enabled;
    /// <summary>X11 Window.</summary>
    public nuint window;
    public cef_runtime_style_t runtime_style;
}
