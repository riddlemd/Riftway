namespace Oriel.Cef.Interop;

/// <summary><c>cef_main_args_t</c> on macOS and Linux.</summary>
public unsafe struct cef_main_args_posix_t
{
    public int argc;
    public byte** argv;
}
