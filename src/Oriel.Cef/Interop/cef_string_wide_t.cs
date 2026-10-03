namespace Oriel.Cef.Interop;

public unsafe partial struct cef_string_wide_t
{
    [NativeTypeName("wchar_t *")]
    public int* str;

    [NativeTypeName("size_t")]
    public nuint length;

    [NativeTypeName("void (*)(wchar_t *)")]
    public delegate* unmanaged[Cdecl]<int*, void> dtor;
}
