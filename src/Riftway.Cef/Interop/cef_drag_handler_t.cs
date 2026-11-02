namespace Riftway.Cef.Interop;

public unsafe partial struct cef_drag_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_drag_handler_t *, struct _cef_browser_t *, struct _cef_drag_data_t *, cef_drag_operations_mask_t)")]
    public delegate* unmanaged[Cdecl]<cef_drag_handler_t*, cef_browser_t*, cef_drag_data_t*, cef_drag_operations_mask_t, int> on_drag_enter;

    [NativeTypeName("void (*)(struct _cef_drag_handler_t *, struct _cef_browser_t *, struct _cef_frame_t *, size_t, const cef_draggable_region_t *)")]
    public delegate* unmanaged[Cdecl]<cef_drag_handler_t*, cef_browser_t*, cef_frame_t*, nuint, cef_draggable_region_t*, void> on_draggable_regions_changed;
}
