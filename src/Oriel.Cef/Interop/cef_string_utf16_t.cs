namespace Oriel.Cef.Interop;

public unsafe partial struct cef_string_utf16_t
{
    [NativeTypeName("char16_t *")]
    public char* str;

    [NativeTypeName("size_t")]
    public nuint length;

    [NativeTypeName("void (*)(char16_t *)")]
    public delegate* unmanaged[Cdecl]<char*, void> dtor;
}
