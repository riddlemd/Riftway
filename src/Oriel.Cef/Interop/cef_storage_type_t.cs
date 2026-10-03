namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_storage_type_t : uint
{
    ST_LOCALSTORAGE = 0,
    ST_SESSIONSTORAGE,
}
