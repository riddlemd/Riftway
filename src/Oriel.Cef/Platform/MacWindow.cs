using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>macOS "simple" full screen: the window fills its display without moving to a new Space.</summary>
/// <remarks>Native full screen gives the window its own Space, which blacks out the other displays;
/// this is Electron's <c>setSimpleFullScreen</c> instead (pre-Lion style).</remarks>
[SupportedOSPlatform("macos")]
public static unsafe class MacWindow
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    private const nuint TitledStyle = 1 << 0;
    private const nuint FullScreenStyle = 1 << 14;
    // NSApplicationPresentationHideDock | NSApplicationPresentationHideMenuBar.
    private const nuint HideDockAndMenuBar = (1 << 1) | (1 << 3);

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    private sealed record Saved(NSRect Frame, nuint StyleMask, nuint PresentationOptions);

    private static readonly Dictionary<nint, Saved> s_saved = [];

    /// <summary>Whether the window behind <paramref name="contentView"/> is in simple full screen.</summary>
    public static bool IsSimpleFullScreen(nint contentView) => s_saved.ContainsKey(NSWindowOf(contentView));

    /// <summary>Enters or leaves simple full screen for the window behind <paramref name="contentView"/>
    /// (CEF's <c>get_window_handle</c> on macOS). Call on the main thread.</summary>
    public static void SetSimpleFullScreen(nint contentView, bool flag)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        var app = Send(Class("NSApplication"u8), Sel("sharedApplication"u8));

        if (flag && !s_saved.ContainsKey(window))
        {
            var saved = new Saved(Frame(window), (nuint)Send(window, Sel("styleMask"u8)), (nuint)Send(app, Sel("presentationOptions"u8)));
            s_saved[window] = saved;

            SendNUInt(window, Sel("setStyleMask:"u8), saved.StyleMask & ~TitledStyle);
            SendNUInt(app, Sel("setPresentationOptions:"u8), HideDockAndMenuBar);
            SetFrame(window, Frame(Send(window, Sel("screen"u8))));
        }
        else if (!flag && s_saved.Remove(window, out var saved))
        {
            SendNUInt(app, Sel("setPresentationOptions:"u8), saved.PresentationOptions);
            SendNUInt(window, Sel("setStyleMask:"u8), saved.StyleMask);
            SetFrame(window, saved.Frame);
        }
    }

    /// <summary>Whether the window behind <paramref name="contentView"/> is in native (own-Space) full screen.</summary>
    public static bool IsNativeFullScreen(nint contentView)
    {
        var window = NSWindowOf(contentView);
        return window != 0 && ((nuint)Send(window, Sel("styleMask"u8)) & FullScreenStyle) != 0;
    }

    /// <summary>Sends the window <c>toggleFullScreen:</c>, as the title bar's green button does.</summary>
    public static void ToggleFullScreen(nint contentView)
    {
        var window = NSWindowOf(contentView);
        if (window != 0) SendObject(window, Sel("toggleFullScreen:"u8), 0);
    }

    private static readonly Dictionary<nint, Action> s_toggleHandlers = [];

    /// <summary>Makes the green button, its hover menu and Window ▸ Enter Full Screen run <paramref name="toggled"/>
    /// instead of native full screen. Returns the NSWindow, for <see cref="UnrouteFullScreenToggle"/>.</summary>
    /// <remarks>They all send <c>toggleFullScreen:</c>; CEF owns the window's class, so the window gets a
    /// runtime subclass overriding it, as Electron's own NSWindow subclass does.</remarks>
    public static nint RouteFullScreenToggle(nint contentView, Action toggled)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return 0;
        var cls = object_getClass(window);
        var name = System.Text.Encoding.UTF8.GetBytes("Oriel_" + Marshal.PtrToStringUTF8((nint)class_getName(cls)) + "\0");
        fixed (byte* n = name)
        {
            var subclass = objc_getClass(n);
            if (subclass == 0)
            {
                subclass = objc_allocateClassPair(cls, n, 0);
                fixed (byte* types = "v@:@"u8)
                    class_addMethod(subclass, Sel("toggleFullScreen:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnToggleFullScreen, types);
                objc_registerClassPair(subclass);
            }
            if (cls != subclass) object_setClass(window, subclass);
        }
        s_toggleHandlers[window] = toggled;
        return window;
    }

    /// <summary>Forgets a <see cref="RouteFullScreenToggle"/> handler once its window is gone.</summary>
    public static void UnrouteFullScreenToggle(nint window) => s_toggleHandlers.Remove(window);

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjcSuper
    {
        public nint Receiver;
        public nint SuperClass;
    }

    [UnmanagedCallersOnly]
    private static void OnToggleFullScreen(nint self, nint cmd, nint sender)
    {
        if (s_toggleHandlers.TryGetValue(self, out var toggled))
        {
            try { toggled(); }
            catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a full-screen toggle: {e}"); }
            return;
        }
        var super = new ObjcSuper { Receiver = self, SuperClass = class_getSuperclass(object_getClass(self)) };
        SendSuperObject(&super, cmd, sender);
    }

    /// <summary>Ends every sheet attached to the window behind <paramref name="contentView"/>, as Electron's
    /// <c>NativeWindowMac::Close</c> does before closing. Main thread only.</summary>
    public static void EndAttachedSheets(nint contentView)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        for (var sheet = Send(window, Sel("attachedSheet"u8)); sheet != 0; sheet = Send(window, Sel("attachedSheet"u8)))
            SendObject(window, Sel("endSheet:"u8), sheet);
    }

    private static nint NSWindowOf(nint contentView) => contentView == 0 ? 0 : Send(contentView, Sel("window"u8));

    // NSRect is four doubles: arm64 returns it in registers, x64 through a hidden pointer (_stret).
    private static NSRect Frame(nint obj)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return SendRect(obj, Sel("frame"u8));
        NSRect rect;
        SendRectStret(&rect, obj, Sel("frame"u8));
        return rect;
    }

    private static void SetFrame(nint window, NSRect frame) => SendSetFrame(window, Sel("setFrame:display:"u8), frame, 1);

    /// <summary>The window's level (NSWindowLevel).</summary>
    public static long GetLevel(nint contentView) => NSWindowOf(contentView) is var window && window != 0 ? (long)Send(window, Sel("level"u8)) : 0;

    /// <summary>Sets the window's level (NSWindowLevel).</summary>
    public static void SetLevel(nint contentView, long level)
    {
        var window = NSWindowOf(contentView);
        if (window != 0) SendObject(window, Sel("setLevel:"u8), (nint)level);
    }

    /// <summary>The NSWindow behind <paramref name="contentView"/>, or 0.</summary>
    public static nint WindowOf(nint contentView) => NSWindowOf(contentView);

    /// <summary>The NSWindow's <c>parentWindow</c>, or 0.</summary>
    public static nint ParentWindow(nint contentView) => NSWindowOf(contentView) is var window && window != 0 ? Send(window, Sel("parentWindow"u8)) : 0;

    /// <summary>The NSWindow's <c>childWindows</c>.</summary>
    public static IReadOnlyList<nint> ChildWindows(nint contentView)
    {
        var window = NSWindowOf(contentView);
        var array = window == 0 ? 0 : Send(window, Sel("childWindows"u8));
        if (array == 0) return [];
        var count = (int)Send(array, Sel("count"u8));
        var children = new nint[count];
        for (var i = 0; i < count; i++) children[i] = SendNUIntObject(array, Sel("objectAtIndex:"u8), (nuint)i);
        return children;
    }

    /// <summary><c>[parent addChildWindow:child ordered:NSWindowAbove]</c>; AppKit orders the child in.</summary>
    public static void AddChildWindow(nint parentContentView, nint childContentView)
    {
        var (parent, child) = (NSWindowOf(parentContentView), NSWindowOf(childContentView));
        // NSWindowAbove
        if (parent != 0 && child != 0) SendObjectNInt(parent, Sel("addChildWindow:ordered:"u8), child, 1);
    }

    /// <summary><c>[parent removeChildWindow:child]</c>.</summary>
    public static void RemoveChildWindow(nint parentContentView, nint childContentView)
    {
        var (parent, child) = (NSWindowOf(parentContentView), NSWindowOf(childContentView));
        if (parent != 0 && child != 0) SendObject(parent, Sel("removeChildWindow:"u8), child);
    }

    public static void OrderOut(nint contentView)
    {
        var window = NSWindowOf(contentView);
        if (window != 0) SendObject(window, Sel("orderOut:"u8), 0);
    }

    /// <summary>Electron's <c>NativeWindowMac::IsVisible</c>: ordered in, not occluded, and not minimized.</summary>
    public static bool IsVisible(nint contentView)
    {
        var window = NSWindowOf(contentView);
        // NSWindowOcclusionStateVisible
        return window != 0 && SendGetBool(window, Sel("isVisible"u8)) != 0 && Send(window, Sel("occlusionState"u8)) == 2
            && SendGetBool(window, Sel("isMiniaturized"u8)) == 0;
    }

    /// <summary>Moves and resizes the window with AppKit's animation; the rectangle is in screen points with the origin at
    /// the top left of the main display, as Views uses.</summary>
    public static void AnimateFrame(nint contentView, int x, int y, int width, int height)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        // AppKit's origin is the bottom left of the first screen.
        var screens = Send(Class("NSScreen"u8), Sel("screens"u8));
        var primary = Frame(Send(screens, Sel("firstObject"u8)));
        var frame = new NSRect { X = x, Y = primary.Height - (y + height), Width = width, Height = height };
        SendAnimateFrame(window, Sel("setFrame:display:animate:"u8), frame, 1, 1);
    }

    /// <summary>The file the title bar's proxy icon stands for. Electron's <c>setRepresentedFilename</c>.</summary>
    public static void SetRepresentedFilename(nint contentView, string filename)
    {
        var window = NSWindowOf(contentView);
        if (window != 0) SendObject(window, Sel("setRepresentedFilename:"u8), NSString(filename));
    }

    /// <summary>Electron's <c>getRepresentedFilename</c>.</summary>
    public static string GetRepresentedFilename(nint contentView)
        => NSWindowOf(contentView) is var window && window != 0 ? ReadNSString(Send(window, Sel("representedFilename"u8))) : "";

    /// <summary>The close button's unsaved-changes dot. Electron's <c>setDocumentEdited</c>.</summary>
    /// <summary>[NSWindow setCanHide:], which Electron clears before hiding the Dock icon.</summary>
    public static void SetCanHide(nint contentView, bool canHide)
    {
        var window = NSWindowOf(contentView);
        if (window != 0) SendBool(window, Sel("setCanHide:"u8), canHide ? (byte)1 : (byte)0);
    }

    /// <summary>[NSWindow canHide].</summary>
    public static bool CanHide(nint contentView)
    {
        var window = NSWindowOf(contentView);
        return window != 0 && (byte)Send(window, Sel("canHide"u8)) != 0;
    }

    public static void SetDocumentEdited(nint contentView, bool edited)
    {
        var window = NSWindowOf(contentView);
        if (window != 0) SendBool(window, Sel("setDocumentEdited:"u8), edited ? (byte)1 : (byte)0);
    }

    /// <summary>Electron's <c>isDocumentEdited</c>.</summary>
    public static bool IsDocumentEdited(nint contentView)
        => NSWindowOf(contentView) is var window && window != 0 && SendGetBool(window, Sel("isDocumentEdited"u8)) != 0;

    /// <summary>Shows or hides the close, minimise and zoom buttons. Electron's <c>setWindowButtonVisibility</c>.</summary>
    public static void SetWindowButtonsHidden(nint contentView, bool hidden)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        // NSWindowCloseButton, NSWindowMiniaturizeButton, NSWindowZoomButton.
        for (nuint kind = 0; kind <= 2; kind++)
        {
            var button = SendNUIntObject(window, Sel("standardWindowButton:"u8), kind);
            if (button != 0) SendBool(button, Sel("setHidden:"u8), hidden ? (byte)1 : (byte)0);
        }
    }

    /// <summary>Makes the window see-through where nothing is painted, as Electron's NativeWindowMac does for
    /// <c>transparent</c>: a clear background, which also drops the shadow, no opacity, and a hidden title bar.</summary>
    public static void MakeTransparent(nint contentView, bool hideButtons)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        SendObject(window, Sel("setBackgroundColor:"u8), Send(Class("NSColor"u8), Sel("clearColor"u8)));
        SendBool(window, Sel("setOpaque:"u8), 0);
        SendBool(window, Sel("setTitlebarAppearsTransparent:"u8), 1);
        // NSWindowTitleHidden.
        SendNUInt(window, Sel("setTitleVisibility:"u8), 1);
        if (hideButtons) SetWindowButtonsHidden(contentView, true);
    }

    /// <summary>[NSWindow isOpaque].</summary>
    public static bool IsOpaque(nint contentView)
        => NSWindowOf(contentView) is var window && window != 0 && SendGetBool(window, Sel("isOpaque"u8)) != 0;

    /// <summary>Whether a collection behaviour flag (NSWindowCollectionBehavior) is set.</summary>
    public static bool HasCollectionBehavior(nint contentView, nuint flag)
        => NSWindowOf(contentView) is var window && window != 0 && ((nuint)Send(window, Sel("collectionBehavior"u8)) & flag) != 0;

    /// <summary>Sets or clears a collection behaviour flag, as Electron's <c>SetCollectionBehavior</c>.</summary>
    public static void SetCollectionBehavior(nint contentView, nuint flag, bool on)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        var behavior = (nuint)Send(window, Sel("collectionBehavior"u8));
        SendNUInt(window, Sel("setCollectionBehavior:"u8), on ? behavior | flag : behavior & ~flag);
    }

    /// <summary>NSWindowCollectionBehaviorCanJoinAllSpaces.</summary>
    public const nuint CanJoinAllSpaces = 1 << 0;

    /// <summary>NSWindowCollectionBehaviorTransient: left out of Mission Control.</summary>
    public const nuint Transient = 1 << 3;

    /// <summary>NSWindowCollectionBehaviorFullScreenAuxiliary.</summary>
    public const nuint FullScreenAuxiliary = 1 << 8;

    /// <summary>Hides (accessory) or shows (regular) the app's Dock icon, as Electron's <c>app.dock.hide/show</c>.</summary>
    public static void SetDockIconHidden(bool hidden)
        => SendNUInt(Send(Class("NSApplication"u8), Sel("sharedApplication"u8)), Sel("setActivationPolicy:"u8), hidden ? 1u : 0u);

    private static nint NSString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + " ");
        fixed (byte* p = bytes) return SendBytesObject(Class("NSString"u8), Sel("stringWithUTF8String:"u8), p);
    }

    private static string ReadNSString(nint value)
        => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"u8))) ?? "";

    // u8 literals are null-terminated in memory, as the runtime expects.
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
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendNUInt(nint receiver, nint selector, nuint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRect(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void SendRectStret(NSRect* result, nint receiver, nint selector);
    [DllImport(ObjC)] private static extern nint object_getClass(nint obj);
    [DllImport(ObjC)] private static extern nint object_setClass(nint obj, nint cls);
    [DllImport(ObjC)] private static extern nint class_getSuperclass(nint cls);
    [DllImport(ObjC)] private static extern byte* class_getName(nint cls);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendObject(nint receiver, nint selector, nint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBool(nint receiver, nint selector, byte value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern byte SendGetBool(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendObjectNInt(nint receiver, nint selector, nint arg, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendNUIntObject(nint receiver, nint selector, nuint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendBytesObject(nint receiver, nint selector, byte* value);
    [DllImport(ObjC, EntryPoint = "objc_msgSendSuper")] private static extern void SendSuperObject(ObjcSuper* super, nint selector, nint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSetFrame(nint receiver, nint selector, NSRect frame, byte display);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendAnimateFrame(nint receiver, nint selector, NSRect frame, byte display, byte animate);
}
