using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>One bitmap of a type icon: premultiplied BGRA, top row first.</summary>
public sealed record MacIconBitmap(double Scale, int Width, int Height, byte[] Bgra);

/// <summary>Chromium's macOS <c>IconLoader</c> (chrome/browser/icon_loader_mac.mm): icons by uniform type, drawn as
/// gfx's <c>ImageSkiaFromResizedNSImage</c> draws them. NSWorkspace is thread-safe, so any thread.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacFileIcon
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string UniformTypeIdentifiers = "/System/Library/Frameworks/UniformTypeIdentifiers.framework/UniformTypeIdentifiers";
    // kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little: BGRA in memory.
    private const uint Bgra = 2 | (2 << 12);
    private const int ColorSpaceModelRgb = 1;
    private const nuint CompositingOperationCopy = 1;

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly Lazy<bool> s_frameworks = new(() => NativeLibrary.TryLoad(AppKit, out _) && NativeLibrary.TryLoad(UniformTypeIdentifiers, out _));
    private static readonly Lazy<nint> s_srgb = new(() => *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(CoreGraphics), "kCGColorSpaceSRGB"));
    private static readonly Lazy<nint> s_contentTypeKey = new(() => *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(Foundation), "NSURLContentTypeKey"));
    private static readonly Lazy<nint> s_hintCtm = new(() => *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(AppKit), "NSImageHintCTM"));

    /// <summary><c>IconLoader::GroupForFilepath</c>: the content type of the file at <paramref name="path"/>, else of
    /// <paramref name="extension"/> (the final extension without its dot; null when the name has none), else
    /// <c>public.content</c>, as a type identifier.</summary>
    public static string TypeFor(string path, string? extension)
    {
        if (!s_frameworks.Value) return "public.content";
        var pool = objc_autoreleasePoolPush();
        try
        {
            // base::apple::FilePathToNSURL is nil for an empty path; fileURLWithPath: would raise.
            if (path.Length > 0)
            {
                var url = Send(Class("NSURL"u8), Sel("fileURLWithPath:"u8), NSString(path));
                nint type = 0;
                if (((delegate* unmanaged<nint, nint, nint*, nint, nint, byte>)s_msgSend)(url, Sel("getResourceValue:forKey:error:"u8), &type, s_contentTypeKey.Value, 0) != 0
                    && type != 0)
                    return Identifier(type);
            }
            var byExtension = extension is null ? 0 : Send(Class("UTType"u8), Sel("typeWithFilenameExtension:"u8), NSString(extension));
            return byExtension != 0 ? Identifier(byExtension) : "public.content";
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary><c>IconLoader::ReadIcon</c>: NSWorkspace's icon for the type <paramref name="type"/>, drawn at
    /// <paramref name="points"/> square for each of <paramref name="scales"/>; empty when the icon has no
    /// representations.</summary>
    public static IReadOnlyList<MacIconBitmap> Icon(string type, int points, IReadOnlyList<double> scales)
    {
        var result = new List<MacIconBitmap>();
        if (!s_frameworks.Value) return result;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var utType = Send(Class("UTType"u8), Sel("typeWithIdentifier:"u8), NSString(type));
            var image = utType == 0 ? 0 : Send(Send(Class("NSWorkspace"u8), Sel("sharedWorkspace"u8)), Sel("iconForContentType:"u8), utType);
            if (image == 0 || Send(Send(image, Sel("representations"u8)), Sel("count"u8)) == 0) return result;
            foreach (var scale in scales)
            {
                var transform = Send(Class("NSAffineTransform"u8), Sel("transform"u8));
                ((delegate* unmanaged<nint, nint, double, void>)s_msgSend)(transform, Sel("scaleBy:"u8), scale);
                var hints = Send(Class("NSDictionary"u8), Sel("dictionaryWithObject:forKey:"u8), transform, s_hintCtm.Value);
                var rep = ((delegate* unmanaged<nint, nint, CGRect, nint, nint, nint>)s_msgSend)(
                    image, Sel("bestRepresentationForRect:context:hints:"u8), new CGRect { Width = points, Height = points }, 0, hints);
                var pixels = (int)(points * scale);
                if (Draw(rep, pixels, pixels) is { } bgra) result.Add(new MacIconBitmap(scale, pixels, pixels, bgra));
            }
            return result;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    // skia::NSImageRepToSkBitmap: a bitmap rep's own RGB colour space, else sRGB. A nil rep leaves the bitmap clear.
    private static byte[]? Draw(nint rep, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var pixels = new byte[width * height * 4];
        var space = ColorSpaceFor(rep);
        try
        {
            fixed (byte* p = pixels)
            {
                var context = CGBitmapContextCreate(p, (nuint)width, (nuint)height, 8, (nuint)(width * 4), space, Bgra);
                if (context == 0) return null;
                try
                {
                    var graphics = Class("NSGraphicsContext"u8);
                    Send(graphics, Sel("saveGraphicsState"u8));
                    var cocoa = ((delegate* unmanaged<nint, nint, nint, byte, nint>)s_msgSend)(graphics, Sel("graphicsContextWithCGContext:flipped:"u8), context, 0);
                    Send(graphics, Sel("setCurrentContext:"u8), cocoa);
                    if (rep != 0)
                        ((delegate* unmanaged<nint, nint, CGRect, CGRect, nuint, double, byte, nint, byte>)s_msgSend)(
                            rep, Sel("drawInRect:fromRect:operation:fraction:respectFlipped:hints:"u8),
                            new CGRect { Width = width, Height = height }, default, CompositingOperationCopy, 1.0, 0, 0);
                    Send(graphics, Sel("restoreGraphicsState"u8));
                }
                finally { CFRelease(context); }
            }
        }
        finally { CFRelease(space); }
        return pixels;
    }

    // Skia keeps only colour spaces it can express, so a grey or CMYK rep falls back to sRGB as in Chromium.
    private static nint ColorSpaceFor(nint rep)
    {
        if (rep != 0 && ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(rep, Sel("isKindOfClass:"u8), Class("NSBitmapImageRep"u8)) != 0)
        {
            var space = Send(Send(rep, Sel("colorSpace"u8)), Sel("CGColorSpace"u8));
            if (space != 0 && CGColorSpaceGetModel(space) == ColorSpaceModelRgb) return CGColorSpaceRetain(space);
        }
        return CGColorSpaceCreateWithName(s_srgb.Value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, Width, Height;
    }

    private static string Identifier(nint type) => Marshal.PtrToStringUTF8(Send(Send(type, Sel("identifier"u8)), Sel("UTF8String"u8))) ?? "";

    private static nint NSString(string s)
    {
        fixed (char* p = s)
            return ((delegate* unmanaged<nint, nint, char*, nuint, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithCharacters:length:"u8), p, (nuint)s.Length);
    }

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static nint Send(nint receiver, nint selector, nint arg1, nint arg2)
        => ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg1, arg2);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(CoreGraphics)] private static extern nint CGColorSpaceCreateWithName(nint name);
    [DllImport(CoreGraphics)] private static extern nint CGColorSpaceRetain(nint space);
    [DllImport(CoreGraphics)] private static extern int CGColorSpaceGetModel(nint space);
    [DllImport(CoreGraphics)] private static extern nint CGBitmapContextCreate(void* data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint cf);
}
