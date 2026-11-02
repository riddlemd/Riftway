namespace Riftway.Cef.Interop;

public unsafe partial struct cef_preference_observer_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("void (*)(struct _cef_preference_observer_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_preference_observer_t*, cef_string_utf16_t*, void> on_preference_changed;
}
