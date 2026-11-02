namespace Riftway.Cef.Interop;

public unsafe partial struct cef_permission_prompt_callback_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_permission_prompt_callback_t *, cef_permission_request_result_t)")]
    public delegate* unmanaged[Cdecl]<cef_permission_prompt_callback_t*, cef_permission_request_result_t, void> cont;
}
