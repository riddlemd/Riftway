namespace Riftway.Cef.Interop;

public unsafe partial struct cef_download_image_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_download_image_callback_t *, const cef_string_t *, int, struct _cef_image_t *)")]
    public delegate* unmanaged[Cdecl]<cef_download_image_callback_t*, cef_string_utf16_t*, int, cef_image_t*, void> on_download_image_finished;
}
