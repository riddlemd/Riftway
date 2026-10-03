namespace Oriel.Cef.Interop;

public unsafe partial struct cef_media_sink_device_info_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_media_sink_device_info_callback_t *, const struct _cef_media_sink_device_info_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_sink_device_info_callback_t*, cef_media_sink_device_info_t*, void> on_media_sink_device_info;
}
