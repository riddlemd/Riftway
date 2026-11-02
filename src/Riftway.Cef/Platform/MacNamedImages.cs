using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>Electron v44's <c>electron_api_native_image_mac.mm</c>: named AppKit images and SF Symbols as PNG.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacNamedImages
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    // NSBitmapImageFileTypePNG.
    private const nuint PngFileType = 4;
    private static readonly nint s_send = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize
    {
        public double Width, Height;
    }

    /// <summary><c>imageNamed:</c> as PNG; null when there is no such valid image.</summary>
    public static byte[]? Named(string name) => WithPool(() => Png(((delegate* unmanaged<nint, nint, nint, nint>)s_send)(Class("NSImage"u8), Sel("imageNamed:"u8), NSString(name))));

    /// <summary><c>imageWithSystemSymbolName:</c> in the configuration of <paramref name="pointSize"/>,
    /// <paramref name="weight"/> (an NSFontWeight) and <paramref name="scale"/> (an NSImageSymbolScale), as PNG; null when
    /// there is no such symbol.</summary>
    public static byte[]? Symbol(string name, double pointSize, double weight, long scale) => WithPool(() =>
    {
        _ = s_appKit;
        var image = ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_send)(Class("NSImage"u8), Sel("imageWithSystemSymbolName:accessibilityDescription:"u8), NSString(name), 0);
        if (image == 0) return null;
        var configuration = ((delegate* unmanaged<nint, nint, double, double, nint, nint>)s_send)(Class("NSImageSymbolConfiguration"u8),
            Sel("configurationWithPointSize:weight:scale:"u8), pointSize, weight, (nint)scale);
        return Png(((delegate* unmanaged<nint, nint, nint, nint>)s_send)(image, Sel("imageWithSymbolConfiguration:"u8), configuration));
    });

    // bufferFromNSImage: the image's CGImage in a bitmap representation of the image's size, encoded as PNG.
    private static byte[]? Png(nint image)
    {
        if (image == 0 || ((delegate* unmanaged<nint, nint, byte>)s_send)(image, Sel("isValid"u8)) == 0) return null;
        var cgImage = ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)s_send)(image, Sel("CGImageForProposedRect:context:hints:"u8), 0, 0, 0);
        if (cgImage == 0) return null;
        var rep = ((delegate* unmanaged<nint, nint, nint, nint>)s_send)(Send(Class("NSBitmapImageRep"u8), Sel("alloc"u8)), Sel("initWithCGImage:"u8), cgImage);
        try
        {
            ((delegate* unmanaged<nint, nint, NSSize, void>)s_send)(rep, Sel("setSize:"u8), ((delegate* unmanaged<nint, nint, NSSize>)s_send)(image, Sel("size"u8)));
            var data = ((delegate* unmanaged<nint, nint, nuint, nint, nint>)s_send)(rep, Sel("representationUsingType:properties:"u8), PngFileType,
                Send(Class("NSDictionary"u8), Sel("dictionary"u8)));
            if (data == 0) return null;
            var length = (int)(nuint)Send(data, Sel("length"u8));
            var bytes = new byte[length];
            Marshal.Copy(Send(data, Sel("bytes"u8)), bytes, 0, length);
            return bytes;
        }
        finally { Send(rep, Sel("release"u8)); }
    }

    private static T WithPool<T>(Func<T> func)
    {
        var pool = objc_autoreleasePoolPush();
        try { return func(); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static nint NSString(string s)
    {
        fixed (char* p = s)
            return ((delegate* unmanaged<nint, nint, char*, nuint, nint>)s_send)(Class("NSString"u8), Sel("stringWithCharacters:length:"u8), p, (nuint)s.Length);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_send)(receiver, selector);

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
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
}
