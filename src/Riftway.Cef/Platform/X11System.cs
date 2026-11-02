using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>Chromium's X11 pieces behind <c>globalShortcut</c>, <c>powerMonitor</c> idle time and the power save
/// blocker's screen saver suspension, on an XCB connection of Riftway's own (Chromium uses its own connection too).</summary>
/// <remarks>XCB rather than Xlib: errors come back per request instead of through Xlib's process-wide error handler,
/// which GTK also uses. A thread of its own reads the connection's events for the key grabs.</remarks>
[SupportedOSPlatform("linux")]
public static unsafe partial class X11System
{
    private const string LibXcb = "libxcb.so.1";
    private const string LibC = "libc.so.6";
    private const int KeyPress = 2;
    // MIT-SCREEN-SAVER minor opcodes.
    private const byte SaverQueryVersion = 0, SaverQueryInfo = 1, SaverSuspend = 5;

    private static readonly Lock s_gate = new();
    private static nint s_connection;
    private static uint s_root;
    private static bool s_tried;
    private static nint s_saverExtension;
    private static bool? s_saverAvailable;

    /// <summary>A grabbed key was pressed: its keycode and the modifier state, on the X event thread.</summary>
    public static event Action<byte, ushort>? KeyPressed;

    /// <summary>Grabs <paramref name="keysym"/> with <paramref name="modifiers"/> on the root window, as Chromium's
    /// <c>XGlobalShortcutListener::RegisterAccelerator</c>: once per lock-modifier variant, undoing them all if any fails
    /// (another client holds it). The grabbed keycode, or null on failure or without an X display.</summary>
    public static byte? GrabKey(uint keysym, ushort modifiers)
    {
        var connection = Connection();
        if (connection == 0) return null;
        var keycode = KeycodeOf(connection, keysym);
        var cookies = stackalloc uint[X11Keys.LockMasks.Length];
        for (var i = 0; i < X11Keys.LockMasks.Length; i++)
            // owner_events false, GrabModeAsync (1) for pointer and keyboard.
            cookies[i] = xcb_grab_key_checked(connection, 0, s_root, (ushort)(modifiers | X11Keys.LockMasks[i]), keycode, 1, 1);
        _ = xcb_flush(connection);
        var failed = false;
        for (var i = 0; i < X11Keys.LockMasks.Length; i++)
        {
            var error = xcb_request_check(connection, cookies[i]);
            if (error == null) continue;
            failed = true;
            free(error);
        }
        if (!failed) return keycode;
        UngrabKey(keycode, modifiers);
        return null;
    }

    /// <summary>Releases every lock-modifier variant of a grab, as <c>UnregisterAccelerator</c>.</summary>
    public static void UngrabKey(byte keycode, ushort modifiers)
    {
        var connection = Connection();
        if (connection == 0) return;
        foreach (var mask in X11Keys.LockMasks) _ = xcb_ungrab_key(connection, keycode, s_root, (ushort)(modifiers | mask));
        _ = xcb_flush(connection);
    }

    /// <summary>Seconds since the last input, from the screen saver extension's <c>QueryInfo</c>; 0 when it cannot be
    /// read, as Chromium's <c>IdleQueryX11</c>.</summary>
    public static int IdleSeconds() => QueryInfo() is { } info ? (int)(info.MsSinceUserInput / 1000) : 0;

    /// <summary>Chromium's <c>IsXScreensaverActive</c>: the MIT screen saver is on or cycling, or xscreensaver's
    /// <c>_SCREENSAVER_STATUS</c> on the root window starts with the <c>LOCK</c> atom.</summary>
    public static bool ScreenSaverActive()
    {
        var connection = Connection();
        if (connection == 0) return false;
        return X11Keys.MitScreenSaverActive(QueryInfo()?.State) || ScreenSaverStatusIsLock(connection);
    }

