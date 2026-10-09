using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Oriel;

/// <summary>globalShortcut on macOS: key presses posted as CGEvents at the HID level, and a second process (Python with
/// ctypes) that registers Carbon hot keys itself to see which combinations Oriel holds. Posting events needs the
/// Accessibility permission for the terminal running the test.</summary>
[SupportedOSPlatform("macos")]
static class MacGlobalShortcutTest
{
    private const ushort KeyF9 = 0x65, KeyF10 = 0x6D, KeyF11 = 0x67; // kVK_F9, kVK_F10, kVK_F11
    private const uint CarbonMods = 0x100 | 0x200 | 0x800 | 0x1000; // cmdKey | shiftKey | optionKey | controlKey
    private const ulong CgMods = 0x20000 | 0x40000 | 0x80000 | 0x100000; // kCGEventFlagMask Shift, Control, Alternate, Command
    private const string Combo = "Cmd+Ctrl+Alt+Shift";

    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var canPost = IOHIDCheckAccess(0 /* kIOHIDRequestTypePostEvent */) == 0;
        Check(check, "the test may post key events (Accessibility permission)", canPost);

        var fired = 0;
        Check(check, "Register takes a free shortcut", GlobalShortcut.Register($"{Combo}+F9", () => fired++) && GlobalShortcut.IsRegistered($"{Combo}+F9"));
        Check(check, "the same combination written differently is the same shortcut", !GlobalShortcut.Register("Shift+Alt+Ctrl+Command+F9", () => { }));
        Check(check, "another process cannot take it (Carbon refuses)", HolderRegisters(KeyF9) == "refused", HolderRegisters(KeyF9));
        Press(KeyF9, CgMods);
        Check(check, "a posted key press runs the callback", Eventually(() => fired == 1, step), fired);
        Press(KeyF9, 0);
        await Task.Delay(300);
        Check(check, "the bare key does not", fired == 1, fired);

        GlobalShortcut.Unregister($"{Combo}+F9");
        Check(check, "Unregister lets it go: another process can take it", !GlobalShortcut.IsRegistered($"{Combo}+F9") && HolderRegisters(KeyF9) == "held", HolderRegisters(KeyF9));
        Press(KeyF9, CgMods);
        await Task.Delay(300);
        Check(check, "a released shortcut no longer runs the callback", fired == 1, fired);

        // A combination another process holds: Carbon's RegisterEventHotKey fails, so Register is false.
        using (var holder = Holder(KeyF10))
        {
            Check(check, "the helper holds Cmd+Ctrl+Alt+Shift+F10", holder?.Ready == true, holder?.Ready);
            Check(check, "Register is false for a combination another process holds", !GlobalShortcut.Register($"{Combo}+F10", () => { }) && !GlobalShortcut.IsRegistered($"{Combo}+F10"));
        }
        Check(check, "once the other process lets go, Register takes it", GlobalShortcut.Register($"{Combo}+F10", () => { }));
        GlobalShortcut.UnregisterAll();

        Check(check, "RegisterAll takes each", GlobalShortcut.RegisterAll([$"{Combo}+F10", $"{Combo}+F11"], () => { }) && HolderRegisters(KeyF10) == "refused" && HolderRegisters(KeyF11) == "refused");
        GlobalShortcut.SetSuspended(true);
        Check(check, "SetSuspended(true) releases the hot keys but keeps them registered", GlobalShortcut.IsSuspended() && GlobalShortcut.IsRegistered($"{Combo}+F11") && HolderRegisters(KeyF11) == "held");
        GlobalShortcut.SetSuspended(false);
        Check(check, "SetSuspended(false) takes them again", !GlobalShortcut.IsSuspended() && HolderRegisters(KeyF11) == "refused");
        GlobalShortcut.UnregisterAll();
        Check(check, "UnregisterAll lets them all go", HolderRegisters(KeyF10) == "held" && HolderRegisters(KeyF11) == "held");

        // Chromium's Mac listener reports the registered accelerator itself, so an AltGr shortcut fires (unlike J5).
        fired = 0;
        Check(check, "AltGr+F9 registers the bare F9", GlobalShortcut.Register("AltGr+F9", () => fired++) && HolderRegisters(KeyF9, 0) == "refused" && !GlobalShortcut.Register("F9", () => { }));
        Press(KeyF9, 0);
        Check(check, "pressing F9 runs the AltGr callback", Eventually(() => fired == 1, step), fired);
        GlobalShortcut.UnregisterAll();

        // Media keys need the app to be a trusted accessibility client, as Electron checks.
        var trusted = AXIsProcessTrusted() != 0;
        var media = 0;
        var registered = GlobalShortcut.Register("MediaPlayPause", () => media++);
        Check(check, "MediaPlayPause registers only for a trusted accessibility client", registered == trusted, $"trusted={trusted}");
        if (registered)
        {
            PostMediaKey(16 /* NX_KEYTYPE_PLAY */);
            Check(check, "a posted play/pause media key runs the callback through the event tap", Eventually(() => media == 1, step), media);
        }
        GlobalShortcut.UnregisterAll();
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

    // NSEvent's system-defined media key event (subtype 8), down then up, posted as its CGEvent.
    private static void PostMediaKey(int keyType)
    {
        foreach (var state in (int[])[0xA, 0xB])
        {
            var script = "ObjC.import('AppKit'); ObjC.import('CoreGraphics'); "
                + $"const e = $.NSEvent.otherEventWithTypeLocationModifierFlagsTimestampWindowNumberContextSubtypeData1Data2(14, $.NSMakePoint(0, 0), {state << 8}, 0, 0, null, 8, {(keyType << 16) | (state << 8)}, -1); "
                + "$.CGEventPost(0, e.CGEvent);";
            Run("osascript", "-l", "JavaScript", "-e", script);
        }
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
        if status == 0 and len(sys.argv) > 3: sys.stdin.read()
        """;

    // Registers the hot key in a fresh process and lets it go at once: "held" when free, "refused" when taken.
    private static string HolderRegisters(ushort keyCode, uint mods = CarbonMods) => Run("python3", "-I", "-c", HolderScript, keyCode.ToString(), mods.ToString());

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
        foreach (var arg in (string[])["-I", "-c", HolderScript, keyCode.ToString(), CarbonMods.ToString(), "hold"]) psi.ArgumentList.Add(arg);
        var process = Process.Start(psi);
        return process is null ? null : new HeldKey(process, process.StandardOutput.ReadLine() == "held");
    }

    private static string Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : "<" + error.Trim() + ">";
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(CoreGraphics)] private static extern nint CGEventCreateKeyboardEvent(nint source, ushort keyCode, byte keyDown);
    [DllImport(CoreGraphics)] private static extern void CGEventSetFlags(nint e, ulong flags);
    [DllImport(CoreGraphics)] private static extern void CGEventPost(uint tap, nint e);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint cf);
    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")] private static extern byte AXIsProcessTrusted();
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")] private static extern uint IOHIDCheckAccess(uint requestType);
}
