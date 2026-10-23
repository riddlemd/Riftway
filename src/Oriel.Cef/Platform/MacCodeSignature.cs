using System.Runtime.InteropServices;

namespace Oriel.Cef.Platform;

/// <summary>The running app's own code signature on macOS.</summary>
public static unsafe class MacCodeSignature
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint kSecCSSigningInformation = 1 << 1;
    private const uint kCFStringEncodingUTF8 = 0x08000100;

    /// <summary>The team identifier of the app's signature: null when the app is unsigned or signed ad hoc, whose
    /// identity changes with every build.</summary>
    public static string? TeamIdentifier()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        nint code;
        if (SecCodeCopySelf(0, &code) != 0 || code == 0) return null;
        try
        {
            nint info;
            if (SecCodeCopySigningInformation(code, kSecCSSigningInformation, &info) != 0 || info == 0) return null;
            try
            {
                var key = *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(Security), "kSecCodeInfoTeamIdentifier");
                var value = CFDictionaryGetValue(info, key);
                if (value == 0) return null;
                var buffer = stackalloc byte[256];
                return CFStringGetCString(value, buffer, 256, kCFStringEncodingUTF8) != 0 ? Marshal.PtrToStringUTF8((nint)buffer) : null;
            }
            finally { CFRelease(info); }
        }
        finally { CFRelease(code); }
    }

    [DllImport(Security)] private static extern int SecCodeCopySelf(uint flags, nint* self);
    [DllImport(Security)] private static extern int SecCodeCopySigningInformation(nint code, uint flags, nint* information);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryGetValue(nint dict, nint key);
    [DllImport(CoreFoundation)] private static extern byte CFStringGetCString(nint str, byte* buffer, nint size, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint cf);
}
