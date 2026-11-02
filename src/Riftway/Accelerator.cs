namespace Riftway;

/// <summary>Modifier keys of a parsed accelerator, after <c>CmdOrCtrl</c> is resolved for the OS.</summary>
[Flags]
internal enum AcceleratorModifiers
{
    None = 0,
    Shift = 1 << 0,
    Control = 1 << 1,
    Alt = 1 << 2,
    // Command on macOS; the Windows/Super key elsewhere, which CEF accelerators cannot express.
    Command = 1 << 3,
    // Electron's EF_ALTGR_DOWN: a modifier of its own, not Alt.
    AltGr = 1 << 4,
}

internal static class AcceleratorModifiersExtensions
{
    /// <summary>The modifiers as Riftway's menus read them, with AltGr counted as Alt.</summary>
    internal static AcceleratorModifiers ForMenus(this AcceleratorModifiers m)
        => m.HasFlag(AcceleratorModifiers.AltGr) ? (m & ~AcceleratorModifiers.AltGr) | AcceleratorModifiers.Alt : m;
}

/// <summary>An Electron accelerator string such as <c>CmdOrCtrl+Shift+Z</c>, parsed for one OS, as Electron's
/// <c>StringToAccelerator</c> and <c>KeyboardCodeFromStr</c> parse it.</summary>
/// <param name="KeyCode">The Windows virtual-key code, which CEF uses on every OS.</param>
/// <param name="MacKeyEquivalent">The key as an <c>NSMenuItem.keyEquivalent</c>, shifted when Shift is down.</param>
/// <param name="Modifiers">The modifier keys; a shifted character such as <c>!</c> or <c>Plus</c> adds Shift.</param>
internal readonly record struct Accelerator(int KeyCode, string MacKeyEquivalent, AcceleratorModifiers Modifiers)
{
    // base::kWhitespaceASCII; char.IsWhiteSpace would also trim 0x1C-0x1F.
    private static readonly char[] s_whitespace = ['\t', '\n', '\v', '\f', '\r', ' '];

    // KeyboardCodeFromKeyIdentifier's multi-character names; Shifted marks the ones that carry a shifted_char.
    private static readonly Dictionary<string, (int KeyCode, bool Shifted)> s_names = BuildNames();

    private static Dictionary<string, (int KeyCode, bool Shifted)> BuildNames()
    {
        var names = new Dictionary<string, (int, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Plus"] = (0xBB, true),
            ["Space"] = (0x20, false),
            ["Tab"] = (0x09, false),
            ["Backspace"] = (0x08, false),
            ["Delete"] = (0x2E, false),
            ["Insert"] = (0x2D, false),
            ["Return"] = (0x0D, false),
            ["Enter"] = (0x0D, false),
            ["Escape"] = (0x1B, false),
            ["Esc"] = (0x1B, false),
            ["Up"] = (0x26, false),
            ["Down"] = (0x28, false),
            ["Left"] = (0x25, false),
            ["Right"] = (0x27, false),
            ["Home"] = (0x24, false),
            ["End"] = (0x23, false),
            ["PageUp"] = (0x21, false),
            ["PageDown"] = (0x22, false),
            ["CapsLock"] = (0x14, false),
            ["NumLock"] = (0x90, false),
            ["ScrollLock"] = (0x91, false),
            ["PrintScreen"] = (0x2C, false),
            ["NumAdd"] = (0x6B, false),
            ["NumSub"] = (0x6D, false),
            ["NumMult"] = (0x6A, false),
            ["NumDiv"] = (0x6F, false),
            ["NumDec"] = (0x6E, false),
            ["MediaNextTrack"] = (0xB0, false),
            ["MediaPreviousTrack"] = (0xB1, false),
            ["MediaStop"] = (0xB2, false),
            ["MediaPlayPause"] = (0xB3, false),
            ["VolumeMute"] = (0xAD, false),
            ["VolumeDown"] = (0xAE, false),
            ["VolumeUp"] = (0xAF, false),
        };
        for (var i = 0; i < 10; i++) names["num" + i] = (0x60 + i, false);
        for (var i = 1; i <= 24; i++) names["F" + i] = (0x6F + i, false);
        return names;
    }

    /// <summary>Parses <paramref name="text"/> for macOS (<paramref name="macOS"/>) or for Windows and Linux.</summary>
    /// <exception cref="ArgumentException">Non-ASCII text, or no valid key: Electron's <c>StringToAccelerator</c> returns false.</exception>
    internal static Accelerator Parse(string text, bool macOS)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!text.All(char.IsAscii)) throw new ArgumentException($"'{text}' has characters outside ASCII.", nameof(text));
        var modifiers = AcceleratorModifiers.None;
        // Electron overwrites the key with every non-modifier token, an unknown one included, so the last one decides.
        var key = 0;
        foreach (var part in text.Split('+'))
        {
            var token = part.Trim(s_whitespace);
            if (token.Length == 0) continue;
            if (ModifierOf(token, macOS) is { } modifier)
            {
                modifiers |= modifier;
                continue;
            }
            var (code, shifted) = Resolve(token);
            if (shifted) modifiers |= AcceleratorModifiers.Shift;
            key = code;
        }
        if (key == 0) throw new ArgumentException($"'{text}' has no valid key.", nameof(text));
        return new Accelerator(key, MacCharacter(key, modifiers.HasFlag(AcceleratorModifiers.Shift)), modifiers);
    }

    /// <summary>The modifiers of a macOS menu item: Chromium drops Shift when the key equivalent is itself the shifted
    /// character (<c>!</c>, <c>A</c>, <c>+</c>), since Cocoa would otherwise require it twice.</summary>
    internal AcceleratorModifiers MacMenuModifiers
        => Modifiers.HasFlag(AcceleratorModifiers.Shift) && MacShiftedCharacter(KeyCode) is not null ? Modifiers & ~AcceleratorModifiers.Shift : Modifiers;

    private static AcceleratorModifiers? ModifierOf(string part, bool macOS) => part.ToLowerInvariant() switch
    {
        "command" or "cmd" or "super" or "meta" => AcceleratorModifiers.Command,
        "control" or "ctrl" => AcceleratorModifiers.Control,
        "commandorcontrol" or "cmdorctrl" => macOS ? AcceleratorModifiers.Command : AcceleratorModifiers.Control,
        "alt" or "option" => AcceleratorModifiers.Alt,
        "altgr" => AcceleratorModifiers.AltGr,
        "shift" => AcceleratorModifiers.Shift,
        _ => null,
    };

    // Electron's KeyboardCodeFromStr: one character goes through KeyboardCodeFromCharCode, longer text through the name table.
    // KeyCode 0 is VKEY_UNKNOWN. A lone '+' never reaches here, since it splits tokens; "Plus" names that key.
    private static (int KeyCode, bool Shifted) Resolve(string token)
    {
        if (token.Length != 1) return s_names.TryGetValue(token, out var named) ? named : default;
        return char.ToLowerInvariant(token[0]) switch
        {
            ' ' => (0x20, false),
            '!' => (0x31, true),
            '"' => (0xDE, true),
            '#' => (0x33, true),
            '$' => (0x34, true),
            '%' => (0x35, true),
            '&' => (0x37, true),
            '(' => (0x39, true),
            ')' => (0x30, true),
            '*' => (0x38, true),
            ',' => (0xBC, false),
            '-' => (0xBD, false),
            '.' => (0xBE, false),
            '/' => (0xBF, false),
            >= '0' and <= '9' and var c => (c, false),
            ':' => (0xBA, true),
            ';' => (0xBA, false),
            '<' => (0xBC, true),
            '=' => (0xBB, false),
            '>' => (0xBE, true),
            '?' => (0xBF, true),
            '@' => (0x32, true),
            '[' => (0xDB, false),
            '\'' => (0xDE, false),
            '\\' => (0xDC, false),
            ']' => (0xDD, false),
            '^' => (0x36, true),
            '_' => (0xBD, true),
            '`' => (0xC0, false),
            >= 'a' and <= 'z' and var c => (char.ToUpperInvariant(c), false),
            '{' => (0xDB, true),
            '|' => (0xDC, true),
            '}' => (0xDD, true),
            '~' => (0xC0, true),
            '\b' => (0x08, false),
            '\t' => (0x09, false),
            '\r' => (0x0D, false),
            '\u001B' => (0x1B, false),
            '\u007F' => (0x2E, false),
            _ => default,
        };
    }

    // ui::MacKeyCodeForWindowsKeyCode's character_ignoring_all_modifiers, or its shifted character when Shift is down;
    // empty for keys with no character, which a macOS menu cannot bind.
    private static string MacCharacter(int keyCode, bool shift)
        => shift && MacShiftedCharacter(keyCode) is { } shifted ? shifted : MacBaseCharacter(keyCode);

    private static string MacBaseCharacter(int keyCode) => keyCode switch
    {
        0x08 => "\b",
        0x09 => "\t",
        0x0D => "\r",
        0x1B => "\u001B",
        0x20 => " ",
        0x21 => "\uF72C",
        0x22 => "\uF72D",
        0x23 => "\uF72B",
        0x24 => "\uF729",
        0x25 => "\uF702",
        0x26 => "\uF700",
        0x27 => "\uF703",
        0x28 => "\uF701",
        0x2D => "\uF727",
        0x2E => "\uF728",
        >= 0x30 and <= 0x39 => ((char)keyCode).ToString(),
        >= 0x41 and <= 0x5A => ((char)(keyCode + 32)).ToString(),
        >= 0x60 and <= 0x69 => ((char)(keyCode - 0x30)).ToString(),
        0x6A => "*",
        0x6B => "+",
        0x6D => "-",
        0x6E => ".",
        0x6F => "/",
        >= 0x70 and <= 0x87 => ((char)(0xF703 + (keyCode - 0x6F))).ToString(),
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        // Caps/Num/Scroll Lock, Print Screen and the media keys type nothing.
        _ => "",
    };

    // The branches of MacKeyCodeForWindowsKeyCode that change the character under Shift; null for every other key.
    private static string? MacShiftedCharacter(int keyCode) => keyCode switch
    {
        >= 0x30 and <= 0x39 => ")!@#$%^&*("[keyCode - 0x30].ToString(),
        >= 0x41 and <= 0x5A => ((char)keyCode).ToString(),
        0xC0 => "~",
        0xBD => "_",
        0xBB => "+",
        0xDB => "{",
        0xDD => "}",
        0xDC => "|",
        0xBA => ":",
        0xDE => "\"",
        0xBC => "<",
        0xBE => ">",
        0xBF => "?",
        _ => null,
    };
}
