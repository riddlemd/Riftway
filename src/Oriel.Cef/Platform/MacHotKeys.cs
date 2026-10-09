using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The Carbon hot keys, media-key event tap and keyboard-layout watch behind macOS global shortcuts, as
/// Chromium's <c>GlobalAcceleratorListenerMac</c> and <c>MediaKeysListenerImpl</c> use them. Main thread only.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacHotKeys
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";

    private const uint KeyboardEventClass = 0x6B657962; // 'keyb'
    private const uint HotKeyPressedKind = 5; // kEventHotKeyPressed
    private const uint DirectObjectParam = 0x2D2D2D2D; // '----'
    private const uint HotKeyIdType = 0x686B6964; // 'hkid'
    private const uint UnknownCreator = 0x3F3F3F3F; // kUnknownType '????'
    private const ushort KeyActionDown = 0; // kUCKeyActionDown
    private const uint NoDeadKeysBit = 0; // kUCKeyTranslateNoDeadKeysBit
    private const ushort SpaceKeyCode = 0x31; // kVK_Space
    private const uint SystemDefined = 14; // NX_SYSDEFINED, NSEventTypeSystemDefined
    private const uint TapDisabledByTimeout = 0xFFFFFFFE; // kCGEventTapDisabledByTimeout
    private const short MediaKeysSubtype = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_layoutDataKey = *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(Carbon), "kTISPropertyUnicodeKeyLayoutData");
    private static readonly nint s_commonModes = *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFRunLoopCommonModes");

    private static nint s_handler;
    private static Action<uint>? s_hotKeyPressed;
    private static nint s_tap;
    private static nint s_tapSource;
    private static Func<int, bool>? s_mediaKey;
    private static bool s_permissionCheckPending;
    private static int s_tapGeneration;
    private static Action? s_layoutChanged;
    private static nint s_layoutObserver;

    /// <summary><c>base::apple::CreatorCodeForApplication</c>: the main bundle's creator code, the hot keys' signature.</summary>
    public static uint CreatorCode()
    {
        var bundle = CFBundleGetMainBundle();
        if (bundle == 0) return UnknownCreator;
        var creator = UnknownCreator;
        CFBundleGetPackageInfo(bundle, null, &creator);
        return creator;
    }

    /// <summary><c>RegisterEventHotKey</c> on the application target; 0 when Carbon refuses, as for a combination
    /// another process holds.</summary>
    public static nint Register(int keyCode, uint carbonModifiers, uint signature, uint id)
    {
        nint hotKey = 0;
        var hotKeyId = new EventHotKeyId { Signature = signature, Id = id };
        return RegisterEventHotKey((uint)keyCode, carbonModifiers, hotKeyId, GetApplicationEventTarget(), 0, &hotKey) == 0 ? hotKey : 0;
    }

    /// <summary><c>UnregisterEventHotKey</c>.</summary>
    public static void Unregister(nint hotKey) => _ = UnregisterEventHotKey(hotKey);

    /// <summary><c>StartWatchingHotKeys</c>: installs the application's <c>kEventHotKeyPressed</c> handler, which calls
    /// <paramref name="pressed"/> with the hot key's id.</summary>
    public static void StartWatching(Action<uint> pressed)
    {
        if (s_handler != 0) return;
        s_hotKeyPressed = pressed;
        var spec = new EventTypeSpec { EventClass = KeyboardEventClass, EventKind = HotKeyPressedKind };
        nint handler = 0;
        if (InstallEventHandler(GetApplicationEventTarget(), (delegate* unmanaged<nint, nint, nint, int>)&OnHotKey, 1, &spec, 0, &handler) == 0)
            s_handler = handler;
    }

    /// <summary><c>StopWatchingHotKeys</c>: removes the handler.</summary>
    public static void StopWatching()
    {
        if (s_handler == 0) return;
        _ = RemoveEventHandler(s_handler);
        s_handler = 0;
        s_hotKeyPressed = null;
    }

    [UnmanagedCallersOnly]
    private static int OnHotKey(nint nextHandler, nint carbonEvent, nint userData)
    {
        EventHotKeyId hotKeyId;
        var result = GetEventParameter(carbonEvent, DirectObjectParam, HotKeyIdType, null, (nuint)sizeof(EventHotKeyId), null, &hotKeyId);
        if (result != 0) return result;
        try { s_hotKeyPressed?.Invoke(hotKeyId.Id); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a global shortcut: {e}"); }
        return 0;
    }

    /// <summary>A translator over the current keyboard layout, as Chromium's <c>NsKeyCodeAndModifiersToCharacter(key, 0)</c>:
    /// the character a key types with no modifiers, and whether it is a dead key (then the character it shows over a
    /// space). Dispose it to release the layout.</summary>
    public static KeyboardLayout CurrentLayout() => new(TISCopyCurrentKeyboardLayoutInputSource());

    /// <summary>A copy of the current keyboard layout input source.</summary>
    public sealed class KeyboardLayout : IDisposable
    {
        private nint _source;

        internal KeyboardLayout(nint source) => _source = source;

        /// <summary>The character key <paramref name="keyCode"/> types unmodified, and whether it is a dead key.</summary>
        public (char Character, bool DeadKey) Translate(int keyCode)
        {
            uint deadKeyState = 0;
            var character = TranslatedChar(_source, (ushort)keyCode, &deadKeyState);
            var dead = deadKeyState != 0;
            if (dead) character = TranslatedChar(_source, SpaceKeyCode, &deadKeyState);
            return (character, dead);
        }

        public void Dispose()
        {
            if (_source != 0) CFRelease(_source);
            _source = 0;
        }
    }

    // ui::TranslatedUnicodeCharFromKeyCode with no modifiers.
    private static char TranslatedChar(nint source, ushort keyCode, uint* deadKeyState)
    {
        var data = source == 0 ? 0 : TISGetInputSourceProperty(source, s_layoutDataKey);
        if (data == 0) return '�';
        var buffer = stackalloc char[4];
        nuint length = 4;
        // Chromium passes the bit's index (0) as the options, so dead keys still count.
        _ = UCKeyTranslate(CFDataGetBytePtr(data), keyCode, KeyActionDown, 0, LMGetKbdLast(), NoDeadKeysBit, deadKeyState, 4, &length, buffer);
        return buffer[0];
    }

    /// <summary>Calls <paramref name="changed"/> on each <c>NSTextInputContextKeyboardSelectionDidChangeNotification</c>,
    /// as the listener's <c>layout_change_listener_</c> does; once per process.</summary>
    public static void WatchLayoutChanges(Action changed)
    {
        s_layoutChanged = changed;
        if (s_layoutObserver != 0) return;
        fixed (byte* name = "OrielKeyboardLayoutObserver"u8)
        {
            var cls = objc_getClass(name);
            if (cls == 0)
            {
                cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
                fixed (byte* types = "v@:@"u8)
                    class_addMethod(cls, Sel("layoutChanged:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnLayoutChanged, types);
                objc_registerClassPair(cls);
            }
            s_layoutObserver = Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
        }
        var center = Send(Class("NSNotificationCenter"u8), Sel("defaultCenter"u8));
        var notification = NSString("NSTextInputContextKeyboardSelectionDidChangeNotification");
        ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)s_msgSend)(center, Sel("addObserver:selector:name:object:"u8), s_layoutObserver, Sel("layoutChanged:"u8), notification, 0);
    }

    [UnmanagedCallersOnly]
    private static void OnLayoutChanged(nint self, nint cmd, nint notification)
    {
        try { s_layoutChanged?.Invoke(); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception re-registering global shortcuts: {e}"); }
    }

    /// <summary>Electron's <c>IsTrustedAccessibilityClient(false)</c>: <c>AXIsProcessTrustedWithOptions</c> without the
    /// prompt.</summary>
    public static bool IsTrustedAccessibilityClient() => AXIsProcessTrusted() != 0;

    /// <summary><c>StartEventTapIfNecessary</c>: a session event tap on system-defined events in the main run loop.
    /// <paramref name="mediaKey"/> gets the Windows virtual key of each pressed media key and returns whether this app
    /// watches it, which swallows the event.</summary>
    public static void StartMediaTap(Func<int, bool> mediaKey)
    {
        s_mediaKey = mediaKey;
        if (s_tap != 0) return;
        var tap = CGEventTapCreate(1 /* kCGSessionEventTap */, 0 /* kCGHeadInsertEventTap */, 0 /* kCGEventTapOptionDefault */,
            1UL << (int)SystemDefined, (delegate* unmanaged<nint, uint, nint, nint, nint>)&OnTap, 0);
        if (tap == 0)
        {
            Console.Error.WriteLine("[Oriel] Error: failed to create event tap.");
            return;
        }
        var source = CFMachPortCreateRunLoopSource(0, tap, 0);
        if (source == 0)
        {
            Console.Error.WriteLine("[Oriel] Error: failed to create new run loop source.");
            CFRelease(tap);
            return;
        }
        s_tap = tap;
        s_tapSource = source;
        CFRunLoopAddSource(CFRunLoopGetCurrent(), source, s_commonModes);
    }

    /// <summary><c>StopEventTapIfNecessary</c>.</summary>
    public static void StopMediaTap()
    {
        s_permissionCheckPending = false;
        s_tapGeneration++;
        if (s_tap == 0) return;
        CFRunLoopRemoveSource(CFRunLoopGetCurrent(), s_tapSource, s_commonModes);
        CFMachPortInvalidate(s_tap);
        CFRelease(s_tap);
        CFRelease(s_tapSource);
        s_tap = 0;
        s_tapSource = 0;
    }

    [UnmanagedCallersOnly]
    private static nint OnTap(nint proxy, uint type, nint cgEvent, nint userInfo)
    {
        try
        {
            SchedulePermissionCheck();
            if (type == TapDisabledByTimeout)
            {
                CGEventTapEnable(s_tap, 1);
                return cgEvent;
            }
            var nsEvent = Send(Class("NSEvent"u8), Sel("eventWithCGEvent:"u8), cgEvent);
            if (nsEvent == 0) return cgEvent;
            if (type != SystemDefined || (nuint)Send(nsEvent, Sel("type"u8)) != SystemDefined
                || ((delegate* unmanaged<nint, nint, short>)s_msgSend)(nsEvent, Sel("subtype"u8)) != MediaKeysSubtype)
                return cgEvent;
            if (MacKeyCodes.MediaKeyPressed(Send(nsEvent, Sel("data1"u8))) is not { } key) return cgEvent;
            return s_mediaKey?.Invoke(key) == true ? 0 : cgEvent;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Oriel] Unhandled exception in a media-key global shortcut: {e}");
            return cgEvent;
        }
    }

    // IOHIDCheckAccess can block on TCC, so it runs off the main thread with one check in flight; a tap without the
    // permission is stopped, as Chromium's OnEventTapPermissionCheckCompleted does.
    private static void SchedulePermissionCheck()
    {
        if (s_tap == 0 || s_permissionCheckPending) return;
        s_permissionCheckPending = true;
        var generation = s_tapGeneration;
        _ = Task.Run(() =>
        {
            var access = IOHIDCheckAccess(0 /* kIOHIDRequestTypePostEvent */);
            try
            {
                CefThread.PostUi(() =>
                {
                    if (generation != s_tapGeneration) return;
                    s_permissionCheckPending = false;
                    if (s_tap == 0 || access == 0 /* kIOHIDAccessTypeGranted */) return;
                    Console.Error.WriteLine($"[Oriel] Accessibility permission unavailable while media key event tap is running, stopping tap. IOHID access={access}");
                    StopMediaTap();
                });
            }
            // CEF is shutting down.
            catch (InvalidOperationException) { }
        });
    }

    private static nint NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return Send(Class("NSString"u8), Sel("stringWithUTF8String:"u8), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
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

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id, nint target, uint options, nint* hotKey);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(nint hotKey);
    [DllImport(Carbon)] private static extern nint GetApplicationEventTarget();
    [DllImport(Carbon)] private static extern int InstallEventHandler(nint target, delegate* unmanaged<nint, nint, nint, int> handler, nuint numTypes, EventTypeSpec* types, nint userData, nint* handlerRef);
    [DllImport(Carbon)] private static extern int RemoveEventHandler(nint handlerRef);
    [DllImport(Carbon)] private static extern int GetEventParameter(nint carbonEvent, uint name, uint desiredType, uint* actualType, nuint bufferSize, nuint* actualSize, void* data);
    [DllImport(Carbon)] private static extern nint TISCopyCurrentKeyboardLayoutInputSource();
    [DllImport(Carbon)] private static extern nint TISGetInputSourceProperty(nint source, nint key);
    [DllImport(Carbon)] private static extern byte LMGetKbdLast();
    [DllImport(Carbon)] private static extern int UCKeyTranslate(nint layout, ushort keyCode, ushort keyAction, uint modifierKeyState, uint keyboardType, uint options, uint* deadKeyState, nuint maxLength, nuint* actualLength, char* unicode);
    [DllImport(CoreFoundation)] private static extern nint CFBundleGetMainBundle();
    [DllImport(CoreFoundation)] private static extern void CFBundleGetPackageInfo(nint bundle, uint* packageType, uint* creator);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetBytePtr(nint data);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint cf);
    [DllImport(CoreFoundation)] private static extern nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);
    [DllImport(CoreFoundation)] private static extern void CFMachPortInvalidate(nint port);
    [DllImport(CoreFoundation)] private static extern nint CFRunLoopGetCurrent();
    [DllImport(CoreFoundation)] private static extern void CFRunLoopAddSource(nint loop, nint source, nint mode);
    [DllImport(CoreFoundation)] private static extern void CFRunLoopRemoveSource(nint loop, nint source, nint mode);
    [DllImport(CoreGraphics)] private static extern nint CGEventTapCreate(uint tap, uint place, uint options, ulong eventsOfInterest, delegate* unmanaged<nint, uint, nint, nint, nint> callback, nint userInfo);
    [DllImport(CoreGraphics)] private static extern void CGEventTapEnable(nint tap, byte enable);
    [DllImport(ApplicationServices)] private static extern byte AXIsProcessTrusted();
    [DllImport(IOKit)] private static extern uint IOHIDCheckAccess(uint requestType);
}
