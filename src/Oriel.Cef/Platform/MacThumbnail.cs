using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>One bitmap of a thumbnail: premultiplied BGRA, top row first.</summary>
public sealed record ThumbnailBitmap(double Scale, int Width, int Height, byte[] Bgra);

/// <summary>Quick Look thumbnails as Electron's <c>NativeImage::CreateThumbnailFromPath</c> makes them on macOS
/// (shell/common/api/electron_api_native_image_mac.mm).</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacThumbnail
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string QuickLookThumbnailing = "/System/Library/Frameworks/QuickLookThumbnailing.framework/QuickLookThumbnailing";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    // kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little: BGRA in memory.
    private const uint Bgra = 2 | (2 << 12);
    // QLThumbnailGenerationRequestRepresentationTypeAll.
    private static readonly nuint AllRepresentations = nuint.MaxValue;

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_libSystem = NativeLibrary.Load(LibSystem);
    private static readonly Lazy<bool> s_frameworks = new(() => NativeLibrary.TryLoad(AppKit, out _) && NativeLibrary.TryLoad(QuickLookThumbnailing, out _));
    private static readonly Lazy<nint> s_srgb = new(() => *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(CoreGraphics), "kCGColorSpaceSRGB"));

    /// <summary><c>[[NSFileManager defaultManager] fileExistsAtPath:]</c>: a file or a directory.</summary>
    public static bool Exists(string path)
    {
        var pool = objc_autoreleasePoolPush();
        try { return ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(Send(Class("NSFileManager"u8), Sel("defaultManager"u8)), Sel("fileExistsAtPath:"u8), NSString(path)) != 0; }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary><c>[[NSScreen screens] firstObject].backingScaleFactor</c>, or 1 without a screen. Main thread.</summary>
    public static double FirstScreenScale()
    {
        _ = s_frameworks.Value;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var screen = Send(Send(Class("NSScreen"u8), Sel("screens"u8)), Sel("firstObject"u8));
            return screen == 0 ? 1 : ((delegate* unmanaged<nint, nint, double>)s_msgSend)(screen, Sel("backingScaleFactor"u8));
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>Asks <c>QLThumbnailGenerator</c> for the best representation of <paramref name="path"/> at
    /// <paramref name="width"/> x <paramref name="height"/> points and <paramref name="scale"/>. <paramref name="done"/>
    /// gets, on Quick Look's queue, the thumbnail stretched to the requested size at each of
    /// <paramref name="renderScales"/> (as <c>[[NSImage alloc] initWithCGImage:size:]</c> then gfx's
    /// <c>ImageSkiaFromNSImage</c>), or null and the error's <c>localizedDescription</c> (null without one).</summary>
    public static void Generate(string path, int width, int height, double scale, IReadOnlyList<double> renderScales,
        Action<IReadOnlyList<ThumbnailBitmap>?, string?> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        if (!s_frameworks.Value)
        {
            done(null, null);
            return;
        }
        var scales = renderScales.ToArray();
        var block = NewBlock((thumbnail, error) =>
        {
            if (error != 0 || thumbnail == 0)
            {
                done(null, error == 0 ? null : NSStringValue(Send(error, Sel("localizedDescription"u8))));
                return;
            }
            var image = Send(thumbnail, Sel("CGImage"u8));
            var bitmaps = new List<ThumbnailBitmap>();
            foreach (var s in scales)
                if (Draw(image, (int)(width * s), (int)(height * s)) is { } pixels) bitmaps.Add(new ThumbnailBitmap(s, (int)(width * s), (int)(height * s), pixels));
            done(bitmaps, null);
        });
        var pool = objc_autoreleasePoolPush();
        try
        {
            var url = Send(Class("NSURL"u8), Sel("fileURLWithPath:"u8), NSString(path));
            var request = ((delegate* unmanaged<nint, nint, nint, CGSize, double, nuint, nint>)s_msgSend)(
                Send(Class("QLThumbnailGenerationRequest"u8), Sel("alloc"u8)), Sel("initWithFileAtURL:size:scale:representationTypes:"u8),
                url, new CGSize { Width = width, Height = height }, scale, AllRepresentations);
            var generator = Send(Class("QLThumbnailGenerator"u8), Sel("sharedGenerator"u8));
            ((delegate* unmanaged<nint, nint, nint, Block*, void>)s_msgSend)(generator, Sel("generateBestRepresentationForRequest:completionHandler:"u8), request, block);
            Send(request, Sel("release"u8));
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    // Drawn into an sRGB context, the space Chromium's NSImageRepToSkBitmap falls back to for a non-bitmap rep.
    private static byte[]? Draw(nint image, int width, int height)
    {
        if (image == 0 || width <= 0 || height <= 0) return null;
        var pixels = new byte[width * height * 4];
        var space = CGColorSpaceCreateWithName(s_srgb.Value);
        try
        {
            fixed (byte* p = pixels)
            {
                var context = CGBitmapContextCreate(p, (nuint)width, (nuint)height, 8, (nuint)(width * 4), space, Bgra);
                if (context == 0) return null;
                CGContextDrawImage(context, new CGRect { Width = width, Height = height }, image);
                CFRelease(context);
            }
        }
        finally { CFRelease(space); }
        return pixels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Block
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public delegate* unmanaged<Block*, nint, nint, void> Invoke;
        public BlockDescriptor* Descriptor;
        public nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    // A global-class block carrying a GCHandle: Block_copy hands back the same memory, so the handle survives.
    private static Block* NewBlock(Action<nint, nint> callback)
    {
        var block = (Block*)NativeMemory.AllocZeroed((nuint)(sizeof(Block) + sizeof(BlockDescriptor)));
        var descriptor = (BlockDescriptor*)(block + 1);
        descriptor->Size = (nuint)sizeof(Block);
        block->Isa = NativeLibrary.GetExport(s_libSystem, "_NSConcreteGlobalBlock");
        // BLOCK_IS_GLOBAL.
        block->Flags = 1 << 28;
        block->Invoke = &OnBlock;
        block->Descriptor = descriptor;
        block->Context = GCHandle.ToIntPtr(GCHandle.Alloc(callback));
        return block;
    }

    [UnmanagedCallersOnly]
    private static void OnBlock(Block* block, nint thumbnail, nint error)
    {
        var handle = GCHandle.FromIntPtr(block->Context);
        var callback = (Action<nint, nint>)handle.Target!;
        handle.Free();
        try { callback(thumbnail, error); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a thumbnail callback: {e}"); }
        // Never freed: a copy of a global block is the block itself, so whoever holds it may read its flags at any time.
    }

    private static nint NSString(string s)
    {
        fixed (char* p = s)
            return ((delegate* unmanaged<nint, nint, char*, nuint, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithCharacters:length:"u8), p, (nuint)s.Length);
    }

    private static string NSStringValue(nint s) => s == 0 ? "" : Marshal.PtrToStringUTF8(Send(s, Sel("UTF8String"u8))) ?? "";

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

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(CoreGraphics)] private static extern nint CGColorSpaceCreateWithName(nint name);
    [DllImport(CoreGraphics)] private static extern nint CGBitmapContextCreate(void* data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);
    [DllImport(CoreGraphics)] private static extern void CGContextDrawImage(nint context, CGRect rect, nint image);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint cf);
}
