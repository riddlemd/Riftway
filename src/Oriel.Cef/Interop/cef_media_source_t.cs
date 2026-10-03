namespace Oriel.Cef.Interop;

public unsafe partial struct cef_media_source_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_media_source_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_source_t*, cef_string_utf16_t*> get_id;

    [NativeTypeName("int (*)(struct _cef_media_source_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_source_t*, int> is_cast_source;

    [NativeTypeName("int (*)(struct _cef_media_source_t *)")]
    public delegate* unmanaged[Cdecl]<cef_media_source_t*, int> is_dial_source;
}
