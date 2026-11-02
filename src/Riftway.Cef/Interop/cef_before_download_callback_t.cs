namespace Riftway.Cef.Interop;

public unsafe partial struct cef_before_download_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_before_download_callback_t *, const cef_string_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_before_download_callback_t*, cef_string_utf16_t*, int, void> cont;
}
