namespace Oriel.Cef.Interop;

public unsafe partial struct cef_media_sink_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_media_sink_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_sink_t*, cef_string_utf16_t*> get_id;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_media_sink_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_sink_t*, cef_string_utf16_t*> get_name;

    [NativeTypeName("cef_media_sink_icon_type_t (*)(struct _cef_media_sink_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_sink_t*, cef_media_sink_icon_type_t> get_icon_type;

    [NativeTypeName("void (*)(struct _cef_media_sink_t *, struct _cef_media_sink_device_info_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_sink_t*, cef_media_sink_device_info_callback_t*, void> get_device_info;

    [NativeTypeName("int (*)(struct _cef_media_sink_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_sink_t*, int> is_cast_sink;

    [NativeTypeName("int (*)(struct _cef_media_sink_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_sink_t*, int> is_dial_sink;

    [NativeTypeName("int (*)(struct _cef_media_sink_t *, struct _cef_media_source_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_sink_t*, cef_media_source_t*, int> is_compatible_with;
}
