using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>gfx's <c>NSImageFromImageSkia</c> for Oriel's images: an NSImage with a bitmap representation per scale.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacImages
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize
    {
        public double Width, Height;
    }

    /// <summary>A new NSImage (+1) of the PNG representations, sized in DIPs from the first; 0 for none.</summary>
    public static nint FromRepresentations(IReadOnlyList<(byte[] Png, double Scale)> representations, bool template)
    {
        if (representations.Count == 0) return 0;
        var image = Send(Send(Class("NSImage"u8), Sel("alloc"u8)), Sel("init"u8));
        NSSize? size = null;
        var pool = objc_autoreleasePoolPush();
        try
        {
            foreach (var (png, scale) in representations)
            {
                nint data;
                fixed (byte* p = png) data = ((delegate* unmanaged<nint, nint, byte*, nuint, nint>)s_msgSend)(Class("NSData"u8), Sel("dataWithBytes:length:"u8), p, (nuint)png.Length);
                var rep = ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(Class("NSBitmapImageRep"u8), Sel("imageRepWithData:"u8), data);
                if (rep == 0) continue;
                var points = new NSSize { Width = Send(rep, Sel("pixelsWide"u8)) / scale, Height = Send(rep, Sel("pixelsHigh"u8)) / scale };
                ((delegate* unmanaged<nint, nint, NSSize, void>)s_msgSend)(rep, Sel("setSize:"u8), points);
                ((delegate* unmanaged<nint, nint, nint, void>)s_msgSend)(image, Sel("addRepresentation:"u8), rep);
                size ??= points;
            }
        }
        finally { objc_autoreleasePoolPop(pool); }
        if (size is { } s) ((delegate* unmanaged<nint, nint, NSSize, void>)s_msgSend)(image, Sel("setSize:"u8), s);
        if (template) ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(image, Sel("setTemplate:"u8), 1);
        return image;
    }

    /// <summary>Releases an image from <see cref="FromRepresentations"/>; 0 is ignored.</summary>
    public static void Release(nint image)
    {
        if (image != 0) Send(image, Sel("release"u8));
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

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
}
