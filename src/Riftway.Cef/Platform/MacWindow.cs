using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>A window's geometry for <c>windowWillResize:toSize:</c>, in AppKit's coordinates.</summary>
public readonly record struct MacResizeGeometry(double X, double Y, double Width, double Height, double ContentWidth, double ContentHeight,
    double MinWidth, double MinHeight, double MaxWidth, double MaxHeight);

/// <summary>The window delegate notifications <see cref="MacWindow.ObserveEvents"/> reports.</summary>
public enum MacWindowEvent
{
    WillMove,
    DidMove,
    DidEndLiveResize,
    WillMiniaturize,
    DidMiniaturize,
    DidDeminiaturize,
    DidResize,
    DidBecomeMain,
    DidResignMain,
    DidBecomeKey,
    DidResignKey,
    WillEnterFullScreen,
    DidEnterFullScreen,
    WillExitFullScreen,
    DidExitFullScreen,
    WillBeginSheet,
    DidEndSheet,
}

/// <summary>macOS "simple" full screen: the window fills its display without moving to a new Space.</summary>
/// <remarks>Native full screen gives the window its own Space, which blacks out the other displays;
/// this is Electron's <c>setSimpleFullScreen</c> instead (pre-Lion style).</remarks>
[SupportedOSPlatform("macos")]
public static unsafe class MacWindow
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    // CGWindowLevelKey values.
    private const int MinimumWindowLevelKey = 1;
    private const int MaximumWindowLevelKey = 14;

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
        UseRiftwaySubclass(window);
        s_toggleHandlers[window] = toggled;
        return window;
    }

    private static readonly Dictionary<nint, Func<nint>> s_touchBarMakers = [];

    /// <summary>Electron's <c>ElectronNSWindow makeTouchBar</c>: AppKit's request for the window's Touch Bar goes to
    /// <paramref name="make"/> (an autoreleased NSTouchBar, or 0 for none) until <see cref="UnrouteMakeTouchBar"/>.
    /// Returns the NSWindow, or 0. Main thread only.</summary>
    public static nint RouteMakeTouchBar(nint contentView, Func<nint> make)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return 0;
        UseRiftwaySubclass(window);
        s_touchBarMakers[window] = make;
        return window;
    }

    /// <summary>Forgets a <see cref="RouteMakeTouchBar"/> once its window is gone.</summary>
    public static void UnrouteMakeTouchBar(nint window) => s_touchBarMakers.Remove(window);

    /// <summary><c>[window setTouchBar:nil]</c>, so AppKit asks <c>makeTouchBar</c> again.</summary>
    public static void ResetTouchBar(nint window) => SendObject(window, Sel("setTouchBar:"u8), 0);

    /// <summary><c>[window touchBar]</c>, which AppKit makes on first use.</summary>
    public static nint TouchBar(nint window) => Send(window, Sel("touchBar"u8));

    // Windows allowed past the screen, with whether each has a frame.
    private static readonly Dictionary<nint, bool> s_largerThanScreen = [];

    /// <summary>Electron's <c>enableLargerThanScreen</c>: AppKit no longer shrinks the window to its screen. Returns the
    /// NSWindow, for <see cref="ForgetLargerThanScreen"/>.</summary>
    public static nint EnableLargerThanScreen(nint contentView, bool hasFrame)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return 0;
        UseRiftwaySubclass(window);
        s_largerThanScreen[window] = hasFrame;
        return window;
    }

    /// <summary>Forgets an <see cref="EnableLargerThanScreen"/> once its window is gone.</summary>
    public static void ForgetLargerThanScreen(nint window) => s_largerThanScreen.Remove(window);

    [UnmanagedCallersOnly]
    private static NSRect OnConstrainFrameRect(nint self, nint cmd, NSRect frame, nint screen)
    {
        var super = new ObjcSuper { Receiver = self, SuperClass = RiftwaySuperclass(self) };
        NSRect result;
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            result = ((delegate* unmanaged<ObjcSuper*, nint, NSRect, nint, NSRect>)s_msgSendSuper)(&super, cmd, frame, screen);
        else ((delegate* unmanaged<NSRect*, ObjcSuper*, nint, NSRect, nint, void>)s_msgSendSuperStret)(&result, &super, cmd, frame, screen);
        if (!s_largerThanScreen.TryGetValue(self, out var hasFrame)) return result;
        var (x, y, width, height) = MacLargerThanScreen.Constrain((frame.X, frame.Y, frame.Width, frame.Height), (result.X, result.Y, result.Width, result.Height), hasFrame);
        return new NSRect { X = x, Y = y, Width = width, Height = height };
    }

    private static readonly Lazy<nint> s_quartz = new(() => NativeLibrary.Load("/System/Library/Frameworks/Quartz.framework/Quartz"));
    // Each window's QuickLook item, as Electron's preview_item_.
    private static readonly Dictionary<nint, nint> s_previewItems = [];

    /// <summary>Electron's <c>PreviewFile</c>: QuickLook's shared panel shows <paramref name="path"/> titled
    /// <paramref name="displayName"/>, with the window as its controller.</summary>
    public static void PreviewFile(nint contentView, string path, string displayName)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        _ = s_quartz.Value;
        UseRiftwaySubclass(window);
        ForgetPreviewItem(window);
        var item = Send(PreviewItemClass(), Sel("new"u8));
        var url = ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(Class("NSURL"u8), Sel("fileURLWithPath:"u8), NSString(path));
        s_previewItems[window] = item;
        s_previewItemUrls[item] = (Send(url, Sel("retain"u8)), Send(NSString(displayName), Sel("retain"u8)));
        SendObject(Send(Class("QLPreviewPanel"u8), Sel("sharedPreviewPanel"u8)), Sel("makeKeyAndOrderFront:"u8), 0);
    }

    /// <summary>Electron's <c>CloseFilePreview</c>: closes the panel this window opened; touching
    /// <c>sharedPreviewPanel</c> otherwise would make the panel again.</summary>
    public static void CloseFilePreview(nint contentView)
    {
        var window = NSWindowOf(contentView);
        if (window == 0 || !s_previewItems.ContainsKey(window)) return;
        _ = s_quartz.Value;
        if (SendGetBool(Class("QLPreviewPanel"u8), Sel("sharedPreviewPanelExists"u8)) == 0) return;
        Send(Send(Class("QLPreviewPanel"u8), Sel("sharedPreviewPanel"u8)), Sel("close"u8));
        ForgetPreviewItem(window);
    }

    /// <summary>Releases the window's QuickLook item once the window is gone.</summary>
    public static void ForgetPreviewItem(nint window)
    {
        if (!s_previewItems.Remove(window, out var item)) return;
        if (s_previewItemUrls.Remove(item, out var parts))
        {
            Send(parts.Url, Sel("release"u8));
            Send(parts.Title, Sel("release"u8));
        }
        Send(item, Sel("release"u8));
    }

    private static readonly Dictionary<nint, (nint Url, nint Title)> s_previewItemUrls = [];

    // ElectronPreviewItem: a QLPreviewItem with a URL and a title.
    private static nint PreviewItemClass()
    {
        fixed (byte* name = "RiftwayPreviewItem"u8)
        {
            var cls = objc_getClass(name);
            if (cls != 0) return cls;
            cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
            fixed (byte* types = "@@:"u8)
            {
                class_addMethod(cls, Sel("previewItemURL"u8), (delegate* unmanaged<nint, nint, nint>)&PreviewItemUrl, types);
                class_addMethod(cls, Sel("previewItemTitle"u8), (delegate* unmanaged<nint, nint, nint>)&PreviewItemTitle, types);
            }
            fixed (byte* protocolName = "QLPreviewItem"u8)
                if (objc_getProtocol(protocolName) is var protocol and not 0) class_addProtocol(cls, protocol);
            objc_registerClassPair(cls);
            return cls;
        }
    }

    [UnmanagedCallersOnly]
    private static nint PreviewItemUrl(nint self, nint cmd) => s_previewItemUrls.TryGetValue(self, out var parts) ? parts.Url : 0;

    [UnmanagedCallersOnly]
    private static nint PreviewItemTitle(nint self, nint cmd) => s_previewItemUrls.TryGetValue(self, out var parts) ? parts.Title : 0;

    [UnmanagedCallersOnly]
    private static byte AcceptsPreviewPanelControl(nint self, nint cmd, nint panel) => 1;

    // Electron makes the window's delegate the data source; here the window answers for its own item.
    [UnmanagedCallersOnly]
    private static void BeginPreviewPanelControl(nint self, nint cmd, nint panel) => SendObject(panel, Sel("setDataSource:"u8), self);

    [UnmanagedCallersOnly]
    private static void EndPreviewPanelControl(nint self, nint cmd, nint panel) => SendObject(panel, Sel("setDataSource:"u8), 0);

    [UnmanagedCallersOnly]
    private static nint NumberOfPreviewItems(nint self, nint cmd, nint panel) => 1;

    [UnmanagedCallersOnly]
    private static nint PreviewItemAtIndex(nint self, nint cmd, nint panel, nint index) => s_previewItems.GetValueOrDefault(self);

    // Riftway's overrides go onto the window's own class (CEF's CefNSWindow, which implements none of them), never onto a
    // subclass swapped in: AppKit removes its KVO registrations on dealloc by the class they were made under, and throws.
    private static void UseRiftwaySubclass(nint window)
    {
        var cls = object_getClass(window);
        if (IsRiftwaySubclassed(cls)) return;
        while (class_getSuperclass(cls) != 0 && (Marshal.PtrToStringUTF8((nint)class_getName(cls)) ?? "").StartsWith("NSKVONotifying_", StringComparison.Ordinal))
            cls = class_getSuperclass(cls);
        s_riftwayClasses.Add(cls);
        fixed (byte* types = "v@:@"u8)
            class_addMethod(cls, Sel("toggleFullScreen:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnToggleFullScreen, types);
        fixed (byte* types = "@@:"u8)
            class_addMethod(cls, Sel("makeTouchBar"u8), (delegate* unmanaged<nint, nint, nint>)&OnMakeTouchBar, types);
        fixed (byte* types = "{CGRect={CGPoint=dd}{CGSize=dd}}@:{CGRect={CGPoint=dd}{CGSize=dd}}@"u8)
            class_addMethod(cls, Sel("constrainFrameRect:toScreen:"u8), (delegate* unmanaged<nint, nint, NSRect, nint, NSRect>)&OnConstrainFrameRect, types);
        fixed (byte* types = "@@:"u8)
            class_addMethod(cls, Sel("accessibilityTitle"u8), (delegate* unmanaged<nint, nint, nint>)&OnAccessibilityTitle, types);
        fixed (byte* types = "v@:@"u8)
            class_addMethod(cls, Sel("rotateWithEvent:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnRotateWithEvent, types);
        fixed (byte* types = ObjCEncoding.Of("B@:"))
        {
            class_addMethod(cls, Sel("canBecomeKeyWindow"u8), (delegate* unmanaged<nint, nint, byte>)&CanBecomeKeyOrMain, types);
            class_addMethod(cls, Sel("canBecomeMainWindow"u8), (delegate* unmanaged<nint, nint, byte>)&CanBecomeKeyOrMain, types);
        }
        fixed (byte* types = ObjCEncoding.Of("B@:@"))
            class_addMethod(cls, Sel("acceptsPreviewPanelControl:"u8), (delegate* unmanaged<nint, nint, nint, byte>)&AcceptsPreviewPanelControl, types);
        fixed (byte* types = "v@:@"u8)
        {
            class_addMethod(cls, Sel("beginPreviewPanelControl:"u8), (delegate* unmanaged<nint, nint, nint, void>)&BeginPreviewPanelControl, types);
            class_addMethod(cls, Sel("endPreviewPanelControl:"u8), (delegate* unmanaged<nint, nint, nint, void>)&EndPreviewPanelControl, types);
        }
        fixed (byte* types = "q@:@"u8)
            class_addMethod(cls, Sel("numberOfPreviewItemsInPreviewPanel:"u8), (delegate* unmanaged<nint, nint, nint, nint>)&NumberOfPreviewItems, types);
        fixed (byte* types = "@@:@q"u8)
            class_addMethod(cls, Sel("previewPanel:previewItemAtIndex:"u8), (delegate* unmanaged<nint, nint, nint, nint, nint>)&PreviewItemAtIndex, types);
    }

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly Dictionary<nint, Action<bool>> s_occlusionHandlers = [];
    private static nint s_occlusionObserver;
    // NSWindowOcclusionStateVisible.
    private const nuint OcclusionStateVisible = 1 << 1;

    /// <summary>Electron's <c>windowDidChangeOcclusionState:</c>: calls <paramref name="changed"/> with whether the window
    /// behind <paramref name="contentView"/> is visible each time its occlusion state changes, until
    /// <see cref="StopObservingOcclusion"/>. Returns the NSWindow, or 0. Main thread only.</summary>
    public static nint ObserveOcclusion(nint contentView, Action<bool> changed)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return 0;
        if (s_occlusionObserver == 0)
        {
            fixed (byte* name = "RiftwayOcclusionObserver"u8)
            {
                var cls = objc_getClass(name);
                if (cls == 0)
                {
                    cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
                    fixed (byte* types = "v@:@"u8)
                        class_addMethod(cls, Sel("riftwayOcclusionChanged:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnOcclusionChanged, types);
                    objc_registerClassPair(cls);
                }
                s_occlusionObserver = Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
            }
        }
        s_occlusionHandlers[window] = changed;
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)s_msgSend)(NotificationCenter(), Sel("addObserver:selector:name:object:"u8),
            s_occlusionObserver, Sel("riftwayOcclusionChanged:"u8), OcclusionNotificationName(), window);
        return window;
    }

    /// <summary>Stops an <see cref="ObserveOcclusion"/> once its window is gone.</summary>
    public static void StopObservingOcclusion(nint window)
    {
        if (!s_occlusionHandlers.Remove(window)) return;
        ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)s_msgSend)(NotificationCenter(), Sel("removeObserver:name:object:"u8),
            s_occlusionObserver, OcclusionNotificationName(), window);
    }

    /// <summary>What <c>windowDidChangeOcclusionState:</c> reports: null for the synthetic notifications Chromium's
    /// <c>WebContentsOcclusionCheckerMac</c> posts, else whether the visible bit is set.</summary>
    public static bool? OcclusionVisible(nuint occlusionState, bool fromOcclusionChecker)
        => fromOcclusionChecker ? null : (occlusionState & OcclusionStateVisible) != 0;

    [UnmanagedCallersOnly]
    private static void OnOcclusionChanged(nint self, nint cmd, nint notification)
    {
        var window = Send(notification, Sel("object"u8));
        if (!s_occlusionHandlers.TryGetValue(window, out var changed)) return;
        var userInfo = Send(notification, Sel("userInfo"u8));
        var fromChecker = userInfo != 0 && ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(userInfo, Sel("objectForKey:"u8), NSString("WebContentsOcclusionCheckerMac")) != 0;
        if (OcclusionVisible((nuint)Send(window, Sel("occlusionState"u8)), fromChecker) is not { } visible) return;
        try { changed(visible); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception in an occlusion change: {e}"); }
    }

    private static nint NotificationCenter() => Send(Class("NSNotificationCenter"u8), Sel("defaultCenter"u8));

    private static nint OcclusionNotificationName()
        => *(nint*)NativeLibrary.GetExport(NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"), "NSWindowDidChangeOcclusionStateNotification");
    [UnmanagedCallersOnly]
    private static nint OnMakeTouchBar(nint self, nint cmd)
    {
        if (s_touchBarMakers.TryGetValue(self, out var make))
        {
            try { return make(); }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[Riftway] Unhandled exception making a Touch Bar: {e}");
                return 0;
            }
        }
        var super = new ObjcSuper { Receiver = self, SuperClass = RiftwaySuperclass(self) };
        return ((delegate* unmanaged<ObjcSuper*, nint, nint>)s_msgSendSuper)(&super, cmd);
    }

    private static readonly nint s_msgSendSuper = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSendSuper");
    // x64 only; arm64 has no _stret entry points.
    private static readonly nint s_msgSendSuperStret = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? 0 : NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSendSuper_stret");
    // Riftway's runtime subclasses; super is the class above the first of them, so a KVO subclass on top cannot loop.
    private static readonly HashSet<nint> s_riftwayClasses = [];

    private static bool IsRiftwaySubclassed(nint cls)
    {
        for (; cls != 0; cls = class_getSuperclass(cls)) if (s_riftwayClasses.Contains(cls)) return true;
        return false;
    }

    private static nint RiftwaySuperclass(nint self)
    {
        var cls = object_getClass(self);
        while (cls != 0 && !s_riftwayClasses.Contains(cls)) cls = class_getSuperclass(cls);
        return class_getSuperclass(cls != 0 ? cls : object_getClass(self));
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
            catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception in a full-screen toggle: {e}"); }
            return;
        }
        var super = new ObjcSuper { Receiver = self, SuperClass = RiftwaySuperclass(self) };
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

    public static nint NSWindowOf(nint contentView) => contentView == 0 ? 0 : Send(contentView, Sel("window"u8));

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

    /// <summary>The window server's level range: <c>CGWindowLevelForKey</c> of <c>kCGMinimumWindowLevelKey</c> and
    /// <c>kCGMaximumWindowLevelKey</c>.</summary>
    public static (long Min, long Max) LevelRange() => (CGWindowLevelForKey(MinimumWindowLevelKey), CGWindowLevelForKey(MaximumWindowLevelKey));

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

    /// <summary>Electron's <c>NativeWindowMac::IsVisible</c>, literally: ordered in, not minimized, and an occlusion state
    /// other than exactly <c>NSWindowOcclusionStateVisible</c>.</summary>
    public static bool IsVisible(nint contentView)
    {
        var window = NSWindowOf(contentView);
        // Electron calls the state == NSWindowOcclusionStateVisible "occluded"; AppKit reports 0x2002 for an on-screen
        // window, so in practice only isVisible and minimized decide.
        return window != 0 && SendGetBool(window, Sel("isVisible"u8)) != 0 && Send(window, Sel("occlusionState"u8)) != 2
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

    /// <summary>Enables or disables the title bar's zoom (green) button.</summary>
    public static void SetZoomButtonEnabled(nint contentView, bool enabled)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        // NSWindowZoomButton.
        var button = SendNUIntObject(window, Sel("standardWindowButton:"u8), 2);
        if (button != 0) SendBool(button, Sel("setEnabled:"u8), enabled ? (byte)1 : (byte)0);
    }

    private static readonly nint s_send = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    /// <summary><c>setAlphaValue:</c>, Electron's macOS <c>SetOpacity</c>.</summary>
    public static void SetAlphaValue(nint contentView, double alpha)
    {
        if (NSWindowOf(contentView) is var window and not 0) ((delegate* unmanaged<nint, nint, double, void>)s_send)(window, Sel("setAlphaValue:"u8), alpha);
    }

    /// <summary><c>alphaValue</c>; 1 without a window.</summary>
    public static double AlphaValue(nint contentView)
        => NSWindowOf(contentView) is var window and not 0 ? ((delegate* unmanaged<nint, nint, double>)s_send)(window, Sel("alphaValue"u8)) : 1;

    /// <summary><c>setHasShadow:</c>.</summary>
    public static void SetHasShadow(nint contentView, bool hasShadow)
    {
        if (NSWindowOf(contentView) is var window and not 0) SendBool(window, Sel("setHasShadow:"u8), hasShadow ? (byte)1 : (byte)0);
    }

    /// <summary><c>hasShadow</c>.</summary>
    public static bool HasShadow(nint contentView) => NSWindowOf(contentView) is var window and not 0 && SendGetBool(window, Sel("hasShadow"u8)) != 0;

    /// <summary><c>invalidateShadow</c>: recomputes the shadow from the window's current shape.</summary>
    public static void InvalidateShadow(nint contentView)
    {
        if (NSWindowOf(contentView) is var window and not 0) Send(window, Sel("invalidateShadow"u8));
    }

    /// <summary>Electron's macOS <c>SetIgnoreMouseEvents</c>: <c>ignoresMouseEvents</c>, and mouse moves still delivered
    /// (<c>acceptsMouseMovedEvents</c>) only when forwarding while ignoring.</summary>
    public static void SetIgnoresMouseEvents(nint contentView, bool ignore, bool forward)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        SendBool(window, Sel("setIgnoresMouseEvents:"u8), ignore ? (byte)1 : (byte)0);
        SendBool(window, Sel("setAcceptsMouseMovedEvents:"u8), ignore && forward ? (byte)1 : (byte)0);
    }

    /// <summary>Electron's macOS <c>SetContentProtection</c>: <c>NSWindowSharingNone</c> or <c>NSWindowSharingReadOnly</c>.</summary>
    public static void SetContentProtected(nint contentView, bool enable)
    {
        if (NSWindowOf(contentView) is var window and not 0) SendNUInt(window, Sel("setSharingType:"u8), enable ? 0u : 1u);
    }

    /// <summary>Whether the window's <c>sharingType</c> is <c>NSWindowSharingNone</c>.</summary>
    public static bool IsContentProtected(nint contentView) => NSWindowOf(contentView) is var window and not 0 && Send(window, Sel("sharingType"u8)) == 0;
    private static readonly Dictionary<nint, Action<MacWindowEvent>> s_eventHandlers = [];
    private static nint s_eventObserver;
    private static readonly (string Name, MacWindowEvent Event)[] s_eventNames =
    [
        ("NSWindowWillMoveNotification", MacWindowEvent.WillMove),
        ("NSWindowDidMoveNotification", MacWindowEvent.DidMove),
        ("NSWindowDidEndLiveResizeNotification", MacWindowEvent.DidEndLiveResize),
        ("NSWindowWillMiniaturizeNotification", MacWindowEvent.WillMiniaturize),
        ("NSWindowDidMiniaturizeNotification", MacWindowEvent.DidMiniaturize),
        ("NSWindowDidDeminiaturizeNotification", MacWindowEvent.DidDeminiaturize),
        ("NSWindowDidResizeNotification", MacWindowEvent.DidResize),
        ("NSWindowDidBecomeMainNotification", MacWindowEvent.DidBecomeMain),
        ("NSWindowDidResignMainNotification", MacWindowEvent.DidResignMain),
        ("NSWindowDidBecomeKeyNotification", MacWindowEvent.DidBecomeKey),
        ("NSWindowDidResignKeyNotification", MacWindowEvent.DidResignKey),
        ("NSWindowWillEnterFullScreenNotification", MacWindowEvent.WillEnterFullScreen),
        ("NSWindowDidEnterFullScreenNotification", MacWindowEvent.DidEnterFullScreen),
        ("NSWindowWillExitFullScreenNotification", MacWindowEvent.WillExitFullScreen),
        ("NSWindowDidExitFullScreenNotification", MacWindowEvent.DidExitFullScreen),
        ("NSWindowWillBeginSheetNotification", MacWindowEvent.WillBeginSheet),
        ("NSWindowDidEndSheetNotification", MacWindowEvent.DidEndSheet),
    ];

    /// <summary>Calls <paramref name="handler"/> for the window delegate notifications Electron's
    /// <c>ElectronNSWindowDelegate</c> turns into move, resize and minimize events, until <see cref="StopObservingEvents"/>.
    /// Returns the NSWindow, or 0. Main thread only.</summary>
    public static nint ObserveEvents(nint contentView, Action<MacWindowEvent> handler)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return 0;
        if (s_eventObserver == 0)
        {
            fixed (byte* name = "RiftwayWindowEventObserver"u8)
            {
                var cls = objc_getClass(name);
                if (cls == 0)
                {
                    cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
                    fixed (byte* types = "v@:@"u8)
                        class_addMethod(cls, Sel("riftwayWindowEvent:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnWindowEvent, types);
                    objc_registerClassPair(cls);
                }
                s_eventObserver = Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
            }
        }
        s_eventHandlers[window] = handler;
        var center = Send(Class("NSNotificationCenter"u8), Sel("defaultCenter"u8));
        foreach (var (name, _) in s_eventNames)
            ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)s_send)(center, Sel("addObserver:selector:name:object:"u8),
                s_eventObserver, Sel("riftwayWindowEvent:"u8), NotificationName(name), window);
        return window;
    }

    /// <summary>Stops an <see cref="ObserveEvents"/> once its window is gone.</summary>
    public static void StopObservingEvents(nint window)
    {
        if (!s_eventHandlers.Remove(window)) return;
        var center = Send(Class("NSNotificationCenter"u8), Sel("defaultCenter"u8));
        foreach (var (name, _) in s_eventNames)
            ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)s_send)(center, Sel("removeObserver:name:object:"u8), s_eventObserver, NotificationName(name), window);
    }

    /// <summary><c>windowWillMove:</c>'s bounds: the window's origin with its content's size, in screen coordinates from
    /// the top of the primary display (gfx's <c>ScreenRectFromNSRect</c>).</summary>
    public static (double X, double Y, double Width, double Height) WillMoveBounds(nint window)
    {
        var frame = Frame(window);
        var content = Frame(Send(window, Sel("contentView"u8)));
        var primary = Send(Send(Class("NSScreen"u8), Sel("screens"u8)), Sel("firstObject"u8));
        var primaryHeight = primary == 0 ? 0 : Frame(primary).Height;
        return ScreenRectFromNSRect(frame.X, frame.Y, content.Width, content.Height, primaryHeight);
    }

    /// <summary>gfx's <c>ScreenRectFromNSRect</c>: AppKit's bottom-left coordinates flipped against the primary display.</summary>
    public static (double X, double Y, double Width, double Height) ScreenRectFromNSRect(double x, double y, double width, double height, double primaryHeight)
        => (x, primaryHeight - y - height, width, height);

    [UnmanagedCallersOnly]
    private static void OnWindowEvent(nint self, nint cmd, nint notification)
    {
        var window = Send(notification, Sel("object"u8));
        if (!s_eventHandlers.TryGetValue(window, out var handler)) return;
        var name = ReadNSString(Send(notification, Sel("name"u8)));
        foreach (var (n, e) in s_eventNames)
        {
            if (n != name) continue;
            try { handler(e); }
            catch (Exception ex) { Console.Error.WriteLine($"[Riftway] Unhandled exception in a window event: {ex}"); }
            return;
        }
    }

    private static nint NotificationName(string name)
        => *(nint*)NativeLibrary.GetExport(NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit"), name);
    private static readonly nint s_ops = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary><c>setMovable:</c>.</summary>
    public static void SetMovable(nint contentView, bool movable)
    {
        if (NSWindowOf(contentView) is var window && window != 0) SendBool(window, Sel("setMovable:"u8), movable ? (byte)1 : (byte)0);
    }

    /// <summary><c>isMovable</c>; true without a window.</summary>
    public static bool IsMovable(nint contentView) => NSWindowOf(contentView) is not (var window and not 0) || SendGetBool(window, Sel("isMovable"u8)) != 0;

    /// <summary><c>windowNumber</c>, Electron's macOS <c>GetAcceleratedWidget</c>.</summary>
    public static long WindowNumber(nint contentView) => NSWindowOf(contentView) is var window && window != 0 ? Send(window, Sel("windowNumber"u8)) : 0;

    /// <summary>Electron's <c>MoveTop</c>: <c>orderWindowByShuffling:NSWindowAbove relativeTo:0</c>.</summary>
    public static void MoveTop(nint contentView)
    {
        if (NSWindowOf(contentView) is var window && window != 0) Shuffle(window, 0);
    }

    /// <summary>Electron's <c>MoveAbove</c> after parsing: false when no process owns the window
    /// <paramref name="windowId"/>, else orders this window just above it.</summary>
    public static bool MoveAbove(nint contentView, uint windowId)
    {
        if (WindowOwnerPid(windowId) == 0) return false;
        if (NSWindowOf(contentView) is var window && window != 0) Shuffle(window, windowId);
        return true;
    }

    // NSWindowAbove; the shuffling variant keeps the window's place among its own app's windows as Electron's does.
    private static void Shuffle(nint window, long relativeTo)
        => ((delegate* unmanaged<nint, nint, nint, nint, void>)s_ops)(window, Sel("orderWindowByShuffling:relativeTo:"u8), 1, (nint)relativeTo);

    /// <summary>WebRTC's <c>GetWindowOwnerPid</c>: the process owning the window, from its CoreGraphics description; 0 for
    /// none.</summary>
    public static int WindowOwnerPid(uint windowId)
    {
        var cg = NativeLibrary.Load(CoreGraphicsLib);
        var cf = NativeLibrary.Load(CoreFoundationLib);
        var createArray = (delegate* unmanaged<nint, nint*, nint, nint, nint>)NativeLibrary.GetExport(cf, "CFArrayCreate");
        var describe = (delegate* unmanaged<nint, nint>)NativeLibrary.GetExport(cg, "CGWindowListCreateDescriptionFromArray");
        var count = (delegate* unmanaged<nint, nint>)NativeLibrary.GetExport(cf, "CFArrayGetCount");
        var at = (delegate* unmanaged<nint, nint, nint>)NativeLibrary.GetExport(cf, "CFArrayGetValueAtIndex");
        var get = (delegate* unmanaged<nint, nint, nint>)NativeLibrary.GetExport(cf, "CFDictionaryGetValue");
        var number = (delegate* unmanaged<nint, nint, int*, byte>)NativeLibrary.GetExport(cf, "CFNumberGetValue");
        var release = (delegate* unmanaged<nint, void>)NativeLibrary.GetExport(cf, "CFRelease");
        var ownerKey = *(nint*)NativeLibrary.GetExport(cg, "kCGWindowOwnerPID");
        // A CFArray of raw CGWindowIDs, without callbacks, as WebRTC builds it.
        var value = (nint)windowId;
        var ids = createArray(0, &value, 1, 0);
        if (ids == 0) return 0;
        try
        {
            var descriptions = describe(ids);
            if (descriptions == 0) return 0;
            try
            {
                if (count(descriptions) == 0) return 0;
                var pidNumber = get(at(descriptions, 0), ownerKey);
                int pid = 0;
                // kCFNumberIntType.
                return pidNumber != 0 && number(pidNumber, 9, &pid) != 0 ? pid : 0;
            }
            finally { release(descriptions); }
        }
        finally { release(ids); }
    }

    // Windows that may not become key or main: Electron's disableKeyOrMainWindow, which Chromium's window lacks.
    private static readonly HashSet<nint> s_unfocusable = [];

    /// <summary>Electron's macOS <c>SetFocusable</c>: ElectronNSWindow's <c>disableKeyOrMainWindow</c>, here the window
    /// subclass's <c>canBecomeKeyWindow</c> and <c>canBecomeMainWindow</c>; it cannot take focus away from a window that
    /// has it.</summary>
    public static void SetFocusable(nint contentView, bool focusable)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        UseRiftwaySubclass(window);
        if (focusable) s_unfocusable.Remove(window);
        else s_unfocusable.Add(window);
    }

    /// <summary>Whether the window may become key and main; true without a window.</summary>
    public static bool IsFocusable(nint contentView)
    {
        var window = NSWindowOf(contentView);
        return window == 0 || !s_unfocusable.Contains(window);
    }

    // Each window's accessible title as an NSString it retains; the views title otherwise.
    private static readonly Dictionary<nint, nint> s_accessibleTitles = [];

    /// <summary>Electron's <c>SetAccessibleTitle</c>: the title VoiceOver reads for the window, which Chromium's window
    /// takes from the Views accessible title CEF does not expose; empty goes back to the window's own.</summary>
    public static void SetAccessibleTitle(nint contentView, string title)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        UseRiftwaySubclass(window);
        if (s_accessibleTitles.Remove(window, out var old)) Send(old, Sel("release"u8));
        if (title.Length > 0) s_accessibleTitles[window] = Send(NSString(title), Sel("retain"u8));
    }

    /// <summary>Forgets a <see cref="SetAccessibleTitle"/> once its window is gone.</summary>
    public static void ForgetAccessibleTitle(nint window)
    {
        if (s_accessibleTitles.Remove(window, out var title)) Send(title, Sel("release"u8));
    }

    [UnmanagedCallersOnly]
    private static nint OnAccessibilityTitle(nint self, nint cmd)
    {
        if (s_accessibleTitles.TryGetValue(self, out var title)) return title;
        var super = new ObjcSuper { Receiver = self, SuperClass = RiftwaySuperclass(self) };
        return ((delegate* unmanaged<ObjcSuper*, nint, nint>)s_msgSendSuper)(&super, cmd);
    }

    private static readonly Dictionary<nint, Action<float>> s_rotateHandlers = [];
    private static readonly Dictionary<nint, Action<string>> s_swipeHandlers = [];
    // The frame view classes whose swipeWithEvent: Riftway replaced, with AppKit's own implementation.
    private static readonly Dictionary<nint, nint> s_originalSwipes = [];

    /// <summary>Electron's <c>rotate-gesture</c> and <c>swipe</c>: <c>rotateWithEvent:</c> on the window, and
    /// <c>swipeWithEvent:</c> on its frame view's class, which Electron swizzles because Chromium's content view
    /// overrides it and passes it up. Returns the NSWindow, for <see cref="ForgetGestures"/>.</summary>
    public static nint ObserveGestures(nint contentView, Action<float> rotated, Action<string> swiped)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return 0;
        UseRiftwaySubclass(window);
        s_rotateHandlers[window] = rotated;
        s_swipeHandlers[window] = swiped;
        var frameView = Send(Send(window, Sel("contentView"u8)), Sel("superview"u8));
        var cls = frameView == 0 ? 0 : object_getClass(frameView);
        if (cls != 0 && !s_originalSwipes.ContainsKey(cls))
        {
            // The implementation the class has, its own or inherited; replacing an inherited one returns nothing.
            s_originalSwipes[cls] = class_getMethodImplementation(cls, Sel("swipeWithEvent:"u8));
            fixed (byte* types = "v@:@"u8)
                class_replaceMethod(cls, Sel("swipeWithEvent:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnSwipeWithEvent, types);
        }
        return window;
    }

    /// <summary>Forgets an <see cref="ObserveGestures"/> once its window is gone.</summary>
    public static void ForgetGestures(nint window)
    {
        s_rotateHandlers.Remove(window);
        s_swipeHandlers.Remove(window);
    }

    [UnmanagedCallersOnly]
    private static void OnRotateWithEvent(nint self, nint cmd, nint e)
    {
        if (!s_rotateHandlers.TryGetValue(self, out var handler))
        {
            var super = new ObjcSuper { Receiver = self, SuperClass = RiftwaySuperclass(self) };
            ((delegate* unmanaged<ObjcSuper*, nint, nint, void>)s_msgSendSuper)(&super, cmd, e);
            return;
        }
        try { handler(((delegate* unmanaged<nint, nint, float>)s_msgSend)(e, Sel("rotation"u8))); }
        catch (Exception ex) { Console.Error.WriteLine($"[Riftway] Unhandled exception in rotate-gesture: {ex}"); }
    }

    [UnmanagedCallersOnly]
    private static void OnSwipeWithEvent(nint self, nint cmd, nint e)
    {
        var window = Send(self, Sel("window"u8));
        if (s_swipeHandlers.TryGetValue(window, out var handler))
        {
            var dx = ((delegate* unmanaged<nint, nint, double>)s_msgSend)(e, Sel("deltaX"u8));
            var dy = ((delegate* unmanaged<nint, nint, double>)s_msgSend)(e, Sel("deltaY"u8));
            if (MacSwipe.Direction(dx, dy) is { } direction)
            {
                try { handler(direction); }
                catch (Exception ex) { Console.Error.WriteLine($"[Riftway] Unhandled exception in swipe: {ex}"); }
            }
            return;
        }
        // Not Riftway's window: AppKit's own frame view behaviour.
        for (var cls = object_getClass(self); cls != 0; cls = class_getSuperclass(cls))
            if (s_originalSwipes.TryGetValue(cls, out var original) && original != 0)
            {
                ((delegate* unmanaged<nint, nint, nint, void>)original)(self, cmd, e);
                return;
            }
    }

    // Each window's NSVisualEffectView, as ElectronNSWindow's vibrantView.
    private static readonly Dictionary<nint, nint> s_vibrantViews = [];

    /// <summary>Electron's <c>SetVibrancy</c>: an <c>NSVisualEffectView</c> of <paramref name="material"/> behind the
    /// window's content, made once and blending with what is behind the window; null removes it. Electron hosts the view
    /// in the Views root view, which CEF does not expose, so it goes under Chromium's content view instead.
    /// <paramref name="durationMs"/> fades it in or out.</summary>
    public static void SetVibrancy(nint contentView, long? material, long state, int durationMs)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        s_vibrantViews.TryGetValue(window, out var view);
        var seconds = durationMs / 1000.0;
        if (material is null)
        {
            if (view == 0) return;
            s_vibrantViews.Remove(window);
            if (durationMs > 0)
            {
                Animate(view, 0, seconds);
                // The view outlives the fade: performSelector keeps it until it runs.
                ((delegate* unmanaged<nint, nint, nint, nint, double, void>)s_send)(view, Sel("performSelector:withObject:afterDelay:"u8), Sel("removeFromSuperview"u8), 0, seconds);
            }
            else Send(view, Sel("removeFromSuperview"u8));
            Send(view, Sel("release"u8));
            return;
        }
        if (view == 0)
        {
            var content = Send(window, Sel("contentView"u8));
            var frameView = Send(content, Sel("superview"u8));
            var frame = Frame(content);
            view = ((delegate* unmanaged<nint, nint, NSRect, nint>)s_send)(Send(Class("NSVisualEffectView"u8), Sel("alloc"u8)), Sel("initWithFrame:"u8), frame);
            // NSViewWidthSizable | NSViewHeightSizable; NSVisualEffectBlendingModeBehindWindow.
            SendNUInt(view, Sel("setAutoresizingMask:"u8), 2 | 16);
            SendNUInt(view, Sel("setBlendingMode:"u8), 0);
            SendNUInt(view, Sel("setState:"u8), (nuint)state);
            // NSWindowBelow.
            ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)s_send)(frameView, Sel("addSubview:positioned:relativeTo:"u8), view, -1, content);
            s_vibrantViews[window] = view;
            if (durationMs > 0)
            {
                ((delegate* unmanaged<nint, nint, double, void>)s_send)(view, Sel("setAlphaValue:"u8), 0);
                Animate(view, 1, seconds);
            }
        }
        SendNUInt(view, Sel("setMaterial:"u8), (nuint)material.Value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSEdgeInsets
    {
        public double Top, Left, Bottom, Right;
    }

    /// <summary>Electron's <c>UpdateVibrancyRadii</c> mask: a rounded rectangle <paramref name="radius"/> round, stretched
    /// over the vibrant view; drawn with lockFocus where Electron uses a drawing handler.</summary>
    public static void SetVibrancyMask(nint contentView, double radius)
    {
        var window = NSWindowOf(contentView);
        if (window == 0 || !s_vibrantViews.TryGetValue(window, out var view)) return;
        var dimension = 2 * radius + 1;
        var image = ((delegate* unmanaged<nint, nint, NSSize, nint>)s_send)(Send(Class("NSImage"u8), Sel("alloc"u8)), Sel("initWithSize:"u8),
            new NSSize { Width = dimension, Height = dimension });
        Send(image, Sel("lockFocus"u8));
        var path = ((delegate* unmanaged<nint, nint, NSRect, double, double, nint>)s_send)(Class("NSBezierPath"u8), Sel("bezierPathWithRoundedRect:xRadius:yRadius:"u8),
            new NSRect { Width = dimension, Height = dimension }, radius, radius);
        Send(path, Sel("fill"u8));
        Send(image, Sel("unlockFocus"u8));
        ((delegate* unmanaged<nint, nint, NSEdgeInsets, void>)s_send)(image, Sel("setCapInsets:"u8), new NSEdgeInsets { Top = radius, Left = radius, Bottom = radius, Right = radius });
        // NSImageResizingModeStretch.
        SendNUInt(image, Sel("setResizingMode:"u8), 1);
        SendObject(view, Sel("setMaskImage:"u8), image);
        Send(image, Sel("release"u8));
    }

    /// <summary>Electron's <c>SetBorderless</c>: NSWindowStyleMaskTitled off, or back on.</summary>
    public static void SetBorderless(nint contentView, bool borderless)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        var mask = (nuint)Send(window, Sel("styleMask"u8));
        SendNUInt(window, Sel("setStyleMask:"u8), borderless ? mask & ~TitledStyle : mask | TitledStyle);
    }

    /// <summary>Whether the window's style mask has NSWindowStyleMaskTitled.</summary>
    public static bool IsTitled(nint contentView) => NSWindowOf(contentView) is var window && window != 0 && ((nuint)Send(window, Sel("styleMask"u8)) & TitledStyle) != 0;

    /// <summary>Whether the window has a vibrant view.</summary>
    public static bool HasVibrancy(nint contentView) => NSWindowOf(contentView) is var window && window != 0 && s_vibrantViews.ContainsKey(window);

    /// <summary>Releases the window's vibrant view once the window is gone.</summary>
    public static void ForgetVibrancy(nint window)
    {
        if (s_vibrantViews.Remove(window, out var view)) Send(view, Sel("release"u8));
    }

    private static void Animate(nint view, double alpha, double seconds)
    {
        var context = Class("NSAnimationContext"u8);
        Send(context, Sel("beginGrouping"u8));
        ((delegate* unmanaged<nint, nint, double, void>)s_send)(Send(context, Sel("currentContext"u8)), Sel("setDuration:"u8), seconds);
        ((delegate* unmanaged<nint, nint, double, void>)s_send)(Send(view, Sel("animator"u8)), Sel("setAlphaValue:"u8), alpha);
        Send(context, Sel("endGrouping"u8));
    }

    /// <summary>Chromium's translucent widget for a vibrant window: a clear, non-opaque window.</summary>
    public static void MakeTranslucent(nint contentView)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        MakeTransparent(contentView);
        SendBool(window, Sel("setOpaque:"u8), 0);
    }

    /// <summary><c>deminiaturize:</c>, Electron's macOS <c>Restore</c>.</summary>
    public static void Deminiaturize(nint contentView)
    {
        if (NSWindowOf(contentView) is var window and not 0) SendObject(window, Sel("deminiaturize:"u8), 0);
    }

    /// <summary>Forgets a <see cref="SetFocusable"/> once its window is gone.</summary>
    public static void ForgetFocusable(nint window) => s_unfocusable.Remove(window);

    [UnmanagedCallersOnly]
    private static byte CanBecomeKeyOrMain(nint self, nint cmd)
    {
        if (s_unfocusable.Contains(self)) return 0;
        var super = new ObjcSuper { Receiver = self, SuperClass = RiftwaySuperclass(self) };
        return ((delegate* unmanaged<ObjcSuper*, nint, byte>)s_msgSendSuper)(&super, cmd);
    }

    /// <summary>Electron's macOS <c>SetEnabled</c>: disabling attaches a half-transparent sheet the size of the window;
    /// enabling ends the attached sheet, if any.</summary>
    public static void SetEnabledBySheet(nint contentView, bool enable)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        if (enable)
        {
            if (Send(window, Sel("attachedSheet"u8)) is var sheet and not 0) SendObject(window, Sel("endSheet:"u8), sheet);
            return;
        }
        var frame = Frame(window);
        // NSWindowStyleMaskTitled, NSBackingStoreBuffered.
        var cover = ((delegate* unmanaged<nint, nint, NSRect, nuint, nuint, byte, nint>)s_ops)(Send(Class("NSWindow"u8), Sel("alloc"u8)),
            Sel("initWithContentRect:styleMask:backing:defer:"u8), new NSRect { Width = frame.Width, Height = frame.Height }, 1, 2, 0);
        ((delegate* unmanaged<nint, nint, double, void>)s_ops)(cover, Sel("setAlphaValue:"u8), 0.5);
        // Electron's handler only logs; a nil handler is allowed.
        ((delegate* unmanaged<nint, nint, nint, nint, void>)s_ops)(window, Sel("beginSheet:completionHandler:"u8), cover, 0);
        Send(cover, Sel("release"u8));
    }

    /// <summary>Electron's macOS <c>IsEnabled</c>: no sheet attached.</summary>
    public static bool HasAttachedSheet(nint contentView) => NSWindowOf(contentView) is var window && window != 0 && Send(window, Sel("attachedSheet"u8)) != 0;

    private static bool RespondsTo(nint obj, nint selector) => ((delegate* unmanaged<nint, nint, nint, byte>)s_ops)(obj, Sel("respondsToSelector:"u8), selector) != 0;

    /// <summary>Makes the window see-through where nothing is painted, as Electron's NativeWindowMac does for
    /// <c>transparent</c>: a clear background, which also drops the shadow; <see cref="HideTitleBar"/> does the rest.</summary>
    public static void MakeTransparent(nint contentView)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        SendObject(window, Sel("setBackgroundColor:"u8), Send(Class("NSColor"u8), Sel("clearColor"u8)));
    }

    /// <summary>Electron's block for a transparent or frameless window: a transparent title bar without its title, and no
    /// opacity, which removes the non-transparent corners.</summary>
    public static void HideTitleBar(nint contentView)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        SetTitlebarAppearsTransparent(contentView, true);
        SetTitleVisible(contentView, false);
        SendBool(window, Sel("setOpaque:"u8), 0);
    }

    /// <summary><c>setTitlebarAppearsTransparent:</c>.</summary>
    public static void SetTitlebarAppearsTransparent(nint contentView, bool transparent)
    {
        if (NSWindowOf(contentView) is var window and not 0) SendBool(window, Sel("setTitlebarAppearsTransparent:"u8), transparent ? (byte)1 : (byte)0);
    }

    /// <summary><c>setTitleVisibility:</c> NSWindowTitleVisible or NSWindowTitleHidden.</summary>
    public static void SetTitleVisible(nint contentView, bool visible)
    {
        if (NSWindowOf(contentView) is var window and not 0) SendNUInt(window, Sel("setTitleVisibility:"u8), visible ? 0u : 1u);
    }

    /// <summary><c>setExcludedFromWindowsMenu:</c>, Electron's <c>SetExcludedFromShownWindowsMenu</c>.</summary>
    public static void SetExcludedFromWindowsMenu(nint contentView, bool excluded)
    {
        if (NSWindowOf(contentView) is var window and not 0) SendBool(window, Sel("setExcludedFromWindowsMenu:"u8), excluded ? (byte)1 : (byte)0);
    }

    /// <summary><c>isExcludedFromWindowsMenu</c>.</summary>
    public static bool IsExcludedFromWindowsMenu(nint contentView)
        => NSWindowOf(contentView) is var window && window != 0 && SendGetBool(window, Sel("isExcludedFromWindowsMenu"u8)) != 0;

    /// <summary>Electron's <c>GetWindowButtonVisibility</c>: any of the three standard buttons shown, a missing one counting
    /// as shown (<c>![nil isHidden]</c>).</summary>
    public static bool AnyWindowButtonShown(nint contentView)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return false;
        for (nuint kind = 0; kind <= 2; kind++)
        {
            var button = SendNUIntObject(window, Sel("standardWindowButton:"u8), kind);
            if (button == 0 || SendGetBool(button, Sel("isHidden"u8)) == 0) return true;
        }
        return false;
    }

    /// <summary>[NSWindow isOpaque].</summary>
    public static bool IsOpaque(nint contentView)
        => NSWindowOf(contentView) is var window && window != 0 && SendGetBool(window, Sel("isOpaque"u8)) != 0;

    /// <summary>Whether a collection behaviour flag (NSWindowCollectionBehavior) is set.</summary>
    public static bool HasCollectionBehavior(nint contentView, nuint flag)
        => NSWindowOf(contentView) is var window && window != 0 && ((nuint)Send(window, Sel("collectionBehavior"u8)) & flag) != 0;

    /// <summary>Sets or clears a collection behaviour flag, as Electron's <c>SetCollectionBehavior</c>.</summary>
    /// <summary>Electron's <c>ShowInactive</c> through CEF's show: while <paramref name="show"/> runs, Chromium's window
    /// refuses to become key (<c>preventKeyWindow</c>) and leaves the app inactive (<c>activationIndependence</c>); then
    /// <c>orderFrontRegardless</c> brings it to the front without focus.</summary>
    public static void ShowInactive(nint contentView, Action show)
    {
        var window = NSWindowOf(contentView);
        var prevent = window != 0 && RespondsTo(window, Sel("setPreventKeyWindow:"u8));
        var independent = window != 0 && RespondsTo(window, Sel("setActivationIndependence:"u8));
        var wasPrevented = prevent && SendGetBool(window, Sel("preventKeyWindow"u8)) != 0;
        var wasIndependent = independent && SendGetBool(window, Sel("activationIndependence"u8)) != 0;
        if (prevent) SendBool(window, Sel("setPreventKeyWindow:"u8), 1);
        if (independent) SendBool(window, Sel("setActivationIndependence:"u8), 1);
        try { show(); }
        finally
        {
            if (prevent) SendBool(window, Sel("setPreventKeyWindow:"u8), wasPrevented ? (byte)1 : (byte)0);
            if (independent) SendBool(window, Sel("setActivationIndependence:"u8), wasIndependent ? (byte)1 : (byte)0);
        }
        if (window != 0) Send(window, Sel("orderFrontRegardless"u8));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize
    {
        public double Width, Height;
    }


    private static readonly Dictionary<nint, Func<(double Width, double Height), (double Width, double Height)>> s_resizeHandlers = [];
    private static readonly Dictionary<nint, Action> s_failedFullScreenHandlers = [];
    private static readonly Dictionary<nint, Func<(double X, double Y)>> s_sheetOffsets = [];
    private static readonly Dictionary<nint, Action> s_newTabHandlers = [];

    /// <summary>Electron's <c>ElectronNSWindowDelegate windowWillResize:toSize:</c>: the window's delegate (Chromium's) gains a
    /// runtime subclass whose resize hook asks <paramref name="handler"/> for the size, until <see cref="UnrouteWillResize"/>;
    /// other delegates of that class keep Chromium's. Returns the delegate, or 0.</summary>
    public static nint RouteWillResize(nint contentView, Func<(double Width, double Height), (double Width, double Height)> handler)
    {
        var window = NSWindowOf(contentView);
        var windowDelegate = window == 0 ? 0 : Send(window, Sel("delegate"u8));
        if (windowDelegate == 0) return 0;
        var cls = object_getClass(windowDelegate);
        var className = Marshal.PtrToStringUTF8((nint)class_getName(cls)) ?? "";
        if (!IsRiftwaySubclassed(cls))
        {
            var name = System.Text.Encoding.UTF8.GetBytes("Riftway_" + className + "\0");
            fixed (byte* n = name)
            {
                var subclass = objc_getClass(n);
                if (subclass == 0)
                {
                    subclass = objc_allocateClassPair(cls, n, 0);
                    s_riftwayClasses.Add(subclass);
                    fixed (byte* types = "{CGSize=dd}@:@{CGSize=dd}"u8)
                        class_addMethod(subclass, Sel("windowWillResize:toSize:"u8), (delegate* unmanaged<nint, nint, nint, NSSize, NSSize>)&OnWillResize, types);
                    fixed (byte* types = "v@:@"u8)
                        class_addMethod(subclass, Sel("windowDidFailToEnterFullScreen:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnDidFailToEnterFullScreen, types);
                    fixed (byte* types = "{CGRect={CGPoint=dd}{CGSize=dd}}@:@{CGRect={CGPoint=dd}{CGSize=dd}}"u8)
                        class_addMethod(subclass, Sel("windowWillUseStandardFrame:defaultFrame:"u8), (delegate* unmanaged<nint, nint, nint, NSRect, NSRect>)&OnWillUseStandardFrame, types);
                    fixed (byte* types = "v@:@"u8)
                        class_addMethod(subclass, Sel("newWindowForTab:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnNewWindowForTab, types);
                    fixed (byte* types = "{CGRect={CGPoint=dd}{CGSize=dd}}@:@@{CGRect={CGPoint=dd}{CGSize=dd}}"u8)
                        class_addMethod(subclass, Sel("window:willPositionSheet:usingRect:"u8), (delegate* unmanaged<nint, nint, nint, nint, NSRect, NSRect>)&OnWillPositionSheet, types);
                    objc_registerClassPair(subclass);
                }
                object_setClass(windowDelegate, subclass);
            }
        }
        s_resizeHandlers[windowDelegate] = handler;
        return windowDelegate;
    }

    /// <summary>Forgets a <see cref="RouteWillResize"/> once its window is gone.</summary>
    public static void UnrouteWillResize(nint windowDelegate)
    {
        s_resizeHandlers.Remove(windowDelegate);
        s_failedFullScreenHandlers.Remove(windowDelegate);
        s_sheetOffsets.Remove(windowDelegate);
        s_newTabHandlers.Remove(windowDelegate);
        s_zoomWidths.Remove(windowDelegate);
    }

    private static readonly Dictionary<nint, Func<double>> s_zoomWidths = [];

    /// <summary>Electron's <c>zoomToPageWidth</c>: the zoom button widens the window to <paramref name="preferredWidth"/>
    /// (0 for none) instead of filling the screen, unless Shift is held; on a delegate routed by <see cref="RouteWillResize"/>.</summary>
    public static void ZoomToPageWidth(nint windowDelegate, Func<double> preferredWidth)
    {
        if (windowDelegate != 0) s_zoomWidths[windowDelegate] = preferredWidth;
    }

    // NSEventModifierFlagShift.
    private const nuint ShiftFlag = 1 << 17;

    [UnmanagedCallersOnly]
    private static NSRect OnWillUseStandardFrame(nint self, nint cmd, nint window, NSRect frame)
    {
        if (!s_zoomWidths.TryGetValue(self, out var preferredWidth)) return frame;
        try
        {
            var current = Send(Class("NSApplication"u8), Sel("sharedApplication"u8));
            var e = Send(current, Sel("currentEvent"u8));
            if (e != 0 && ((nuint)Send(e, Sel("modifierFlags"u8)) & ShiftFlag) != 0) return frame;
            var now = Frame(window);
            var (x, y, width, height) = MacZoom.StandardFrame((frame.X, frame.Y, frame.Width, frame.Height), (now.X, now.Width), preferredWidth());
            return new NSRect { X = x, Y = y, Width = width, Height = height };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Riftway] Unhandled exception zooming to the page width: {e}");
            return frame;
        }
    }

    /// <summary>Calls <paramref name="handler"/> on the window delegate's <c>newWindowForTab:</c>, the tab bar's + button,
    /// on a delegate routed by <see cref="RouteWillResize"/>.</summary>
    public static void OnNewWindowForTab(nint windowDelegate, Action handler)
    {
        if (windowDelegate != 0) s_newTabHandlers[windowDelegate] = handler;
    }

    [UnmanagedCallersOnly]
    private static void OnNewWindowForTab(nint self, nint cmd, nint sender)
    {
        if (!s_newTabHandlers.TryGetValue(self, out var handler)) return;
        try { handler(); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception in new-window-for-tab: {e}"); }
    }

    /// <summary>Electron's tabbing setup: the identifier, or no tabbing for a null one.</summary>
    public static void SetTabbingIdentifier(nint contentView, string? identifier)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        // NSWindowTabbingModeDisallowed.
        if (identifier is null) SendNUInt(window, Sel("setTabbingMode:"u8), 2);
        else SendObject(window, Sel("setTabbingIdentifier:"u8), NSString(identifier));
    }

    /// <summary>Electron's <c>GetTabbingIdentifier</c>: null while tabbing is disallowed.</summary>
    public static string? GetTabbingIdentifier(nint contentView)
    {
        var window = NSWindowOf(contentView);
        if (window == 0 || (nuint)Send(window, Sel("tabbingMode"u8)) == 2) return null;
        return ReadNSString(Send(window, Sel("tabbingIdentifier"u8)));
    }

    /// <summary>Sends one of NSWindow's tab actions (<c>selectNextTab:</c> and the like) with a nil sender.</summary>
    public static void TabAction(nint contentView, string selector)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        var name = System.Text.Encoding.UTF8.GetBytes(selector + "\0");
        fixed (byte* p = name) SendObject(window, sel_registerName(p), 0);
    }

    /// <summary><c>addTabbedWindow:ordered:NSWindowAbove</c>.</summary>
    public static void AddTabbedWindow(nint contentView, nint otherContentView)
    {
        var window = NSWindowOf(contentView);
        var other = NSWindowOf(otherContentView);
        if (window != 0 && other != 0) ((delegate* unmanaged<nint, nint, nint, nint, void>)s_send)(window, Sel("addTabbedWindow:ordered:"u8), other, 1);
    }

    /// <summary>Whether two content views are in the same NSWindow.</summary>
    public static bool SameWindow(nint contentView, nint otherContentView) => NSWindowOf(contentView) == NSWindowOf(otherContentView);

    /// <summary>Places the window's sheets by <paramref name="offsets"/> (x from the left, y from the content's top), as
    /// Electron's <c>window:willPositionSheet:usingRect:</c>, on a delegate routed by <see cref="RouteWillResize"/>.</summary>
    public static void OnPositionSheet(nint windowDelegate, Func<(double X, double Y)> offsets)
    {
        if (windowDelegate != 0) s_sheetOffsets[windowDelegate] = offsets;
    }

    [UnmanagedCallersOnly]
    private static NSRect OnWillPositionSheet(nint self, nint cmd, nint window, nint sheet, NSRect rect)
    {
        if (!s_sheetOffsets.TryGetValue(self, out var offsets)) return rect;
        try
        {
            var (x, y) = MacSheet.Position(offsets(), Frame(Send(window, Sel("contentView"u8))).Height);
            return rect with { X = x, Y = y };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Riftway] Unhandled exception placing a sheet: {e}");
            return rect;
        }
    }

    /// <summary>Calls <paramref name="handler"/> on <c>windowDidFailToEnterFullScreen:</c>, which AppKit sends only to the
    /// window's delegate, routed by <see cref="RouteWillResize"/>.</summary>
    public static void OnFailedFullScreen(nint windowDelegate, Action handler)
    {
        if (windowDelegate != 0) s_failedFullScreenHandlers[windowDelegate] = handler;
    }

    [UnmanagedCallersOnly]
    private static void OnDidFailToEnterFullScreen(nint self, nint cmd, nint window)
    {
        var superClass = RiftwaySuperclass(self);
        // Chromium's delegate ends its transition here; the method is optional, so the superclass may have none.
        if (class_respondsToSelector(superClass, cmd) != 0)
        {
            var super = new ObjcSuper { Receiver = self, SuperClass = superClass };
            ((delegate* unmanaged<ObjcSuper*, nint, nint, void>)s_msgSendSuper)(&super, cmd, window);
        }
        if (!s_failedFullScreenHandlers.TryGetValue(self, out var handler)) return;
        try { handler(); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception after a failed full screen: {e}"); }
    }

    [UnmanagedCallersOnly]
    private static NSSize OnWillResize(nint self, nint cmd, nint sender, NSSize size)
    {
        if (s_resizeHandlers.TryGetValue(self, out var handler))
        {
            try
            {
                var (w, h) = handler((size.Width, size.Height));
                return new NSSize { Width = w, Height = h };
            }
            catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception in a resize: {e}"); }
        }
        var superClass = RiftwaySuperclass(self);
        // Chromium's delegate may not implement it; AppKit then takes the size as asked.
        if (class_respondsToSelector(superClass, cmd) == 0) return size;
        var super = new ObjcSuper { Receiver = self, SuperClass = superClass };
        return ((delegate* unmanaged<ObjcSuper*, nint, nint, NSSize, NSSize>)s_msgSendSuper)(&super, cmd, sender, size);
    }

    /// <summary>The primary display's height, against which AppKit's coordinates flip.</summary>
    public static double PrimaryScreenHeight()
    {
        var primary = Send(Send(Class("NSScreen"u8), Sel("screens"u8)), Sel("firstObject"u8));
        return primary == 0 ? 0 : Frame(primary).Height;
    }

    /// <summary>The frame, content size and size limits <c>windowWillResize:toSize:</c> works from.</summary>
    public static MacResizeGeometry GetResizeGeometry(nint contentView)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return default;
        var frame = Frame(window);
        var content = Frame(Send(window, Sel("contentView"u8)));
        var min = ((delegate* unmanaged<nint, nint, NSSize>)s_ops)(window, Sel("minSize"u8));
        var max = ((delegate* unmanaged<nint, nint, NSSize>)s_ops)(window, Sel("maxSize"u8));
        return new MacResizeGeometry(frame.X, frame.Y, frame.Width, frame.Height, content.Width, content.Height, min.Width, min.Height, max.Width, max.Height);
    }

    /// <summary>Electron's macOS <c>SetAspectRatio</c>: the content's (framed) or window's (frameless) aspect ratio, or the
    /// default 1 � 1 resize increments for none.</summary>
    public static void SetAppKitAspectRatio(nint contentView, double aspectRatio, bool hasFrame)
    {
        var window = NSWindowOf(contentView);
        if (window == 0) return;
        var set = (delegate* unmanaged<nint, nint, NSSize, void>)s_ops;
        if (aspectRatio > 0) set(window, hasFrame ? Sel("setContentAspectRatio:"u8) : Sel("setAspectRatio:"u8), new NSSize { Width = aspectRatio, Height = 1 });
        else set(window, Sel("setResizeIncrements:"u8), new NSSize { Width = 1, Height = 1 });
    }

    /// <summary>Replaces the window's whole collection behaviour.</summary>
    public static void SetCollectionBehaviorValue(nint contentView, nuint behavior)
    {
        if (NSWindowOf(contentView) is var window && window != 0) SendNUInt(window, Sel("setCollectionBehavior:"u8), behavior);
    }

    /// <summary>Adds <paramref name="mask"/> to the window's style mask.</summary>
    public static void AddStyleMask(nint contentView, nuint mask)
    {
        if (NSWindowOf(contentView) is var window && window != 0) SendNUInt(window, Sel("setStyleMask:"u8), (nuint)Send(window, Sel("styleMask"u8)) | mask);
    }

    /// <summary><c>kCGDesktopWindowLevel - 1</c>, Electron's level for a <c>desktop</c> window.</summary>
    public static long BelowDesktopLevel() => CGWindowLevelForKey(2) - 1;

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

    /// <summary>NSWindowCollectionBehaviorFullScreenPrimary.</summary>
    public const nuint FullScreenPrimary = 1 << 7;

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
    [DllImport(CoreGraphics)] private static extern int CGWindowLevelForKey(int key);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendNUInt(nint receiver, nint selector, nuint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern NSRect SendRect(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern void SendRectStret(NSRect* result, nint receiver, nint selector);
    [DllImport(ObjC)] private static extern nint object_getClass(nint obj);
    [DllImport(ObjC)] private static extern nint object_setClass(nint obj, nint cls);
    [DllImport(ObjC)] private static extern nint class_getSuperclass(nint cls);
    [DllImport(ObjC)] private static extern byte class_respondsToSelector(nint cls, nint selector);
    [DllImport(ObjC)] private static extern byte* class_getName(nint cls);
    [DllImport(ObjC)] private static extern nint class_replaceMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport(ObjC)] private static extern nint class_getMethodImplementation(nint cls, nint name);
    [DllImport(ObjC)] private static extern nint objc_getProtocol(byte* name);
    [DllImport(ObjC)] private static extern byte class_addProtocol(nint cls, nint protocol);
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
