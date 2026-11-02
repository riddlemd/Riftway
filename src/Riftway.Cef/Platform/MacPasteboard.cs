using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>The general pasteboard and the find pasteboard through raw <c>objc_msgSend</c>: the AppKit calls of
/// Chromium's <c>ClipboardMac</c>, <c>clipboard_util_mac</c> and <c>FindPasteboard</c>, one method per call site.</summary>
/// <remarks>Each method runs in its own autorelease pool. Chromium makes these calls on the browser UI thread, except
/// <see cref="ImagePng"/>, which it runs on the thread pool.</remarks>
[SupportedOSPlatform("macos")]
public static unsafe class MacPasteboard
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string FileUrlType = "public.file-url";
    private const uint Utf8Encoding = 0x08000100; // kCFStringEncodingUTF8
    private const nint PosixPathStyle = 0; // kCFURLPOSIXPathStyle
    private const nuint PngFileType = 4; // NSBitmapImageFileTypePNG
    private const uint PremultipliedFirstLittle = 2 | (2 << 12); // kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize
    {
        public double Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRange
    {
        public nuint Location, Length;
    }

    /// <summary><c>NSPasteboard.generalPasteboard.types</c>, copied.</summary>
    public static IReadOnlyList<string> Types() => Pooled(() => Strings(Send(General(), Sel("types"u8))));

    /// <summary><c>[pasteboard availableTypeFromArray:]</c>: the first of <paramref name="types"/> the pasteboard holds, or
    /// null.</summary>
    public static string? AvailableType(params string[] types) => Pooled(() =>
    {
        var array = Send(Class("NSMutableArray"u8), Sel("array"u8));
        foreach (var type in types) SendObject(array, Sel("addObject:"u8), NSString(type));
        return ManagedString(SendObject(General(), Sel("availableTypeFromArray:"u8), array));
    });

    /// <summary><c>[pasteboard stringForType:]</c>, or null.</summary>
    public static string? String(string type) => Pooled(() => ManagedString(SendObject(General(), Sel("stringForType:"u8), NSString(type))));

    /// <summary><c>[pasteboard dataForType:]</c>, or null.</summary>
    public static byte[]? Data(string type) => Pooled(() => Bytes(SendObject(General(), Sel("dataForType:"u8), NSString(type))));

    /// <summary><c>[NSImage canInitWithPasteboard:]</c>.</summary>
    public static bool CanReadImage() => Pooled(() => SendBoolObject(Class("NSImage"u8), Sel("canInitWithPasteboard:"u8), General()));

    /// <summary>Chromium's <c>GetNSImage</c>: a retained NSImage of the pasteboard with at least one representation, or
    /// 0. Pass it to <see cref="ImagePng"/>, then <see cref="Release"/>.</summary>
    /// <remarks>Chromium catches the exception AppKit may throw for malformed image data; an Objective-C exception
    /// cannot be caught here.</remarks>
    public static nint CopyImage() => Pooled(() =>
    {
        var image = SendObject(Send(Class("NSImage"u8), Sel("alloc"u8)), Sel("initWithPasteboard:"u8), General());
        if (image == 0) return 0;
        if (Send(Send(image, Sel("representations"u8)), Sel("count"u8)) == 0)
        {
            Release(image);
            return 0;
        }
        return image;
    });

    /// <summary>gfx's <c>Get1xPNGBytesFromNSImage</c>: the image's CGImage at an identity transform, as PNG; empty if
    /// it has none.</summary>
    public static byte[] ImagePng(nint image) => Pooled(() =>
    {
        var transform = Send(Class("NSAffineTransform"u8), Sel("transform"u8));
        var hints = SendObjectObject(Class("NSDictionary"u8), Sel("dictionaryWithObject:forKey:"u8), transform, AppKitString("NSImageHintCTM"));
        var cgImage = ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)s_msgSend)(image, Sel("CGImageForProposedRect:context:hints:"u8), 0, 0, hints);
        if (cgImage == 0) return [];
        return PngOf(cgImage);
    });

    /// <summary><c>-[NSObject release]</c>.</summary>
    public static void Release(nint obj)
    {
        if (obj != 0) Send(obj, Sel("release"u8));
    }

    /// <summary><c>clipboard_util::GetHTMLFromRTFOnPasteboard</c>: the RTF converted to HTML by
    /// <c>NSAttributedString</c>, or null without RTF.</summary>
    public static string? HtmlFromRtf() => Pooled(() =>
    {
        var rtf = SendObject(General(), Sel("dataForType:"u8), NSString("public.rtf"));
        if (rtf == 0) return null;
        var attributed = ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_msgSend)(Send(Class("NSAttributedString"u8), Sel("alloc"u8)), Sel("initWithRTF:documentAttributes:"u8), rtf, 0);
        var attributes = SendObjectObject(Class("NSDictionary"u8), Sel("dictionaryWithObject:forKey:"u8), AppKitString("NSHTMLTextDocumentType"), AppKitString("NSDocumentTypeDocumentAttribute"));
        var length = attributed == 0 ? 0 : (nuint)Send(attributed, Sel("length"u8));
        var html = attributed == 0 ? 0 : ((delegate* unmanaged<nint, nint, NSRange, nint, nint, nint>)s_msgSend)(attributed, Sel("dataFromRange:documentAttributes:error:"u8), new NSRange { Length = length }, attributes, 0);
        Release(attributed);
        // initWithData:nil answers nil, which reads as "".
        var text = ((delegate* unmanaged<nint, nint, nint, nuint, nint>)s_msgSend)(Send(Class("NSString"u8), Sel("alloc"u8)), Sel("initWithData:encoding:"u8), html, 4 /* NSUTF8StringEncoding */);
        var result = ManagedString(text) ?? "";
        Release(text);
        return result;
    });

    /// <summary><c>clipboard_util::FilesFromPasteboard</c>: the path of each item's file URL, in the decomposed form
    /// <c>base::FilePath</c> keeps ("" for a URL that is not a file path).</summary>
    public static IReadOnlyList<string> FilePaths() => Pooled(() =>
    {
        var paths = new List<string>();
        var items = Send(General(), Sel("pasteboardItems"u8));
        for (nint i = 0, n = items == 0 ? 0 : Send(items, Sel("count"u8)); i < n; i++)
        {
            var item = SendInteger(items, Sel("objectAtIndex:"u8), i);
            var text = SendObject(item, Sel("stringForType:"u8), NSString(FileUrlType));
            if (text == 0) continue;
            var url = Send(SendObject(Class("NSURL"u8), Sel("URLWithString:"u8), text), Sel("filePathURL"u8));
            paths.Add(FilePathOf(url));
        }
        return paths;
    });

    /// <summary><c>[pasteboard clearContents]</c>.</summary>
    public static void Clear() => Pooled(() => Send(General(), Sel("clearContents"u8)));

    /// <summary><c>[pasteboard prepareForNewContentsWithOptions:0]</c>: empties it for a write.</summary>
    public static void BeginWrite() => Pooled(() => SendInteger(General(), Sel("prepareForNewContentsWithOptions:"u8), 0));

    /// <summary><c>[pasteboard setData:forType:]</c>.</summary>
    public static void SetData(string type, byte[] data) => Pooled(() => SendObjectObject(General(), Sel("setData:forType:"u8), NSData(data), NSString(type)));

    /// <summary><c>[pasteboard setString:forType:]</c> of <c>base::SysUTF8ToNSString(text)</c>.</summary>
    public static void SetString(string type, string text) => Pooled(() => SendObjectObject(General(), Sel("setString:forType:"u8), NSString(text), NSString(type)));

    /// <summary>Chromium's <c>WriteBitmapInternal</c> of a premultiplied BGRA bitmap in sRGB (skia's
    /// <c>SkCreateCGImageRef</c>): a PNG item, or a TIFF-only NSImage if that fails.</summary>
    public static void WriteBitmap(int width, int height, byte[] premultipliedBgra) => Pooled(() =>
    {
        var cgImage = CreateImage(width, height, premultipliedBgra);
        var rep = SendObject(Send(Class("NSBitmapImageRep"u8), Sel("alloc"u8)), Sel("initWithCGImage:"u8), cgImage);
        CFRelease(cgImage);
        var png = ((delegate* unmanaged<nint, nint, nuint, nint, nint>)s_msgSend)(rep, Sel("representationUsingType:properties:"u8), PngFileType, Send(Class("NSDictionary"u8), Sel("dictionary"u8)));
        if (png != 0)
        {
            var item = Send(Send(Class("NSPasteboardItem"u8), Sel("alloc"u8)), Sel("init"u8));
            SendObjectObject(item, Sel("setData:forType:"u8), png, NSString("public.png"));
            var written = SendBoolObject(General(), Sel("writeObjects:"u8), Array1(item));
            Release(item);
            if (written)
            {
                Release(rep);
                return;
            }
        }
        var image = Send(Send(Class("NSImage"u8), Sel("alloc"u8)), Sel("init"u8));
        SendObject(image, Sel("addRepresentation:"u8), rep);
        ((delegate* unmanaged<nint, nint, NSSize, void>)s_msgSend)(image, Sel("setSize:"u8), new NSSize { Width = width, Height = height });
        SendBoolObject(General(), Sel("writeObjects:"u8), Array1(image));
        Release(image);
        Release(rep);
    });

    /// <summary>Chromium's <c>WriteURL</c>: <c>PasteboardItemsFromUrls</c> for one URL, then <c>AddDataToPasteboard</c>
    /// adds the item's types the pasteboard lacks.</summary>
    public static void WriteUrl(string url, string title) => Pooled(() =>
    {
        var item = Send(Send(Class("NSPasteboardItem"u8), Sel("alloc"u8)), Sel("init"u8));
        var urlString = NSString(url);
        var titleString = NSString(title);
        var nsurl = SendObject(Class("NSURL"u8), Sel("URLWithString:"u8), urlString);
        if (nsurl != 0 && SendBool(nsurl, Sel("isFileURL"u8)) && SendBoolObject(nsurl, Sel("checkResourceIsReachableAndReturnError:"u8), 0))
            SendObjectObject(item, Sel("setString:forType:"u8), urlString, NSString(FileUrlType));
        SendObjectObject(item, Sel("setString:forType:"u8), urlString, NSString("public.utf8-plain-text"));
        SendObjectObject(item, Sel("setString:forType:"u8), urlString, NSString("public.url"));
        if (title.Length > 0) SendObjectObject(item, Sel("setString:forType:"u8), titleString, NSString("public.url-name"));
        var pair = Array2(Array1(urlString), Array1(titleString));
        SendObjectObject(item, Sel("setPropertyList:forType:"u8), pair, NSString("dyn.ah62d4rv4gu8zs3pcnzme2641rf4guzdmsv0gn64uqm10c6xenv61a3k"));

        var pasteboard = General();
        var oldTypes = SendObject(Class("NSSet"u8), Sel("setWithArray:"u8), Send(pasteboard, Sel("types"u8)));
        var newTypes = SendObject(Class("NSMutableSet"u8), Sel("setWithArray:"u8), Send(item, Sel("types"u8)));
        SendObject(newTypes, Sel("minusSet:"u8), oldTypes);
        var added = Send(newTypes, Sel("allObjects"u8));
        SendObjectObject(pasteboard, Sel("addTypes:owner:"u8), added, 0);
        for (nint i = 0, n = Send(added, Sel("count"u8)); i < n; i++)
        {
            var type = SendInteger(added, Sel("objectAtIndex:"u8), i);
            SendObjectObject(pasteboard, Sel("setData:forType:"u8), SendObject(item, Sel("dataForType:"u8), type), type);
        }
        Release(item);
    });

    /// <summary><c>clipboard_util::WriteFilesToPasteboard</c>: one item per path holding its file URL
    /// (<c>base::apple::FilePathToNSURL</c>, never marked as a directory).</summary>
    public static void WriteFiles(IReadOnlyList<string> paths) => Pooled(() =>
    {
        if (paths.Count == 0) return;
        var items = SendInteger(Class("NSMutableArray"u8), Sel("arrayWithCapacity:"u8), paths.Count);
        foreach (var path in paths)
        {
            var url = FileUrlOf(path);
            var item = Send(Send(Class("NSPasteboardItem"u8), Sel("alloc"u8)), Sel("init"u8));
            if (url != 0) SendObjectObject(item, Sel("setString:forType:"u8), Send(url, Sel("absoluteString"u8)), NSString(FileUrlType));
            SendObject(items, Sel("addObject:"u8), item);
            Release(item);
        }
        SendBoolObject(General(), Sel("writeObjects:"u8), items);
    });

    /// <summary><c>[[FindPasteboard sharedInstance] findText]</c>: Chromium's copy of the find pasteboard, refreshed when
    /// the app becomes active. <paramref name="chromium"/> false (CEF not running) reads the find pasteboard
    /// itself.</summary>
    public static string FindText(bool chromium) => Pooled(() =>
    {
        if (chromium && FindPasteboardInstance() is var shared and not 0) return ManagedString(Send(shared, Sel("findText"u8))) ?? "";
        var strings = SendObjectObject(FindPasteboard(), Sel("readObjectsForClasses:options:"u8), Array1(Class("NSString"u8)), 0);
        return strings != 0 && Send(strings, Sel("count"u8)) > 0 ? ManagedString(SendInteger(strings, Sel("objectAtIndex:"u8), 0)) ?? "" : "";
    });

    /// <summary><c>[[FindPasteboard sharedInstance] setFindText:]</c>: writes the find pasteboard when the text differs
    /// from Chromium's copy. <paramref name="chromium"/> false (CEF not running) writes the find pasteboard
    /// itself.</summary>
    public static void SetFindText(string text, bool chromium) => Pooled(() =>
    {
        var value = NSString(text);
        if (chromium && FindPasteboardInstance() is var shared and not 0)
        {
            SendObject(shared, Sel("setFindText:"u8), value);
            return;
        }
        var pasteboard = FindPasteboard();
        Send(pasteboard, Sel("clearContents"u8));
        SendBoolObject(pasteboard, Sel("writeObjects:"u8), Array1(value));
    });

    // Chromium's own singleton, inside the CEF framework, so the find bar and this API share one copy.
    private static nint FindPasteboardInstance()
    {
        var cls = Class("FindPasteboard"u8);
        return cls == 0 ? 0 : Send(cls, Sel("sharedInstance"u8));
    }

    private static nint FindPasteboard() => SendObject(Class("NSPasteboard"u8), Sel("pasteboardWithName:"u8), AppKitString("NSPasteboardNameFind"));

    private static nint General() => Send(Class("NSPasteboard"u8), Sel("generalPasteboard"u8));

    private static byte[] PngOf(nint cgImage)
    {
        var rep = SendObject(Send(Class("NSBitmapImageRep"u8), Sel("alloc"u8)), Sel("initWithCGImage:"u8), cgImage);
        var png = ((delegate* unmanaged<nint, nint, nuint, nint, nint>)s_msgSend)(rep, Sel("representationUsingType:properties:"u8), PngFileType, Send(Class("NSDictionary"u8), Sel("dictionary"u8)));
        var bytes = Bytes(png) ?? [];
        Release(rep);
        return bytes;
    }

    private static nint CreateImage(int width, int height, byte[] bgra)
    {
        nint data;
        fixed (byte* p = bgra) data = CFDataCreate(0, p, bgra.Length);
        var provider = CGDataProviderCreateWithCFData(data);
        CFRelease(data);
        var srgb = *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(CoreGraphics), "kCGColorSpaceSRGB");
        var space = CGColorSpaceCreateWithName(srgb);
        var image = CGImageCreate(width, height, 8, 32, width * 4, space, PremultipliedFirstLittle, provider, 0, 0, 0);
        CGColorSpaceRelease(space);
        CGDataProviderRelease(provider);
        return image;
    }

    // base::apple::FilePathToNSURL: the path's file-system string as a POSIX file URL, or 0.
    private static nint FileUrlOf(string path)
    {
        if (path.Length == 0) return 0;
        var utf8 = Encoding.UTF8.GetBytes(path + "\0");
        nint text;
        fixed (byte* p = utf8) text = CFStringCreateWithFileSystemRepresentation(0, p);
        if (text == 0) return 0;
        var url = CFURLCreateWithFileSystemPath(0, text, PosixPathStyle, 0);
        CFRelease(text);
        return url == 0 ? 0 : Autorelease(url);
    }

    // base::apple::NSURLToFilePath then FilePath::GetHFSDecomposedForm.
    private static string FilePathOf(nint url)
    {
        if (url == 0 || !SendBool(url, Sel("isFileURL"u8))) return "";
        var path = CFURLCopyFileSystemPath(url, PosixPathStyle);
        if (path == 0) return "";
        try
        {
            if (CFStringGetLength(path) == 0) return "";
            var size = CFStringGetMaximumSizeOfFileSystemRepresentation(path);
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                if (CFStringGetFileSystemRepresentation(path, p, size) == 0) return "";
                return Marshal.PtrToStringUTF8((nint)p) ?? "";
            }
        }
        finally { CFRelease(path); }
    }

    private static nint Autorelease(nint obj) => Send(obj, Sel("autorelease"u8));

    // An AppKit NSString constant, read from its exported symbol.
    private static nint AppKitString(string symbol) => *(nint*)NativeLibrary.GetExport(s_appKit, symbol);

    // base::SysUTF8ToNSString, autoreleased: invalid UTF-16 becomes U+FFFD on the way through UTF-8.
    private static nint NSString(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        nint value;
        fixed (byte* p = utf8) value = CFStringCreateWithBytes(0, p, utf8.Length, Utf8Encoding, 0);
        return Autorelease(value);
    }

    private static nint NSData(byte[] data)
    {
        fixed (byte* p = data)
            return ((delegate* unmanaged<nint, nint, byte*, nuint, nint>)s_msgSend)(Class("NSData"u8), Sel("dataWithBytes:length:"u8), p, (nuint)data.Length);
    }

    private static nint Array1(nint obj) => SendObject(Class("NSArray"u8), Sel("arrayWithObject:"u8), obj);

    private static nint Array2(nint first, nint second)
    {
        var objects = stackalloc nint[] { first, second };
        return ((delegate* unmanaged<nint, nint, nint*, nuint, nint>)s_msgSend)(Class("NSArray"u8), Sel("arrayWithObjects:count:"u8), objects, 2);
    }

    // base::SysNSStringToUTF16: the UTF-16 code units, or null for nil.
    private static string? ManagedString(nint value)
    {
        if (value == 0) return null;
        var length = (int)Send(value, Sel("length"u8));
        var chars = new char[length];
        fixed (char* p = chars)
            ((delegate* unmanaged<nint, nint, char*, NSRange, void>)s_msgSend)(value, Sel("getCharacters:range:"u8), p, new NSRange { Length = (nuint)length });
        return new string(chars);
    }

    private static byte[]? Bytes(nint data)
    {
        if (data == 0) return null;
        var length = (int)Send(data, Sel("length"u8));
        var bytes = new byte[length];
        if (length > 0) Marshal.Copy(Send(data, Sel("bytes"u8)), bytes, 0, length);
        return bytes;
    }

    private static List<string> Strings(nint array)
    {
        var strings = new List<string>();
        if (array == 0) return strings;
        for (nint i = 0, n = Send(array, Sel("count"u8)); i < n; i++)
            if (ManagedString(SendInteger(array, Sel("objectAtIndex:"u8), i)) is { } value) strings.Add(value);
        return strings;
    }

    private static T Pooled<T>(Func<T> body)
    {
        var pool = objc_autoreleasePoolPush();
        try { return body(); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static void Pooled(Action body) => Pooled(() => { body(); return 0; });

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);
    private static nint SendObject(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);
    private static nint SendObjectObject(nint receiver, nint selector, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_msgSend)(receiver, selector, a, b);
    private static nint SendInteger(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);
    private static bool SendBool(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, byte>)s_msgSend)(receiver, selector) != 0;
    private static bool SendBoolObject(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(receiver, selector, arg) != 0;

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint obj);
    [DllImport(CoreFoundation)] private static extern nint CFDataCreate(nint allocator, byte* bytes, nint length);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithBytes(nint allocator, byte* bytes, nint length, uint encoding, byte isExternalRepresentation);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithFileSystemRepresentation(nint allocator, byte* buffer);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(nint text);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetMaximumSizeOfFileSystemRepresentation(nint text);
    [DllImport(CoreFoundation)] private static extern byte CFStringGetFileSystemRepresentation(nint text, byte* buffer, nint maxLength);
    [DllImport(CoreFoundation)] private static extern nint CFURLCreateWithFileSystemPath(nint allocator, nint path, nint style, byte isDirectory);
    [DllImport(CoreFoundation)] private static extern nint CFURLCopyFileSystemPath(nint url, nint style);
    [DllImport(CoreGraphics)] private static extern nint CGDataProviderCreateWithCFData(nint data);
    [DllImport(CoreGraphics)] private static extern void CGDataProviderRelease(nint provider);
    [DllImport(CoreGraphics)] private static extern nint CGColorSpaceCreateWithName(nint name);
    [DllImport(CoreGraphics)] private static extern void CGColorSpaceRelease(nint space);
    [DllImport(CoreGraphics)] private static extern nint CGImageCreate(nint width, nint height, nint bitsPerComponent, nint bitsPerPixel, nint bytesPerRow, nint space, uint bitmapInfo, nint provider, nint decode, byte interpolate, int intent);
}