    /// <summary>Chromium's <c>SuspendX11ScreenSaver</c>: false without screen saver extension 1.1, else sends
    /// <c>Suspend</c>. The server counts suspensions per client and drops them when the client goes.</summary>
    public static bool SuspendScreenSaver(bool suspend)
    {
        var connection = Connection();
        if (connection == 0 || !SaverAvailable(connection)) return false;
        var request = stackalloc uint[2];
        request[0] = 0;
        request[1] = suspend ? 1u : 0u;
        _ = Send(connection, SaverSuspend, request, 8, isVoid: true);
        _ = xcb_flush(connection);
        return true;
    }

    private readonly record struct SaverInfo(byte State, uint MsSinceUserInput);

    private static SaverInfo? QueryInfo()
    {
        var connection = Connection();
        if (connection == 0 || SaverExtension(connection) is not { Present: true }) return null;
        var request = stackalloc uint[2];
        request[0] = 0;
        request[1] = s_root;
        var reply = (byte*)Reply(connection, Send(connection, SaverQueryInfo, request, 8, isVoid: false));
        if (reply == null) return null;
        // xcb_screensaver_query_info_reply_t: state at 1, ms_since_user_input at 16.
        var info = new SaverInfo(reply[1], *(uint*)(reply + 16));
        free(reply);
        return info;
    }

    private static bool SaverAvailable(nint connection)
    {
        lock (s_gate)
        {
            if (s_saverAvailable is { } known) return known;
            var available = false;
            if (SaverExtension(connection) is { Present: true })
            {
                // client_major_version 1, client_minor_version 1, as Chromium's generated bindings ask.
                var request = stackalloc uint[2];
                request[0] = 0;
                request[1] = 1 | (1 << 8);
                var reply = (byte*)Reply(connection, Send(connection, SaverQueryVersion, request, 8, isVoid: false));
                if (reply != null)
                {
                    var major = *(ushort*)(reply + 8);
                    var minor = *(ushort*)(reply + 10);
                    available = major > 1 || (major == 1 && minor >= 1);
                    free(reply);
                }
            }
            s_saverAvailable = available;
            return available;
        }
    }

    private readonly record struct Extension(bool Present);

    private static Extension? SaverExtension(nint connection)
    {
        var data = (byte*)xcb_get_extension_data(connection, SaverExtensionId());
        // xcb_query_extension_reply_t: present at 8.
        return data == null ? null : new Extension(data[8] != 0);
    }

    private static nint SaverExtensionId()
    {
        lock (s_gate)
        {
            if (s_saverExtension != 0) return s_saverExtension;
            // xcb_extension_t { const char *name; int global_id; }; XCB assigns the id on first use. Lives for the process.
            var name = Marshal.StringToHGlobalAnsi("MIT-SCREEN-SAVER");
            var ext = (nint*)NativeMemory.AllocZeroed(16);
            ext[0] = name;
            s_saverExtension = (nint)ext;
            return s_saverExtension;
        }
    }

    // xcb_send_request needs two spare iovecs before the request's own, which XCB fills with the header.
    private static uint Send(nint connection, byte minor, void* body, int length, bool isVoid)
    {
        var parts = stackalloc nint[8];
        parts[4] = (nint)body;
        parts[5] = length;
        parts[6] = 0;
        parts[7] = -length & 3;
        // xcb_protocol_request_t { size_t count; xcb_extension_t *ext; uint8_t opcode; uint8_t isvoid; }.
        var protocol = stackalloc nint[3];
        protocol[0] = 2;
        protocol[1] = SaverExtensionId();
        protocol[2] = minor | ((isVoid ? 1 : 0) << 8);
        // XCB_REQUEST_CHECKED.
        return xcb_send_request(connection, 1, parts + 4, protocol);
    }

    private static void* Reply(nint connection, uint sequence)
    {
        void* error = null;
        var reply = xcb_wait_for_reply(connection, sequence, &error);
        if (error != null) free(error);
        return reply;
    }

