using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Oriel;
using Oriel.Cef;
using Oriel.Cef.Platform;

/// <summary>globalShortcut on macOS: key presses posted as CGEvents at the HID level and read back through the callback,
/// a second process (Python with ctypes) holding the same Carbon hot key, and media keys run through the event tap's
/// callback. Posting events needs the Accessibility permission for the terminal running the test.</summary>
[SupportedOSPlatform("macos")]
static class MacGlobalShortcutTest
{
    private const ushort KeyF9 = 0x65, KeyF10 = 0x6D, KeyF11 = 0x67; // kVK_F9, kVK_F10, kVK_F11
    private const uint CarbonMods = 0x100 | 0x200 | 0x800 | 0x1000; // cmdKey | shiftKey | optionKey | controlKey
    private const ulong CgMods = 0x20000 | 0x40000 | 0x80000 | 0x100000; // kCGEventFlagMask Shift, Control, Alternate, Command
    // A keyboard sets kCGEventFlagMaskSecondaryFn on every F-key press; Carbon matches no F-key hot key without it.
    private const ulong Fn = 0x800000;
    private const string Combo = "Cmd+Ctrl+Alt+Shift";

    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var canPost = IOHIDCheckAccess(0 /* kIOHIDRequestTypePostEvent */) == 0;
        Check(check, "the test may post key events (Accessibility permission)", canPost);

        var fired = 0;
        Check(check, "Register takes a free shortcut", GlobalShortcut.Register($"{Combo}+F9", () => fired++) && GlobalShortcut.IsRegistered($"{Combo}+F9"));
        Check(check, "the same combination written differently is the same shortcut", !GlobalShortcut.Register("Shift+Alt+Ctrl+Command+F9", () => { }));
        Press(KeyF9, CgMods | Fn);
        Check(check, "a posted key press runs the callback", Eventually(() => fired == 1, step), fired);
        var beforeBare = fired;
        Press(KeyF9, Fn);
        await Task.Delay(300);
        Check(check, "the bare key does not", fired == beforeBare, $"{beforeBare} -> {fired}");

        GlobalShortcut.Unregister($"{Combo}+F9");
        Press(KeyF9, CgMods | Fn);
        await Task.Delay(300);
        Check(check, "a released shortcut no longer runs the callback", !GlobalShortcut.IsRegistered($"{Combo}+F9") && fired == 1, fired);
        // A Carbon hot key Unregister left behind would fire alongside the new one.
        var again = 0;
        GlobalShortcut.Register($"{Combo}+F9", () => again++);
        Press(KeyF9, CgMods | Fn);
        Eventually(() => again > 0, step);
        await Task.Delay(300);
        Check(check, "registering it again runs the new callback once per press: Unregister released the hot key", again == 1 && fired == 1, $"{again} {fired}");
        GlobalShortcut.Unregister($"{Combo}+F9");

        // Carbon lets every process register the same combination and sends each press to all of them, so Electron's
        // register() is true for a shortcut another app holds.
        using (var holder = Holder(KeyF10))
        {
            Check(check, "the helper holds Cmd+Ctrl+Alt+Shift+F10", holder?.Ready == true, holder?.Ready);
            fired = 0;
            Check(check, "Register is true for a combination another process holds, as Carbon shares hot keys",
                GlobalShortcut.Register($"{Combo}+F10", () => fired++) && GlobalShortcut.IsRegistered($"{Combo}+F10"));
            Press(KeyF10, CgMods | Fn);
            Check(check, "a press then reaches Oriel's callback too", Eventually(() => fired == 1, step), fired);
        }
        GlobalShortcut.UnregisterAll();

        var presses = new List<string>();
        Check(check, "RegisterAll takes each", GlobalShortcut.RegisterAll([$"{Combo}+F10", $"{Combo}+F11"], () => presses.Add("p")));
        Press(KeyF10, CgMods | Fn);
        Check(check, "RegisterAll: the first shortcut runs the callback", Eventually(() => presses.Count == 1, step), presses.Count);
        Press(KeyF11, CgMods | Fn);
        Check(check, "RegisterAll: the second shortcut runs the callback", Eventually(() => presses.Count == 2, step), presses.Count);
        GlobalShortcut.SetSuspended(true);
        Press(KeyF11, CgMods | Fn);
        await Task.Delay(300);
        Check(check, "SetSuspended(true) releases the hot keys but keeps them registered",
            GlobalShortcut.IsSuspended() && GlobalShortcut.IsRegistered($"{Combo}+F11") && presses.Count == 2, presses.Count);
        GlobalShortcut.SetSuspended(false);
        Press(KeyF11, CgMods | Fn);
        Check(check, "SetSuspended(false) takes them again", !GlobalShortcut.IsSuspended() && Eventually(() => presses.Count == 3, step), presses.Count);
        GlobalShortcut.UnregisterAll();
        Press(KeyF10, CgMods | Fn);
        Press(KeyF11, CgMods | Fn);
        await Task.Delay(300);
        Check(check, "UnregisterAll lets them all go", presses.Count == 3 && !GlobalShortcut.IsRegistered($"{Combo}+F10"), presses.Count);

