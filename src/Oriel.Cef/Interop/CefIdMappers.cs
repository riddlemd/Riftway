using System.Runtime.InteropServices;

namespace Oriel.Cef.Interop;

// include/cef_id_mappers.h, which the generator does not cover.
public static unsafe partial class CefNative
{
    /// <summary>The numeric ID of a Chrome command (<c>IDC_*</c> in cef_command_ids.h) in this CEF build; -1 if unknown.</summary>
    [DllImport("libcef", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    public static extern int cef_id_for_command_id_name(byte* name);
}
