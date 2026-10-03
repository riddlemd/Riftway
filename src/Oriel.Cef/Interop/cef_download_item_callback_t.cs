namespace Oriel.Cef.Interop;

public unsafe partial struct cef_download_item_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_download_item_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_download_item_callback_t*, void> cancel;

    [NativeTypeName("void (*)(struct _cef_download_item_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_download_item_callback_t*, void> pause;

    [NativeTypeName("void (*)(struct _cef_download_item_callback_t *)")]
    public delegate* unmanaged[Cdecl]<cef_download_item_callback_t*, void> resume;
}
