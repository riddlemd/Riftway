namespace Riftway.Cef.Interop;

public unsafe partial struct cef_audio_handler_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_audio_handler_t *, struct _cef_browser_t *, cef_audio_parameters_t *)")]
    public delegate* unmanaged[Cdecl]<cef_audio_handler_t*, cef_browser_t*, cef_audio_parameters_t*, int> get_audio_parameters;

    [NativeTypeName("void (*)(struct _cef_audio_handler_t *, struct _cef_browser_t *, const cef_audio_parameters_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_audio_handler_t*, cef_browser_t*, cef_audio_parameters_t*, int, void> on_audio_stream_started;

    [NativeTypeName("void (*)(struct _cef_audio_handler_t *, struct _cef_browser_t *, const float **, int, int64_t)")]
    public delegate* unmanaged[Cdecl]<cef_audio_handler_t*, cef_browser_t*, float**, int, long, void> on_audio_stream_packet;

    [NativeTypeName("void (*)(struct _cef_audio_handler_t *, struct _cef_browser_t *)")]
    public delegate* unmanaged[Cdecl]<cef_audio_handler_t*, cef_browser_t*, void> on_audio_stream_stopped;

    [NativeTypeName("void (*)(struct _cef_audio_handler_t *, struct _cef_browser_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_audio_handler_t*, cef_browser_t*, cef_string_utf16_t*, void> on_audio_stream_error;
}
