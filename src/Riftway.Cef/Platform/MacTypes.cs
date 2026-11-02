namespace Riftway.Cef.Interop;

/// <summary><c>cef_window_info_t</c> on macOS (include/internal/cef_types_mac.h).</summary>
public struct cef_window_info_mac_t
{
    public nuint size;
    public cef_string_utf16_t window_name;
    public cef_rect_t bounds;
    public int hidden;
    /// <summary>NSView*.</summary>
    public nint parent_view;
    public int windowless_rendering_enabled;
    public int shared_texture_enabled;
    public int external_begin_frame_enabled;
    /// <summary>NSView*.</summary>
    public nint view;
    public cef_runtime_style_t runtime_style;
}

/// <summary><c>cef_accelerated_paint_info_t</c> on macOS.</summary>
public struct cef_accelerated_paint_info_mac_t
{
    public nuint size;
    /// <summary>IOSurface*.</summary>
    public nint shared_texture_io_surface;
    public cef_color_type_t format;
    public cef_accelerated_paint_info_common_t extra;
}
