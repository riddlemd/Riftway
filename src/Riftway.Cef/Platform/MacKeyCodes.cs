namespace Riftway.Cef.Platform;

/// <summary>Chromium's mapping from Windows virtual-key codes to macOS (Carbon <c>kVK_</c>) key codes: pure tables.</summary>
public static class MacKeyCodes
{
    /// <summary>What <c>MacKeyCodeForWindowsKeyCode</c> returns for a key no Mac key code stands for.</summary>
    public const int None = -1;

    // ui/events/keycodes/keyboard_code_conversion_mac.mm: the entries with a key code, and each one's
    // character_ignoring_all_modifiers. Entries Chromium marks -1 give no character either.
    private static readonly Dictionary<int, (int MacKeyCode, char Character)> s_map = new()
    {
        [0x08] = (0x33, '\b'), // VKEY_BACK → kVK_Delete, kBackspaceCharCode
        [0x09] = (0x30, '\t'), // VKEY_TAB → kVK_Tab
        [0x0A] = (0x21E4, '\u0019'), // VKEY_BACKTAB
        [0x0C] = (0x47, '\u001B'), // VKEY_CLEAR → kVK_ANSI_KeypadClear, kClearCharCode
        [0x0D] = (0x24, '\r'), // VKEY_RETURN → kVK_Return
        [0x10] = (0x38, '\0'), // VKEY_SHIFT → kVK_Shift
        [0x11] = (0x3B, '\0'), // VKEY_CONTROL → kVK_Control
        [0x12] = (0x3A, '\0'), // VKEY_MENU → kVK_Option
        [0x14] = (0x39, '\0'), // VKEY_CAPITAL → kVK_CapsLock
        [0x15] = (0x68, '\0'), // VKEY_KANA → kVK_JIS_Kana
        [0x1B] = (0x35, '\u001B'), // VKEY_ESCAPE → kVK_Escape
        [0x20] = (0x31, ' '), // VKEY_SPACE → kVK_Space
        [0x21] = (0x74, ''), // VKEY_PRIOR → kVK_PageUp, NSPageUpFunctionKey
        [0x22] = (0x79, ''), // VKEY_NEXT → kVK_PageDown
        [0x23] = (0x77, ''), // VKEY_END → kVK_End
        [0x24] = (0x73, ''), // VKEY_HOME → kVK_Home
        [0x25] = (0x7B, ''), // VKEY_LEFT → kVK_LeftArrow
        [0x26] = (0x7E, ''), // VKEY_UP → kVK_UpArrow
        [0x27] = (0x7C, ''), // VKEY_RIGHT → kVK_RightArrow
        [0x28] = (0x7D, ''), // VKEY_DOWN → kVK_DownArrow
        [0x2D] = (0x72, ''), // VKEY_INSERT → kVK_Help, NSInsertFunctionKey
        [0x2E] = (0x75, ''), // VKEY_DELETE → kVK_ForwardDelete
        [0x2F] = (0x72, '\u0005'), // VKEY_HELP → kVK_Help, kHelpCharCode
        [0x30] = (0x1D, '0'),
        [0x31] = (0x12, '1'),
        [0x32] = (0x13, '2'),
        [0x33] = (0x14, '3'),
        [0x34] = (0x15, '4'),
        [0x35] = (0x17, '5'),
        [0x36] = (0x16, '6'),
        [0x37] = (0x1A, '7'),
        [0x38] = (0x1C, '8'),
        [0x39] = (0x19, '9'),
        [0x41] = (0x00, 'a'),
        [0x42] = (0x0B, 'b'),
        [0x43] = (0x08, 'c'),
        [0x44] = (0x02, 'd'),
        [0x45] = (0x0E, 'e'),
        [0x46] = (0x03, 'f'),
        [0x47] = (0x05, 'g'),
        [0x48] = (0x04, 'h'),
        [0x49] = (0x22, 'i'),
        [0x4A] = (0x26, 'j'),
        [0x4B] = (0x28, 'k'),
        [0x4C] = (0x25, 'l'),
        [0x4D] = (0x2E, 'm'),
        [0x4E] = (0x2D, 'n'),
        [0x4F] = (0x1F, 'o'),
        [0x50] = (0x23, 'p'),
        [0x51] = (0x0C, 'q'),
        [0x52] = (0x0F, 'r'),
        [0x53] = (0x01, 's'),
        [0x54] = (0x11, 't'),
        [0x55] = (0x20, 'u'),
        [0x56] = (0x09, 'v'),
        [0x57] = (0x0D, 'w'),
        [0x58] = (0x07, 'x'),
        [0x59] = (0x10, 'y'),
        [0x5A] = (0x06, 'z'),
        [0x5B] = (0x37, '\0'), // VKEY_LWIN → kVK_Command
        [0x5C] = (0x36, '\0'), // VKEY_RWIN → kVK_RightCommand
        [0x5D] = (0x36, '\0'), // VKEY_APPS → kVK_RightCommand
        [0x60] = (0x52, '0'), // VKEY_NUMPAD0 → kVK_ANSI_Keypad0
        [0x61] = (0x53, '1'),
        [0x62] = (0x54, '2'),
        [0x63] = (0x55, '3'),
        [0x64] = (0x56, '4'),
        [0x65] = (0x57, '5'),
        [0x66] = (0x58, '6'),
        [0x67] = (0x59, '7'),
        [0x68] = (0x5B, '8'),
        [0x69] = (0x5C, '9'),
        [0x6A] = (0x43, '*'), // VKEY_MULTIPLY → kVK_ANSI_KeypadMultiply
        [0x6B] = (0x45, '+'), // VKEY_ADD → kVK_ANSI_KeypadPlus
        [0x6D] = (0x4E, '-'), // VKEY_SUBTRACT → kVK_ANSI_KeypadMinus
        [0x6E] = (0x41, '.'), // VKEY_DECIMAL → kVK_ANSI_KeypadDecimal
        [0x6F] = (0x4B, '/'), // VKEY_DIVIDE → kVK_ANSI_KeypadDivide
        [0x70] = (0x7A, ''), // VKEY_F1 → kVK_F1, NSF1FunctionKey
        [0x71] = (0x78, ''),
        [0x72] = (0x63, ''),
        [0x73] = (0x76, ''),
        [0x74] = (0x60, ''),
        [0x75] = (0x61, ''),
        [0x76] = (0x62, ''),
        [0x77] = (0x64, ''),
        [0x78] = (0x65, ''),
        [0x79] = (0x6D, ''),
        [0x7A] = (0x67, ''),
        [0x7B] = (0x6F, ''),
        [0x7C] = (0x69, ''),
        [0x7D] = (0x6B, ''),
        [0x7E] = (0x71, ''),
        [0x7F] = (0x6A, ''),
        [0x80] = (0x40, ''),
        [0x81] = (0x4F, ''),
        [0x82] = (0x50, ''),
        [0x83] = (0x5A, ''), // VKEY_F20 → kVK_F20; F21-F24 have no key code
        [0xA0] = (0x38, '\0'), // VKEY_LSHIFT → kVK_Shift
        [0xA1] = (0x38, '\0'), // VKEY_RSHIFT → kVK_Shift
        [0xA2] = (0x3B, '\0'), // VKEY_LCONTROL → kVK_Control
        [0xA3] = (0x3B, '\0'), // VKEY_RCONTROL → kVK_Control
        [0xBA] = (0x29, ';'), // VKEY_OEM_1 → kVK_ANSI_Semicolon
        [0xBB] = (0x18, '='), // VKEY_OEM_PLUS → kVK_ANSI_Equal
        [0xBC] = (0x2B, ','), // VKEY_OEM_COMMA → kVK_ANSI_Comma
        [0xBD] = (0x1B, '-'), // VKEY_OEM_MINUS → kVK_ANSI_Minus
        [0xBE] = (0x2F, '.'), // VKEY_OEM_PERIOD → kVK_ANSI_Period
        [0xBF] = (0x2C, '/'), // VKEY_OEM_2 → kVK_ANSI_Slash
        [0xC0] = (0x32, '`'), // VKEY_OEM_3 → kVK_ANSI_Grave
        [0xDB] = (0x21, '['), // VKEY_OEM_4 → kVK_ANSI_LeftBracket
        [0xDC] = (0x2A, '\\'), // VKEY_OEM_5 → kVK_ANSI_Backslash
        [0xDD] = (0x1E, ']'), // VKEY_OEM_6 → kVK_ANSI_RightBracket
        [0xDE] = (0x27, '\''), // VKEY_OEM_7 → kVK_ANSI_Quote
        [0xFE] = (0x47, '\u001B'), // VKEY_OEM_CLEAR → kVK_ANSI_KeypadClear, kClearCharCode
    };

