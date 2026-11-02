using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>NSLocale's answers, as Electron reads them on macOS.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacLocale
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    /// <summary>[NSLocale preferredLanguages].</summary>
    public static IReadOnlyList<string> PreferredLanguages()
    {
        var array = Send(Class("NSLocale"u8), Sel("preferredLanguages"u8));
        var languages = new List<string>();
        for (nint i = 0, n = Send(array, Sel("count"u8)); i < n; i++)
            languages.Add(Read(((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(array, Sel("objectAtIndex:"u8), i)));
        return languages;
    }

    /// <summary>[[NSLocale currentLocale] localeIdentifier].</summary>
    public static string CurrentIdentifier() => Read(Send(Current(), Sel("localeIdentifier"u8)));

    /// <summary>The current locale's country code, or empty.</summary>
    public static string CountryCode() => Read(Send(Current(), Sel("countryCode"u8)));

    private static nint Current() => Send(Class("NSLocale"u8), Sel("currentLocale"u8));

    private static string Read(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"u8))) ?? "";

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
}