        // Chromium's Mac listener reports the registered accelerator itself, so an AltGr shortcut fires (unlike J5).
        fired = 0;
        Check(check, "AltGr+F9 registers the bare F9", GlobalShortcut.Register("AltGr+F9", () => fired++) && !GlobalShortcut.Register("F9", () => { }));
        Press(KeyF9, Fn);
        Check(check, "pressing F9 runs the AltGr callback", Eventually(() => fired == 1, step), fired);
        GlobalShortcut.UnregisterAll();

        // Media keys need the app to be a trusted accessibility client, as Electron checks.
        var trusted = AXIsProcessTrusted() != 0;
        var media = 0;
        var registered = GlobalShortcut.Register("MediaPlayPause", () => media++);
        Check(check, "MediaPlayPause registers only for a trusted accessibility client", registered == trusted, $"trusted={trusted}");
        if (registered)
        {
            // macOS hands no posted NX_SYSDEFINED event to any event tap, so the key goes straight to the tap's callback.
            Check(check, "registering a media key installs an enabled event tap", CefThread.InvokeOnUi(() => MacHotKeys.IsMediaTapEnabled));
            var play = CefThread.InvokeOnUi(() => MacHotKeys.TapSwallows(MediaKeyEvent(16 /* NX_KEYTYPE_PLAY */)));
            Check(check, "the tap swallows a play/pause key down and runs the callback", play && media == 1, $"{play} {media}");
            var next = CefThread.InvokeOnUi(() => MacHotKeys.TapSwallows(MediaKeyEvent(17 /* NX_KEYTYPE_NEXT */)));
            Check(check, "it passes on a media key nothing registered", !next && media == 1, $"{next} {media}");
        }
        GlobalShortcut.UnregisterAll();
        Check(check, "unregistering the last media key removes the tap", !CefThread.InvokeOnUi(() => MacHotKeys.IsMediaTapEnabled));
    }

    private static void Check(MessageBoxTest.CheckFn check, string name, bool pass, object? detail = null) => check("macOS globalShortcut: " + name, pass, detail);

    private static bool Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    private static void Press(ushort keyCode, ulong flags)
    {
        foreach (var down in (bool[])[true, false])
        {
            var e = CGEventCreateKeyboardEvent(0, keyCode, down ? (byte)1 : (byte)0);
            CGEventSetFlags(e, flags);
            CGEventPost(0 /* kCGHIDEventTap */, e);
            CFRelease(e);
        }
    }

    // A media key down: NSEvent's system-defined event (subtype 8) as its CGEvent, kept alive by the autoreleased NSEvent
    // until the UI task ends.
    private unsafe static nint MediaKeyEvent(int keyType)
    {
        const int down = 0xA;
        var e = ((delegate* unmanaged<nint, nint, nuint, double, double, nuint, double, nint, nint, short, nint, nint, nint>)s_msgSend)(
            objc_getClass("NSEvent"), sel_registerName("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
            14, 0, 0, down << 8, 0, 0, 0, 8, (keyType << 16) | (down << 8), -1);
        return ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(e, sel_registerName("CGEvent"));
    }

    private const string HolderScript = """
        import ctypes, sys
        c = ctypes.CDLL('/System/Library/Frameworks/Carbon.framework/Carbon')
        class HotKeyID(ctypes.Structure):
            _fields_ = [('signature', ctypes.c_uint32), ('id', ctypes.c_uint32)]
        c.GetApplicationEventTarget.restype = ctypes.c_void_p
        c.RegisterEventHotKey.argtypes = [ctypes.c_uint32, ctypes.c_uint32, HotKeyID, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p)]
        ref = ctypes.c_void_p()
        status = c.RegisterEventHotKey(int(sys.argv[1]), int(sys.argv[2]), HotKeyID(0x4F524C54, 1), c.GetApplicationEventTarget(), 0, ctypes.byref(ref))
        print('held' if status == 0 else 'refused', flush=True)
        if status == 0: sys.stdin.read()
        """;

    private sealed class HeldKey(Process process, bool ready) : IDisposable
    {
        public bool Ready { get; } = ready;

        public void Dispose()
        {
            process.StandardInput.Close();
            process.WaitForExit(5000);
            process.Dispose();
        }
    }

    // Holds the hot key until disposed.
    private static HeldKey? Holder(ushort keyCode)
    {
        var psi = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var arg in (string[])["-I", "-c", HolderScript, keyCode.ToString(), CarbonMods.ToString()]) psi.ArgumentList.Add(arg);
        var process = Process.Start(psi);
        return process is null ? null : new HeldKey(process, process.StandardOutput.ReadLine() == "held");
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    [DllImport(CoreGraphics)] private static extern nint CGEventCreateKeyboardEvent(nint source, ushort keyCode, byte keyDown);
    [DllImport(CoreGraphics)] private static extern void CGEventSetFlags(nint e, ulong flags);
    [DllImport(CoreGraphics)] private static extern void CGEventPost(uint tap, nint e);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint cf);
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] private static extern byte AXIsProcessTrusted();
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")] private static extern uint IOHIDCheckAccess(uint requestType);
    [DllImport(ObjC)] private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(ObjC)] private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
}
