using System.Runtime.CompilerServices;

namespace Riftway.Cef.Interop;

public unsafe partial struct cef_version_info_t
{
    [NativeTypeName("size_t")]
    public nuint size;

    public int cef_version_major;

    public int cef_version_minor;

    public int cef_version_patch;

    public int cef_commit_number;

    public int chrome_version_major;

    public int chrome_version_minor;

    public int chrome_version_build;

    public int chrome_version_patch;

    [NativeTypeName("char[17]")]
    public _sandbox_compat_hash_e__FixedBuffer sandbox_compat_hash;

    [NativeTypeName("const wchar_t *")]
    public int* libcef_path;

    public int libcef_is_bundled;

    [NativeTypeName("const char *")]
    public sbyte* libcef_version_full;

    public int installer_error_code;

    [NativeTypeName("const char *")]
    public sbyte* installer_error_message;

    [InlineArray(17)]
    public partial struct _sandbox_compat_hash_e__FixedBuffer
    {
        public sbyte e0;
    }
}