    private static bool ScreenSaverStatusIsLock(nint connection)
    {
        var status = Atom(connection, "_SCREENSAVER_STATUS"u8);
        var locked = Atom(connection, "LOCK"u8);
        if (status == 0) return false;
        // Any type, as Chromium's PropertyCache asks; xscreensaver stores the state atom first in an INTEGER list.
        var reply = (byte*)xcb_get_property_reply(connection, xcb_get_property(connection, 0, s_root, status, 0, 0, 1), null);
        if (reply == null) return false;
        // xcb_get_property_reply_t: format at 1, value_len at 16, value at 32.
        var result = reply[1] == 32 && *(uint*)(reply + 16) >= 1 && *(uint*)(reply + 32) == locked;
        free(reply);
        return result;
    }

    private static uint Atom(nint connection, ReadOnlySpan<byte> name)
    {
        void* reply;
        fixed (byte* p = name) reply = xcb_intern_atom_reply(connection, xcb_intern_atom(connection, 0, (ushort)name.Length, p), null);
        if (reply == null) return 0;
        // xcb_intern_atom_reply_t: atom at 8.
        var atom = *(uint*)((byte*)reply + 8);
        free(reply);
        return atom;
    }

    // Chromium's XkbKeyboardState::KeysymToKeycode: the first keycode carrying the keysym in any column, else 0.
    private static byte KeycodeOf(nint connection, uint keysym)
    {
        var setup = (byte*)xcb_get_setup(connection);
        // xcb_setup_t: min_keycode at 34, max_keycode at 35.
        byte min = setup[34], max = setup[35];
        var reply = (byte*)xcb_get_keyboard_mapping_reply(connection, xcb_get_keyboard_mapping(connection, min, (byte)(max - min + 1)), null);
        if (reply == null) return 0;
        // xcb_get_keyboard_mapping_reply_t: keysyms_per_keycode at 1, length (keysym count) at 4, keysyms at 32.
        var keycode = X11Keys.KeycodeOf(keysym, new ReadOnlySpan<uint>(reply + 32, (int)*(uint*)(reply + 4)), reply[1], min);
        free(reply);
        return keycode;
    }

    private static nint Connection()
    {
        lock (s_gate)
        {
            if (s_tried) return s_connection;
            s_tried = true;
            try
            {
                int screen;
                var connection = xcb_connect(null, &screen);
                if (connection == 0) return 0;
                if (xcb_connection_has_error(connection) != 0)
                {
                    xcb_disconnect(connection);
                    return 0;
                }
                var iterator = xcb_setup_roots_iterator(xcb_get_setup(connection));
                for (var i = 0; i < screen && iterator.Rem > 0; i++) xcb_screen_next(&iterator);
                if (iterator.Data == null)
                {
                    xcb_disconnect(connection);
                    return 0;
                }
                // xcb_screen_t starts with its root window.
                s_root = *(uint*)iterator.Data;
                s_connection = connection;
                new Thread(() => Events(connection)) { IsBackground = true, Name = "Riftway X11" }.Start();
                return connection;
            }
            catch (DllNotFoundException) { return 0; }
        }
    }

