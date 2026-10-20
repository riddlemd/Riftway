using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>A display as ScreenCaptureKit or CoreGraphics lists it: its CGDirectDisplayID and frame in points.</summary>
public sealed record MacDisplayEntry(uint Id, double X, double Y, double Width, double Height);

/// <summary>ScreenCaptureKit's shareable content: on-screen, non-desktop windows and the displays.</summary>
public sealed record MacShareableContent(IReadOnlyList<MacWindowEntry> Windows, IReadOnlyList<MacDisplayEntry> Displays);

/// <summary>The macOS side of Chromium's desktop media lists: <c>ThumbnailCapturerMac</c> (ScreenCaptureKit, macOS 14.4
/// and later) and webrtc's CoreGraphics capturers before it, the screen-recording permission, and window icons.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacDesktopCapture
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string ScreenCaptureKit = "/System/Library/Frameworks/ScreenCaptureKit.framework/ScreenCaptureKit";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private const uint OnScreenOnly = 1, IncludingWindow = 8, ExcludeDesktopElements = 16;
    private const uint ImageBoundsIgnoreFraming = 1;
    // kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little: BGRA in memory.
    private const uint Bgra = 2 | (2 << 12);

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_libSystem = NativeLibrary.Load(LibSystem);
    private static readonly Lazy<bool> s_screenCaptureKit = new(() => NativeLibrary.TryLoad(ScreenCaptureKit, out _));

    /// <summary>Whether Chromium 154 uses ScreenCaptureKit for both lists (<c>ShouldUseThumbnailCapturerMac</c>, with its
    /// features at their defaults): macOS 14.4 and later, where Apple fixed an SCK bug.</summary>
    public static bool UsesScreenCaptureKit => OperatingSystem.IsMacOSVersionAtLeast(14, 4) && s_screenCaptureKit.Value;

    /// <summary><c>CGPreflightScreenCaptureAccess</c>: whether screen recording is allowed, without asking.</summary>
    public static bool HasScreenCaptureAccess() => CGPreflightScreenCaptureAccess() != 0;

    /// <summary><c>ui::TryPromptUserForScreenCapture</c>: <c>CGRequestScreenCaptureAccess</c>, which shows the system
    /// prompt the first time and answers whether access is granted.</summary>
    public static bool RequestScreenCaptureAccess() => CGRequestScreenCaptureAccess() != 0;

    /// <summary>The active displays' ids, as webrtc's screen capturer lists them.</summary>
    public static IReadOnlyList<uint> ActiveDisplays()
    {
        uint count;
        if (CGGetActiveDisplayList(0, null, &count) != 0 || count == 0) return [];
        var ids = new uint[count];
        fixed (uint* p = ids)
            if (CGGetActiveDisplayList(count, p, &count) != 0) return [];
        return ids[..(int)count];
    }

    /// <summary>CGWindowList's on-screen, non-desktop windows, front to back, with every field webrtc and Chromium read.</summary>
    public static IReadOnlyList<MacWindowEntry> CoreGraphicsWindows() => WindowList(OnScreenOnly | ExcludeDesktopElements, 0);

    private static List<MacWindowEntry> WindowList(uint option, uint relativeTo)
    {
        var list = new List<MacWindowEntry>();
        var array = CGWindowListCopyWindowInfo(option, relativeTo);
        if (array == 0) return list;
        try
        {
            var cg = NativeLibrary.Load(CoreGraphics);
            nint Key(string name) => *(nint*)NativeLibrary.GetExport(cg, name);
            var number = Key("kCGWindowNumber");
            var layer = Key("kCGWindowLayer");
            var name = Key("kCGWindowName");
            var owner = Key("kCGWindowOwnerName");
            var pid = Key("kCGWindowOwnerPID");
            var onScreen = Key("kCGWindowIsOnscreen");
            var bounds = Key("kCGWindowBounds");
            for (nint i = 0, n = CFArrayGetCount(array); i < n; i++)
            {
                var window = CFArrayGetValueAtIndex(array, i);
                if (window == 0) continue;
                var id = Int(CFDictionaryGetValue(window, number));
                var windowLayer = CFDictionaryGetValue(window, layer);
                if (id is not { } windowId || windowLayer == 0) continue;
                var rect = default(CGRect);
                var boundsDict = CFDictionaryGetValue(window, bounds);
                if (boundsDict != 0) CGRectMakeWithDictionaryRepresentation(boundsDict, &rect);
                var titleRef = CFDictionaryGetValue(window, name);
                var onScreenRef = CFDictionaryGetValue(window, onScreen);
                list.Add(new MacWindowEntry((uint)windowId, titleRef == 0 ? null : CFString(titleRef), CFString(CFDictionaryGetValue(window, owner)),
                    (int)(Int(CFDictionaryGetValue(window, pid)) ?? 0), (int)(Int(windowLayer) ?? 0), rect.X, rect.Y, rect.Width, rect.Height,
                    onScreenRef != 0 && CFBooleanGetValue(onScreenRef) != 0));
            }
        }
        finally { CFRelease(array); }
        return list;
    }

    /// <summary>The CGWindowList on-screen ids, front to back, which <c>SortOrderByCGWindowList</c> sorts SCK's windows by.</summary>
    public static IReadOnlyList<uint> OnScreenOrder() => [.. CoreGraphicsWindows().Select(w => w.Id)];

    /// <summary><c>SCShareableContent getShareableContentExcludingDesktopWindows:YES onScreenWindowsOnly:YES</c>; null
    /// when ScreenCaptureKit answers with an error (as without screen-recording access).</summary>
    public static Task<MacShareableContent?> ShareableContentAsync()
    {
        var done = new TaskCompletionSource<MacShareableContent?>(TaskCreationOptions.RunContinuationsAsynchronously);
        GetShareableContent(content => done.TrySetResult(content == 0 ? null : ReadContent(content)));
        return done.Task;
    }

    /// <summary>Captures each id's thumbnail with <c>SCScreenshotManager</c> as Chromium's
    /// <c>ScreenshotManagerCapturer</c> does: scaled by SCK to the box at the source's aspect, no cursor. Each capture lands
    /// in <paramref name="results"/> as it arrives; the task ends when all have answered.</summary>
    public static Task CaptureAsync(bool screens, IReadOnlyList<uint> ids, int boxWidth, int boxHeight, ConcurrentDictionary<uint, CaptureBitmap> results)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ids.Count == 0)
        {
            done.SetResult();
            return done.Task;
        }
        GetShareableContent(content =>
        {
            if (content == 0)
            {
                done.TrySetResult();
                return;
            }
            var pending = ids.Count;
            void Answered()
            {
                if (Interlocked.Decrement(ref pending) == 0) done.TrySetResult();
            }
            var objects = Send(content, Sel(screens ? "displays"u8 : "windows"u8));
            foreach (var id in ids)
            {
                var source = Find(objects, screens, id);
                if (source == 0)
                {
                    Answered();
                    continue;
                }
                var frame = Rect(source);
                var filter = Send(Class("SCContentFilter"u8), Sel("alloc"u8));
                filter = screens
                    ? Send(filter, Sel("initWithDisplay:excludingWindows:"u8), source, Send(Class("NSArray"u8), Sel("array"u8)))
                    : Send(filter, Sel("initWithDesktopIndependentWindow:"u8), source);
                var config = Send(Send(Class("SCStreamConfiguration"u8), Sel("alloc"u8)), Sel("init"u8));
                SendBool(config, Sel("setScalesToFit:"u8), 1);
                SendBool(config, Sel("setShowsCursor:"u8), 0);
                var (width, height) = DesktopMedia.MacScreenshotSize(boxWidth, boxHeight, frame.Width, frame.Height);
                SendNUInt(config, Sel("setWidth:"u8), (nuint)width);
                SendNUInt(config, Sel("setHeight:"u8), (nuint)height);
                var captured = id;
                var block = NewBlock((image, error) =>
                {
                    if (image != 0 && error == 0 && ReadImage(image) is { } bitmap) results[captured] = bitmap;
                    Answered();
                });
                ((delegate* unmanaged<nint, nint, nint, nint, ContextBlock*, void>)s_msgSend)(
                    Class("SCScreenshotManager"u8), Sel("captureImageWithFilter:configuration:completionHandler:"u8), filter, config, block);
                Send(filter, Sel("release"u8));
                Send(config, Sel("release"u8));
            }
        });
        return done.Task;
    }

    /// <summary>webrtc's CoreGraphics capture, before macOS 14.4: <c>CGWindowListCreateImage</c> of the window, or
    /// <c>CGDisplayCreateImage</c> of the display. Null when macOS refuses.</summary>
    public static CaptureBitmap? CaptureCoreGraphics(bool screen, uint id)
    {
        var image = screen
            ? CGDisplayCreateImage(id)
            : CGWindowListCreateImage(new CGRect { X = double.PositiveInfinity, Y = double.PositiveInfinity }, IncludingWindow, id, ImageBoundsIgnoreFraming);
        if (image == 0) return null;
        try { return ReadImage(image); }
        finally { CFRelease(image); }
    }

    /// <summary>Chromium's <c>GetWindowIcon</c> on macOS: the icon of the app that owns the window, through
    /// <c>NSRunningApplication</c>, at the icon's size in points. Null when it has none.</summary>
    public static CaptureBitmap? WindowIcon(uint windowId)
    {
        if (WindowList(IncludingWindow, windowId).FirstOrDefault(w => w.Id == windowId) is not { } window) return null;
        NativeLibrary.TryLoad(AppKit, out _);
        var pool = objc_autoreleasePoolPush();
        try
        {
            var app = SendInt(Class("NSRunningApplication"u8), Sel("runningApplicationWithProcessIdentifier:"u8), window.Pid);
            var icon = app == 0 ? 0 : Send(app, Sel("icon"u8));
            if (icon == 0) return null;
            var size = SendSize(icon, Sel("size"u8));
            var cgImage = ((delegate* unmanaged<nint, nint, CGRect*, nint, nint, nint>)s_msgSend)(icon, Sel("CGImageForProposedRect:context:hints:"u8), null, 0, 0);
            return cgImage == 0 ? null : ReadImage(cgImage, (int)Math.Round(size.Width), (int)Math.Round(size.Height));
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static void GetShareableContent(Action<nint> handler)
    {
        if (!s_screenCaptureKit.Value)
        {
            handler(0);
            return;
        }
        var block = NewBlock((content, error) =>
        {
            var pool = objc_autoreleasePoolPush();
            try { handler(error == 0 ? content : 0); }
            finally { objc_autoreleasePoolPop(pool); }
        });
        ((delegate* unmanaged<nint, nint, byte, byte, ContextBlock*, void>)s_msgSend)(
            Class("SCShareableContent"u8), Sel("getShareableContentExcludingDesktopWindows:onScreenWindowsOnly:completionHandler:"u8), 1, 1, block);
    }

    private static MacShareableContent ReadContent(nint content)
    {
        var displays = new List<MacDisplayEntry>();
        var displayArray = Send(content, Sel("displays"u8));
        for (nint i = 0, n = Send(displayArray, Sel("count"u8)); i < n; i++)
        {
            var display = Send(displayArray, Sel("objectAtIndex:"u8), i);
            var frame = Rect(display);
            displays.Add(new MacDisplayEntry(SendUInt(display, Sel("displayID"u8)), frame.X, frame.Y, frame.Width, frame.Height));
        }
        var windows = new List<MacWindowEntry>();
        var windowArray = Send(content, Sel("windows"u8));
        for (nint i = 0, n = Send(windowArray, Sel("count"u8)); i < n; i++)
        {
            var window = Send(windowArray, Sel("objectAtIndex:"u8), i);
            var frame = Rect(window);
            var app = Send(window, Sel("owningApplication"u8));
            var title = Send(window, Sel("title"u8));
            windows.Add(new MacWindowEntry(SendUInt(window, Sel("windowID"u8)), title == 0 ? null : NSString(title),
                app == 0 ? "" : NSString(Send(app, Sel("applicationName"u8))), app == 0 ? 0 : (int)SendUInt(app, Sel("processID"u8)),
                (int)Send(window, Sel("windowLayer"u8)), frame.X, frame.Y, frame.Width, frame.Height,
                ((delegate* unmanaged<nint, nint, byte>)s_msgSend)(window, Sel("isOnScreen"u8)) != 0));
        }
        return new MacShareableContent(windows, displays);
    }

    private static nint Find(nint array, bool screens, uint id)
    {
        for (nint i = 0, n = Send(array, Sel("count"u8)); i < n; i++)
        {
            var item = Send(array, Sel("objectAtIndex:"u8), i);
            if (SendUInt(item, Sel(screens ? "displayID"u8 : "windowID"u8)) == id) return item;
        }
        return 0;
    }

    private static CaptureBitmap? ReadImage(nint image) => ReadImage(image, (int)CGImageGetWidth(image), (int)CGImageGetHeight(image));

    // Drawn into a BGRA context rather than read raw: the image's own layout varies with the source.
    private static CaptureBitmap? ReadImage(nint image, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var pixels = new byte[width * height * 4];
        var space = CGColorSpaceCreateDeviceRGB();
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
        return new CaptureBitmap(width, height, pixels);
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
    private struct ContextBlock
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public delegate* unmanaged<ContextBlock*, nint, nint, void> Invoke;
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
    private static ContextBlock* NewBlock(Action<nint, nint> callback)
    {
        var block = (ContextBlock*)NativeMemory.AllocZeroed((nuint)(sizeof(ContextBlock) + sizeof(BlockDescriptor)));
        var descriptor = (BlockDescriptor*)(block + 1);
        descriptor->Size = (nuint)sizeof(ContextBlock);
        block->Isa = NativeLibrary.GetExport(s_libSystem, "_NSConcreteGlobalBlock");
        // BLOCK_IS_GLOBAL.
        block->Flags = 1 << 28;
        block->Invoke = &OnBlock;
        block->Descriptor = descriptor;
        block->Context = GCHandle.ToIntPtr(GCHandle.Alloc(callback));
        return block;
    }

    [UnmanagedCallersOnly]
    private static void OnBlock(ContextBlock* block, nint first, nint second)
    {
        var handle = GCHandle.FromIntPtr(block->Context);
        var callback = (Action<nint, nint>)handle.Target!;
        handle.Free();
        try { callback(first, second); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a desktop capture callback: {e}"); }
        // Never freed: a copy of a global block is the block itself, so whoever holds it may read its flags at any time.
    }

    private static CGRect Rect(nint obj)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return ((delegate* unmanaged<nint, nint, CGRect>)s_msgSend)(obj, Sel("frame"u8));
        CGRect rect;
        SendRectStret(&rect, obj, Sel("frame"u8));
        return rect;
    }

    private static CGSize SendSize(nint obj, nint selector) => ((delegate* unmanaged<nint, nint, CGSize>)s_msgSend)(obj, selector);

    private static long? Int(nint number)
    {
        if (number == 0) return null;
        long value;
        // kCFNumberSInt64Type.
        return CFNumberGetValue(number, 4, &value) != 0 ? value : null;
    }

    private static string CFString(nint s)
    {
        if (s == 0) return "";
        var length = CFStringGetLength(s);
        var chars = new char[length];
        fixed (char* p = chars) CFStringGetCharacters(s, new CFRange { Location = 0, Length = length }, p);
        return new string(chars);
    }

    private static string NSString(nint s) => s == 0 ? "" : Marshal.PtrToStringUTF8(Send(s, Sel("UTF8String"u8))) ?? "";

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRange
    {
        public nint Location, Length;
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

    private static nint Send(nint receiver, nint selector, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_msgSend)(receiver, selector, a, b);

    private static nint SendInt(nint receiver, nint selector, int arg) => ((delegate* unmanaged<nint, nint, int, nint>)s_msgSend)(receiver, selector, arg);

    private static uint SendUInt(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, uint>)s_msgSend)(receiver, selector);

    private static void SendBool(nint receiver, nint selector, byte value) => ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(receiver, selector, value);

    private static void SendNUInt(nint receiver, nint selector, nuint value) => ((delegate* unmanaged<nint, nint, nuint, void>)s_msgSend)(receiver, selector, value);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void SendRectStret(CGRect* result, nint receiver, nint selector);
    [DllImport(CoreGraphics)] private static extern byte CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphics)] private static extern byte CGRequestScreenCaptureAccess();
    [DllImport(CoreGraphics)] private static extern int CGGetActiveDisplayList(uint max, uint* displays, uint* count);
    [DllImport(CoreGraphics)] private static extern nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(CoreGraphics)] private static extern byte CGRectMakeWithDictionaryRepresentation(nint dict, CGRect* rect);
    [DllImport(CoreGraphics)] private static extern nint CGWindowListCreateImage(CGRect screenBounds, uint option, uint windowId, uint imageOption);
    [DllImport(CoreGraphics)] private static extern nint CGDisplayCreateImage(uint display);
    [DllImport(CoreGraphics)] private static extern nuint CGImageGetWidth(nint image);
    [DllImport(CoreGraphics)] private static extern nuint CGImageGetHeight(nint image);
    [DllImport(CoreGraphics)] private static extern nint CGColorSpaceCreateDeviceRGB();
    [DllImport(CoreGraphics)] private static extern nint CGBitmapContextCreate(void* data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);
    [DllImport(CoreGraphics)] private static extern void CGContextDrawImage(nint context, CGRect rect, nint image);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(nint array);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryGetValue(nint dict, nint key);
    [DllImport(CoreFoundation)] private static extern byte CFNumberGetValue(nint number, nint type, void* value);
    [DllImport(CoreFoundation)] private static extern byte CFBooleanGetValue(nint boolean);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(nint s);
    [DllImport(CoreFoundation)] private static extern void CFStringGetCharacters(nint s, CFRange range, char* buffer);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint cf);
}
