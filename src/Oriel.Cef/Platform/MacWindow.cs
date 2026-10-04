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
    [DllImport(ObjC, EntryPoint = "objc_msgSendSuper")] private static extern void SendSuperObject(ObjcSuper* super, nint selector, nint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSetFrame(nint receiver, nint selector, NSRect frame, byte display);
}
