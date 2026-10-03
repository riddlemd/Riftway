namespace Oriel.Cef.Interop;

public unsafe partial struct cef_string_utf8_t
{
    [NativeTypeName("char *")]
    public sbyte* str;

    [NativeTypeName("size_t")]
    public nuint length;

    [NativeTypeName("void (*)(char *)")]
    public delegate* unmanaged[Cdecl]<sbyte*, void> dtor;
}
