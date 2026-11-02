namespace Riftway.Cef.Interop;

public unsafe partial struct cef_frame_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, int> is_valid;

    [NativeTypeName("void (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, void> undo;

    [NativeTypeName("void (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, void> redo;

    [NativeTypeName("void (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, void> cut;

    [NativeTypeName("void (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, void> copy;

    [NativeTypeName("void (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, void> paste;

    [NativeTypeName("void (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, void> paste_and_match_style;

    [NativeTypeName("void (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, void> del;

    [NativeTypeName("void (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, void> select_all;

    [NativeTypeName("void (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, void> view_source;

    [NativeTypeName("void (*)(struct _cef_frame_t *, struct _cef_string_visitor_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_string_visitor_t*, void> get_source;

    [NativeTypeName("void (*)(struct _cef_frame_t *, struct _cef_string_visitor_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_string_visitor_t*, void> get_text;

    [NativeTypeName("void (*)(struct _cef_frame_t *, struct _cef_request_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_request_t*, void> load_request;

    [NativeTypeName("void (*)(struct _cef_frame_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_string_utf16_t*, void> load_url;

    [NativeTypeName("void (*)(struct _cef_frame_t *, const cef_string_t *, const cef_string_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_string_utf16_t*, cef_string_utf16_t*, int, void> execute_java_script;

    [NativeTypeName("int (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, int> is_main;

    [NativeTypeName("int (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, int> is_focused;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_string_utf16_t*> get_name;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_string_utf16_t*> get_identifier;

    [NativeTypeName("struct _cef_frame_t *(*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_frame_t*> get_parent;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_string_utf16_t*> get_url;

    [NativeTypeName("struct _cef_browser_t *(*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_browser_t*> get_browser;

    [NativeTypeName("struct _cef_v8_context_t *(*)(struct _cef_frame_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_v8_context_t*> get_v8_context;

    [NativeTypeName("void (*)(struct _cef_frame_t *, struct _cef_domvisitor_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_domvisitor_t*, void> visit_dom;

    [NativeTypeName("struct _cef_urlrequest_t *(*)(struct _cef_frame_t *, struct _cef_request_t *, struct _cef_urlrequest_client_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_request_t*, cef_urlrequest_client_t*, cef_urlrequest_t*> create_urlrequest;

    [NativeTypeName("void (*)(struct _cef_frame_t *, cef_process_id_t, struct _cef_process_message_t *)")]
    public delegate* unmanaged[Cdecl]<cef_frame_t*, cef_process_id_t, cef_process_message_t*, void> send_process_message;
}