    private static void Events(nint connection)
    {
        byte* e;
        while ((e = (byte*)xcb_wait_for_event(connection)) != null)
        {
            try
            {
                // xcb_key_press_event_t: detail (the keycode) at 1, state at 28; the top bit of the type marks SendEvent.
                if ((e[0] & 0x7F) == KeyPress) KeyPressed?.Invoke(e[1], *(ushort*)(e + 28));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Console.Error.WriteLine($"Riftway X11: {ex}"); }
            finally { free(e); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenIterator
    {
        public void* Data;
        public int Rem;
        public int Index;
    }

    [LibraryImport(LibC)] private static partial void free(void* p);
    [LibraryImport(LibXcb)] private static partial nint xcb_connect(byte* display, int* screen);
    [LibraryImport(LibXcb)] private static partial int xcb_connection_has_error(nint connection);
    [LibraryImport(LibXcb)] private static partial void xcb_disconnect(nint connection);
    [LibraryImport(LibXcb)] private static partial void* xcb_get_setup(nint connection);
    [LibraryImport(LibXcb)] private static partial ScreenIterator xcb_setup_roots_iterator(void* setup);
    [LibraryImport(LibXcb)] private static partial void xcb_screen_next(ScreenIterator* iterator);
    [LibraryImport(LibXcb)] private static partial int xcb_flush(nint connection);
    [LibraryImport(LibXcb)] private static partial void* xcb_wait_for_event(nint connection);
    [LibraryImport(LibXcb)] private static partial void* xcb_request_check(nint connection, uint cookie);
    [LibraryImport(LibXcb)] private static partial uint xcb_grab_key_checked(nint connection, byte ownerEvents, uint window, ushort modifiers, byte key, byte pointerMode, byte keyboardMode);
    [LibraryImport(LibXcb)] private static partial uint xcb_ungrab_key(nint connection, byte key, uint window, ushort modifiers);
    [LibraryImport(LibXcb)] private static partial uint xcb_get_keyboard_mapping(nint connection, byte first, byte count);
    [LibraryImport(LibXcb)] private static partial void* xcb_get_keyboard_mapping_reply(nint connection, uint cookie, void** error);
    [LibraryImport(LibXcb)] private static partial uint xcb_intern_atom(nint connection, byte onlyIfExists, ushort length, byte* name);
    [LibraryImport(LibXcb)] private static partial void* xcb_intern_atom_reply(nint connection, uint cookie, void** error);
    [LibraryImport(LibXcb)] private static partial uint xcb_get_property(nint connection, byte delete, uint window, uint property, uint type, uint offset, uint length);
    [LibraryImport(LibXcb)] private static partial void* xcb_get_property_reply(nint connection, uint cookie, void** error);
    [LibraryImport(LibXcb)] private static partial void* xcb_get_extension_data(nint connection, nint extension);
    [LibraryImport(LibXcb)] private static partial uint xcb_send_request(nint connection, int flags, nint* vector, nint* request);
    [LibraryImport(LibXcb)] private static partial void* xcb_wait_for_reply(nint connection, uint sequence, void** error);
}

/// <summary>Chromium's X11 key and modifier tables for global shortcuts.</summary>
public static class X11Keys
{
    public const ushort ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64, Mod5Mask = 128;

    /// <summary><c>kModifiersMasks</c>: every grab is repeated with each combination of Num Lock (Mod2), Caps Lock and
    /// Scroll Lock (Mod5), since <c>XGrabKey</c> matches modifiers exactly.</summary>
    public static readonly ushort[] LockMasks =
        [0, Mod2Mask, LockMask, Mod5Mask, Mod2Mask | LockMask, Mod2Mask | Mod5Mask, LockMask | Mod5Mask, Mod2Mask | LockMask | Mod5Mask];

    /// <summary><c>GetNativeModifiers</c>: Shift, Control and Alt as Mod1. Chromium drops Super; Riftway grabs it as Mod4
    /// (DRIFT J3).</summary>
    public static ushort Modifiers(bool alt, bool control, bool shift, bool super)
        => (ushort)((shift ? ShiftMask : 0) | (control ? ControlMask : 0) | (alt ? Mod1Mask : 0) | (super ? Mod4Mask : 0));

    /// <summary>The modifiers of a key event that pick the shortcut; the lock modifiers are left out.</summary>
    public static ushort Relevant(ushort state) => (ushort)(state & (ShiftMask | ControlMask | Mod1Mask | Mod4Mask));

    /// <summary>The MIT screen saver's <c>On</c> (1) and <c>Cycle</c> (2) count as active; <c>Off</c>, <c>Disabled</c>
    /// and no reply do not.</summary>
    public static bool MitScreenSaverActive(byte? state) => state is 1 or 2;

    /// <summary>The first keycode from <paramref name="min"/> whose keysyms (<paramref name="perKeycode"/> each) include
    /// <paramref name="keysym"/>; 0 (X's <c>AnyKey</c>) when none does, which Chromium then grabs as is.</summary>
    public static byte KeycodeOf(uint keysym, ReadOnlySpan<uint> keysyms, int perKeycode, byte min)
    {
        if (perKeycode <= 0) return 0;
        for (var i = 0; i + perKeycode <= keysyms.Length; i += perKeycode)
            if (keysyms.Slice(i, perKeycode).Contains(keysym)) return (byte)(min + i / perKeycode);
        return 0;
    }

    /// <summary><c>XKeysymForWindowsKeyCode(key_code, false)</c> from <c>keyboard_code_conversion_x.cc</c>: the unshifted
    /// keysym for a Windows virtual-key code, 0 for one it does not know.</summary>
    public static uint KeysymOf(int vk) => vk switch
    {
        >= 0x60 and <= 0x69 => 0xFFB0u + (uint)(vk - 0x60), // VKEY_NUMPAD0-9 → XK_KP_0-9
        0x6A => 0xFFAA, // VKEY_MULTIPLY → XK_KP_Multiply
        0x6B => 0xFFAB, // VKEY_ADD → XK_KP_Add
        0x6D => 0xFFAD, // VKEY_SUBTRACT → XK_KP_Subtract
        0x6E => 0xFFAE, // VKEY_DECIMAL → XK_KP_Decimal
        0x6F => 0xFFAF, // VKEY_DIVIDE → XK_KP_Divide
        0x08 => 0xFF08, // XK_BackSpace
        0x09 => 0xFF09, // XK_Tab
        0x0C => 0xFF0B, // VKEY_CLEAR → XK_Clear
        0x0D => 0xFF0D, // XK_Return
        0x10 or 0xA0 => 0xFFE1, // VKEY_SHIFT, VKEY_LSHIFT → XK_Shift_L
        0xA1 => 0xFFE2, // XK_Shift_R
        0x11 => 0xFFE3, // VKEY_CONTROL → XK_Control_L
        0x12 => 0xFFE9, // VKEY_MENU → XK_Alt_L
        0x5D => 0xFF67, // VKEY_APPS → XK_Menu
        0xE1 => 0xFE03, // VKEY_ALTGR → XK_ISO_Level3_Shift
        0xE6 => 0xFF20, // VKEY_COMPOSE → XK_Multi_key
        0x13 => 0xFF13, // XK_Pause
        0x14 => 0xFFE5, // VKEY_CAPITAL → XK_Caps_Lock
        0x15 => 0xFF2D, // VKEY_KANA → XK_Kana_Lock
        0x19 => 0xFF34, // VKEY_HANJA → XK_Hangul_Hanja
        0x1C => 0xFF23, // VKEY_CONVERT → XK_Henkan
        0x1D => 0xFF22, // VKEY_NONCONVERT → XK_Muhenkan
        0xF3 or 0xF4 => 0xFF2A, // VKEY_DBE_SBCSCHAR, VKEY_DBE_DBCSCHAR → XK_Zenkaku_Hankaku
        0x1B => 0xFF1B, // XK_Escape
        0x20 => 0x0020, // XK_space
        0x21 => 0xFF55, // VKEY_PRIOR → XK_Page_Up
        0x22 => 0xFF56, // VKEY_NEXT → XK_Page_Down
        0x23 => 0xFF57, // XK_End
        0x24 => 0xFF50, // XK_Home
        0x25 => 0xFF51, // XK_Left
        0x26 => 0xFF52, // XK_Up
        0x27 => 0xFF53, // XK_Right
        0x28 => 0xFF54, // XK_Down
        0x29 => 0xFF60, // XK_Select
        0x2A => 0xFF61, // XK_Print
        0x2B => 0xFF62, // XK_Execute
        0x2D => 0xFF63, // XK_Insert
        0x2E => 0xFFFF, // XK_Delete
        0x2F => 0xFF6A, // XK_Help
        >= 0x30 and <= 0x39 => (uint)vk, // XK_0-9
        >= 0x41 and <= 0x5A => (uint)vk + 0x20, // XK_a-z
        0x5B => 0xFFEB, // VKEY_LWIN → XK_Super_L
        0x5C => 0xFFEC, // VKEY_RWIN → XK_Super_R
        0x90 => 0xFF7F, // XK_Num_Lock
        0x91 => 0xFF14, // VKEY_SCROLL → XK_Scroll_Lock
        0xBA => 0x003B, // VKEY_OEM_1 → XK_semicolon
        0xBB => 0x003D, // VKEY_OEM_PLUS → XK_equal
        0xBC => 0x002C, // XK_comma
        0xBD => 0x002D, // XK_minus
        0xBE => 0x002E, // XK_period
        0xBF => 0x002F, // VKEY_OEM_2 → XK_slash
        0xC0 => 0x0060, // VKEY_OEM_3 → XK_quoteleft
        0xDB => 0x005B, // VKEY_OEM_4 → XK_bracketleft
        0xDC => 0x005C, // VKEY_OEM_5 → XK_backslash
        0xDD => 0x005D, // VKEY_OEM_6 → XK_bracketright
        0xDE => 0x0027, // VKEY_OEM_7 → XK_quoteright
        0xDF => 0xFE11, // VKEY_OEM_8 → XK_ISO_Level5_Shift
        0xE2 => 0x00BB, // VKEY_OEM_102 → XK_guillemotright
        >= 0x70 and <= 0x87 => 0xFFBEu + (uint)(vk - 0x70), // XK_F1-F24
        0xA6 => 0x1008FF26, // VKEY_BROWSER_BACK → XF86XK_Back
        0xA7 => 0x1008FF27, // XF86XK_Forward
        0xA8 => 0x1008FF73, // VKEY_BROWSER_REFRESH → XF86XK_Reload
        0xA9 => 0x1008FF28, // XF86XK_Stop
        0xAA => 0x1008FF1B, // XF86XK_Search
        0xAB => 0x1008FF30, // XF86XK_Favorites
        0xAC => 0x1008FF18, // XF86XK_HomePage
        0xAD => 0x1008FF12, // XF86XK_AudioMute
        0xAE => 0x1008FF11, // XF86XK_AudioLowerVolume
        0xAF => 0x1008FF13, // XF86XK_AudioRaiseVolume
        0xB0 => 0x1008FF17, // XF86XK_AudioNext
        0xB1 => 0x1008FF16, // XF86XK_AudioPrev
        0xB2 => 0x1008FF15, // XF86XK_AudioStop
        0xB3 => 0x1008FF14, // VKEY_MEDIA_PLAY_PAUSE → XF86XK_AudioPlay
        0xB4 => 0x1008FF19, // VKEY_MEDIA_LAUNCH_MAIL → XF86XK_Mail
        0xB6 => 0x1008FF4A, // VKEY_MEDIA_LAUNCH_APP1 → XF86XK_LaunchA
        0xB7 => 0x1008FF4B, // VKEY_MEDIA_LAUNCH_APP2 → XF86XK_LaunchB
        0x97 => 0x1008FF95, // VKEY_WLAN → XF86XK_WLAN
        0x98 => 0x1008FF2A, // VKEY_POWER → XF86XK_PowerOff
        0xD8 => 0x1008FF03, // VKEY_BRIGHTNESS_DOWN → XF86XK_MonBrightnessDown
        0xD9 => 0x1008FF02, // VKEY_BRIGHTNESS_UP → XF86XK_MonBrightnessUp
        0xDA => 0x1008FF06, // VKEY_KBD_BRIGHTNESS_DOWN → XF86XK_KbdBrightnessDown
        0xE8 => 0x1008FF05, // VKEY_KBD_BRIGHTNESS_UP → XF86XK_KbdBrightnessUp
        _ => 0,
    };
}
