namespace Oriel.Cef.Interop;

[NativeTypeName("unsigned int")]
public enum cef_path_key_t : uint
{
    PK_DIR_CURRENT,
    PK_DIR_EXE,
    PK_DIR_MODULE,
    PK_DIR_TEMP,
    PK_FILE_EXE,
    PK_FILE_MODULE,
    PK_LOCAL_APP_DATA,
    PK_USER_DATA,
    PK_DIR_RESOURCES,
    PK_NUM_VALUES,
}
