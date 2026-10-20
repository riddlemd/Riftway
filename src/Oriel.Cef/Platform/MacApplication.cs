using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>Makes NSApp an NSApplication subclass implementing CefAppProtocol, which CEF requires
/// before initialisation on macOS.</summary>
/// <remarks>Chromium's message pump asks NSApp whether it is inside sendEvent:, to keep nested run
/// loops (menus, drags, modal dialogs) from re-entering it.</remarks>
[SupportedOSPlatform("macos")]
public static unsafe class MacApplication
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjcSuper
    {
        public nint Receiver;
        public nint SuperClass;
    }

    private static nint s_nsApplication;
    private static bool s_handlingSendEvent;
    private static bool s_userStoppedShutdown;

    /// <summary>Electron's <c>shouldShutdown_</c>: asked on <c>NSWorkspaceWillPowerOffNotification</c> whether the
    /// shutdown may go on. Set by <c>powerMonitor</c> once it has a listener.</summary>
    public static Func<bool>? ShutdownHandler { get; set; }

    /// <summary>What <c>terminate:</c> runs instead of AppKit's, as Electron's <c>Browser::Quit</c>; AppKit's
    /// own when null.</summary>
    public static Action? Quit { get; set; }

    /// <summary>CrAppControlProtocol's flag, which <c>base::mac::ScopedSendingEvent</c> sets around a nested menu loop
    /// so that a window closed from it waits for the loop to end.</summary>
    public static bool HandlingSendEvent
    {
        get => s_handlingSendEvent;
        set => s_handlingSendEvent = value;
    }

    /// <summary>Registers the subclass and creates the shared application from it; idempotent.</summary>
    /// <exception cref="InvalidOperationException">NSApp already exists as another class.</exception>
    public static void Install()
    {
        if (s_nsApplication != 0) return;

        s_nsApplication = objc_getClass(Utf8("NSApplication"));
        var cls = objc_allocateClassPair(s_nsApplication, Utf8("OrielApplication"), 0);
        if (cls == 0) throw new InvalidOperationException("OrielApplication is already registered with the Objective-C runtime.");

        class_addMethod(cls, Selector("isHandlingSendEvent"), (delegate* unmanaged<nint, nint, byte>)&IsHandlingSendEvent, Utf8(ObjCEncoding.Types("B@:")));
        class_addMethod(cls, Selector("setHandlingSendEvent:"), (delegate* unmanaged<nint, nint, byte, void>)&SetHandlingSendEvent, Utf8(ObjCEncoding.Types("v@:B")));
        class_addMethod(cls, Selector("sendEvent:"), (delegate* unmanaged<nint, nint, nint, void>)&SendEvent, Utf8("v@:@"));
        class_addMethod(cls, Selector("terminate:"), (delegate* unmanaged<nint, nint, nint, void>)&Terminate, Utf8("v@:@"));
        class_addMethod(cls, Selector("orielWillPowerOff:"), (delegate* unmanaged<nint, nint, nint, void>)&WillPowerOff, Utf8("v@:@"));

        // Chromium checks conformance in some builds; the protocols exist once the CEF framework is loaded.
        foreach (var name in new[] { "CrAppProtocol", "CrAppControlProtocol", "CefAppProtocol" })
        {
            var protocol = objc_getProtocol(Utf8(name));
            if (protocol != 0) class_addProtocol(cls, protocol);
        }

        objc_registerClassPair(cls);

        var app = MsgSend(cls, Selector("sharedApplication"));
        if (MsgSend(app, Selector("class")) != cls)
            throw new InvalidOperationException("NSApp was created before Oriel could install its NSApplication subclass.");

        // Electron's delegate observes it from applicationWillFinishLaunching:, which has not run yet.
        var workspace = MsgSend(MsgSend(objc_getClass(Utf8("NSWorkspace")), Selector("sharedWorkspace")), Selector("notificationCenter"));
        var powerOff = MsgSend(objc_getClass(Utf8("NSString")), Selector("stringWithUTF8String:"), (nint)Utf8("NSWorkspaceWillPowerOffNotification"));
        AddObserver(workspace, Selector("addObserver:selector:name:object:"), app, Selector("orielWillPowerOff:"), powerOff, 0);
    }

    // Electron's willPowerOff: the flag is never reset, so after a held shutdown terminate: stays ignored.
    [UnmanagedCallersOnly]
    private static void WillPowerOff(nint self, nint cmd, nint notification)
    {
        try { s_userStoppedShutdown = ShutdownHandler is { } handler && !handler(); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a shutdown handler: {e}"); }
    }

    [UnmanagedCallersOnly]
    private static void Terminate(nint self, nint cmd, nint sender)
    {
        if (s_userStoppedShutdown) return;
        if (Quit is { } quit)
        {
            try { quit(); }
            catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in terminate: {e}"); }
            return;
        }
        var super = new ObjcSuper { Receiver = self, SuperClass = s_nsApplication };
        MsgSendSuper(&super, cmd, sender);
    }

    [UnmanagedCallersOnly]
    private static byte IsHandlingSendEvent(nint self, nint cmd) => s_handlingSendEvent ? (byte)1 : (byte)0;

    [UnmanagedCallersOnly]
    private static void SetHandlingSendEvent(nint self, nint cmd, byte value) => s_handlingSendEvent = value != 0;

    [UnmanagedCallersOnly]
    private static void SendEvent(nint self, nint cmd, nint nsEvent)
    {
        var wasHandling = s_handlingSendEvent;
        s_handlingSendEvent = true;
        var super = new ObjcSuper { Receiver = self, SuperClass = s_nsApplication };
        MsgSendSuper(&super, cmd, nsEvent);
        s_handlingSendEvent = wasHandling;
    }

    private static nint Selector(string name) => sel_registerName(Utf8(name));

    // Selector and class names live for the process, so the buffers are never freed.
    private static byte* Utf8(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + '\0');
        var buffer = (byte*)NativeMemory.Alloc((nuint)bytes.Length);
        bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
        return buffer;
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint objc_getProtocol(byte* name);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport(ObjC)] private static extern byte class_addProtocol(nint cls, nint protocol);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint MsgSend(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint MsgSend(nint receiver, nint selector, nint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void AddObserver(nint receiver, nint selector, nint observer, nint action, nint name, nint obj);
    [DllImport(ObjC, EntryPoint = "objc_msgSendSuper")] private static extern void MsgSendSuper(ObjcSuper* super, nint selector, nint arg);
}
