using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The application's light or dark appearance, and the display accessibility settings. Main thread only.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacAppearance
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    /// <summary>Forces the app's appearance: true dark, false light, null the system's.</summary>
    public static void SetDark(bool? dark)
    {
        var appearance = dark is { } d
            ? Send(Class("NSAppearance"), "appearanceNamed:", NSString(d ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua"))
            : 0;
        Send(App(), "setAppearance:", appearance);
    }

    /// <summary>Whether the app currently draws dark: its forced appearance, else the system's.</summary>
    public static bool IsDark()
    {
        var name = Send(Send(App(), "effectiveAppearance"), "name");
        return Marshal.PtrToStringUTF8(Send(name, "UTF8String"))?.Contains("Dark", StringComparison.Ordinal) == true;
    }

    /// <summary>The system's "Increase contrast" setting.</summary>
    public static bool IncreaseContrast => Flag("accessibilityDisplayShouldIncreaseContrast");

    /// <summary>The system's "Invert colours" setting.</summary>
    public static bool InvertColors => Flag("accessibilityDisplayShouldInvertColors");

    /// <summary>The system's "Reduce transparency" setting.</summary>
    public static bool ReduceTransparency => Flag("accessibilityDisplayShouldReduceTransparency");

    private static bool Flag(string selector) => (byte)Send(Send(Class("NSWorkspace"), "sharedWorkspace"), selector) != 0;

    private static nint App() => Send(Class("NSApplication"), "sharedApplication");

    private static nint NSString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* p = bytes) return Send(Class("NSString"), "stringWithUTF8String:", (nint)p);
    }

    private static nint Class(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes) return objc_getClass(p);
    }

    private static nint Selector(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes) return sel_registerName(p);
    }

    private static nint Send(nint receiver, string selector)
        => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, Selector(selector));

    private static nint Send(nint receiver, string selector, nint argument)
        => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, Selector(selector), argument);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
}