    /// <summary><c>MacKeyCodeForWindowsKeyCode(keycode, 0, nullptr, &amp;character)</c>: the fixed (US) Mac key code, or
    /// <see cref="None"/>, and the character the key types with no modifiers, or '\0'.</summary>
    public static (int KeyCode, char Character) ForWindowsKeyCode(int virtualKey)
        => s_map.TryGetValue(virtualKey, out var info) ? info : (None, '\0');

    /// <summary>The key code <c>GlobalAcceleratorListenerMac::RegisterHotKey</c> registers (<c>kLayoutAwareGlobalHotkeys</c>,
    /// on by default): the first of key codes 0-127 that types the key's character in the current layout and is not a
    /// dead key, else the fixed key code. <paramref name="translate"/> is <c>NsKeyCodeAndModifiersToCharacter(i, 0)</c>.</summary>
    public static int ForHotKey(int virtualKey, Func<int, (char Character, bool DeadKey)> translate)
    {
        ArgumentNullException.ThrowIfNull(translate);
        var (fixedKeyCode, character) = ForWindowsKeyCode(virtualKey);
        if (character != '\0')
        {
            for (var i = 0; i < 128; i++)
            {
                var (translated, dead) = translate(i);
                if (translated == character && !dead) return i;
            }
        }
        return fixedKeyCode;
    }

    /// <summary>The Windows virtual key of a media key press in a system-defined event's <c>data1</c>, as
    /// <c>MediaKeysListenerImpl::EventTapCallback</c> reads it: play, next and previous (fast and rewind count as next
    /// and previous), on key down only; null for anything else.</summary>
    public static int? MediaKeyPressed(long data1)
    {
        var keyCode = (int)((data1 & 0xFFFF0000) >> 16);
        var pressed = ((data1 & 0xFF00) >> 8) == 0xA;
        if (!pressed) return null;
        return keyCode switch
        {
            16 => 0xB3, // NX_KEYTYPE_PLAY → VKEY_MEDIA_PLAY_PAUSE
            18 or 20 => 0xB1, // NX_KEYTYPE_PREVIOUS, NX_KEYTYPE_REWIND → VKEY_MEDIA_PREV_TRACK
            17 or 19 => 0xB0, // NX_KEYTYPE_NEXT, NX_KEYTYPE_FAST → VKEY_MEDIA_NEXT_TRACK
            _ => null,
        };
    }
}
