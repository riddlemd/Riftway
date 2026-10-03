namespace Oriel.Cef.Interop;

public unsafe partial struct cef_v8_value_t
{
    public cef_base_ref_counted_t @base;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_valid;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_undefined;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_null;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_bool;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_int;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_uint;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_double;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_date;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_string;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_object;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_array;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_array_buffer;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_function;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_promise;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_v8_value_t*, int> is_same;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> get_bool_value;

    [NativeTypeName("int32_t (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> get_int_value;

    [NativeTypeName("uint32_t (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, uint> get_uint_value;

    [NativeTypeName("double (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, double> get_double_value;

    [NativeTypeName("cef_basetime_t (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_basetime_t> get_date_value;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_string_utf16_t*> get_string_value;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> is_user_created;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> has_exception;

    [NativeTypeName("struct _cef_v8_exception_t *(*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_v8_exception_t*> get_exception;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> clear_exception;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> will_rethrow_exceptions;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int, int> set_rethrow_exceptions;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_string_utf16_t*, int> has_value_bykey;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int, int> has_value_byindex;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_string_utf16_t*, int> delete_value_bykey;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int, int> delete_value_byindex;

    [NativeTypeName("struct _cef_v8_value_t *(*)(struct _cef_v8_value_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_string_utf16_t*, cef_v8_value_t*> get_value_bykey;

    [NativeTypeName("struct _cef_v8_value_t *(*)(struct _cef_v8_value_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int, cef_v8_value_t*> get_value_byindex;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, const cef_string_t *, struct _cef_v8_value_t *, cef_v8_propertyattribute_t)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_string_utf16_t*, cef_v8_value_t*, cef_v8_propertyattribute_t, int> set_value_bykey;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, int, struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int, cef_v8_value_t*, int> set_value_byindex;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, const cef_string_t *, cef_v8_propertyattribute_t)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_string_utf16_t*, cef_v8_propertyattribute_t, int> set_value_byaccessor;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, cef_string_list_t)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_string_list_t*, int> get_keys;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, struct _cef_base_ref_counted_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_base_ref_counted_t*, int> set_user_data;

    [NativeTypeName("struct _cef_base_ref_counted_t *(*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_base_ref_counted_t*> get_user_data;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> get_externally_allocated_memory;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, int)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int, int> adjust_externally_allocated_memory;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> get_array_length;

    [NativeTypeName("struct _cef_v8_array_buffer_release_callback_t *(*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_v8_array_buffer_release_callback_t*> get_array_buffer_release_callback;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, int> neuter_array_buffer;

    [NativeTypeName("size_t (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, nuint> get_array_buffer_byte_length;

    [NativeTypeName("void *(*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, void*> get_array_buffer_data;

    [NativeTypeName("cef_string_userfree_t (*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_string_utf16_t*> get_function_name;

    [NativeTypeName("struct _cef_v8_handler_t *(*)(struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_v8_handler_t*> get_function_handler;

    [NativeTypeName("struct _cef_v8_value_t *(*)(struct _cef_v8_value_t *, struct _cef_v8_value_t *, size_t, struct _cef_v8_value_t *const *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_v8_value_t*, nuint, cef_v8_value_t**, cef_v8_value_t*> execute_function;

    [NativeTypeName("struct _cef_v8_value_t *(*)(struct _cef_v8_value_t *, struct _cef_v8_context_t *, struct _cef_v8_value_t *, size_t, struct _cef_v8_value_t *const *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_v8_context_t*, cef_v8_value_t*, nuint, cef_v8_value_t**, cef_v8_value_t*> execute_function_with_context;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, struct _cef_v8_value_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_v8_value_t*, int> resolve_promise;

    [NativeTypeName("int (*)(struct _cef_v8_value_t *, const cef_string_t *)")]
    public delegate* unmanaged[Cdecl]<cef_v8_value_t*, cef_string_utf16_t*, int> reject_promise;
}
