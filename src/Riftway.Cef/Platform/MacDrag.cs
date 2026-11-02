using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>The rules of Electron v44's <c>drag_util_mac.mm</c>.</summary>
public static class MacDragRules
{
    /// <summary><c>NSDragOperationCopy</c>.</summary>
    public const nuint Copy = 1;

    /// <summary><c>NSDragOperationEvery</c>.</summary>
    public static readonly nuint Every = nuint.MaxValue;

    /// <summary><c>DragDownloadItemSource</c>: copy out of the app (<c>NSDraggingContextOutsideApplication</c>, 0), anything
    /// inside it.</summary>
    public static nuint SourceOperationMask(long context) => context == 0 ? Copy : Every;

    /// <summary>Each item's dragging frame: the icon's size, centred on the pointer.</summary>
    public static (double X, double Y, double Width, double Height) ItemFrame(double pointerX, double pointerY, double width, double height)
        => (pointerX - width / 2, pointerY - height / 2, width, height);
}

/// <summary>Electron v44's macOS <c>DragFileItems</c>: an <c>NSDraggingSession</c> of file URLs from the page's view,
/// started from a synthesized drag event. AppKit runs it on its own; nothing waits. Main thread.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacDrag
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    // NSAlignAllEdgesOutward; NSEventTypeLeftMouseDragged.
    private const ulong AllEdgesOutward = 0xF00;
    private const nuint LeftMouseDragged = 6;
    private static readonly nint s_send = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_sendStret = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? 0 : NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend_stret");
    private static nint s_source;

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint
    {
        public double X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize
    {
        public double Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }

    /// <summary>Drags <paramref name="files"/> out of <paramref name="view"/> under the pointer, each shown as
    /// <paramref name="image"/> (an NSImage); does nothing without a window or when AppKit refuses the event.</summary>
    public static void DragFiles(nint view, IReadOnlyList<string> files, nint image)
    {
        var window = view == 0 ? 0 : Send(view, Sel("window"u8));
        if (window == 0) return;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pointer = ((delegate* unmanaged<nint, nint, NSPoint>)s_send)(window, Sel("mouseLocationOutsideOfEventStream"u8));
            var aligned = BackingAligned(view, new NSRect { X = pointer.X, Y = pointer.Y });
            var position = new NSPoint { X = aligned.X, Y = aligned.Y };
            var size = ((delegate* unmanaged<nint, nint, NSSize>)s_send)(image, Sel("size"u8));
            var items = Send(Class("NSMutableArray"u8), Sel("array"u8));
            foreach (var file in files)
            {
                var url = ((delegate* unmanaged<nint, nint, nint, nint>)s_send)(Class("NSURL"u8), Sel("fileURLWithPath:"u8), NSString(file));
                var item = ((delegate* unmanaged<nint, nint, nint, nint>)s_send)(Send(Class("NSDraggingItem"u8), Sel("alloc"u8)), Sel("initWithPasteboardWriter:"u8), url);
                var (x, y, width, height) = MacDragRules.ItemFrame(position.X, position.Y, size.Width, size.Height);
                ((delegate* unmanaged<nint, nint, NSRect, nint, void>)s_send)(item, Sel("setDraggingFrame:contents:"u8), new NSRect { X = x, Y = y, Width = width, Height = height }, image);
                ((delegate* unmanaged<nint, nint, nint, void>)s_send)(items, Sel("addObject:"u8), item);
                Send(item, Sel("release"u8));
            }
            // Electron synthesizes the drag event: the one that started the drag may have gone to the page.
            var current = Send(Send(Class("NSApplication"u8), Sel("sharedApplication"u8)), Sel("currentEvent"u8));
            var timestamp = current == 0 ? 0 : ((delegate* unmanaged<nint, nint, double>)s_send)(current, Sel("timestamp"u8));
            var drag = ((delegate* unmanaged<nint, nint, nuint, NSPoint, nuint, double, nint, nint, nint, nint, float, nint>)s_send)(
                Class("NSEvent"u8), Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"u8),
                LeftMouseDragged, position, 0, timestamp, Send(window, Sel("windowNumber"u8)), 0, 0, 1, 1.0f);
            if (drag == 0) return;
            ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)s_send)(view, Sel("beginDraggingSessionWithItems:event:source:"u8), items, drag, Source());
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static NSRect BackingAligned(nint view, NSRect rect)
    {
        var selector = Sel("backingAlignedRect:options:"u8);
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return ((delegate* unmanaged<nint, nint, NSRect, ulong, NSRect>)s_send)(view, selector, rect, AllEdgesOutward);
        NSRect result;
        ((delegate* unmanaged<NSRect*, nint, nint, NSRect, ulong, void>)s_sendStret)(&result, view, selector, rect, AllEdgesOutward);
        return result;
    }

    // DragDownloadItemSource, one for the process as Electron's static.
    private static nint Source()
    {
        if (s_source != 0) return s_source;
        fixed (byte* name = "RiftwayDragSource"u8)
        {
            var cls = objc_getClass(name);
            if (cls == 0)
            {
                cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
                fixed (byte* types = "Q@:@q"u8)
                    class_addMethod(cls, Sel("draggingSession:sourceOperationMaskForDraggingContext:"u8),
                        (delegate* unmanaged<nint, nint, nint, nint, nuint>)&SourceOperationMask, types);
                fixed (byte* protocolName = "NSDraggingSource"u8)
                    if (objc_getProtocol(protocolName) is var protocol and not 0) class_addProtocol(cls, protocol);
                objc_registerClassPair(cls);
            }
            return s_source = Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
        }
    }

    [UnmanagedCallersOnly]
    private static nuint SourceOperationMask(nint self, nint cmd, nint session, nint context) => MacDragRules.SourceOperationMask(context);

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
    [DllImport(ObjC)] private static extern nint objc_getProtocol(byte* name);
    [DllImport(ObjC)] private static extern byte class_addProtocol(nint cls, nint protocol);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
}
