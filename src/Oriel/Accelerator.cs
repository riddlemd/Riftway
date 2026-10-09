namespace Oriel;

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
    /// <summary>The modifiers as Oriel's menus read them, with AltGr counted as Alt.</summary>
    internal static AcceleratorModifiers ForMenus(this AcceleratorModifiers m)
        => m.HasFlag(AcceleratorModifiers.AltGr) ? (m & ~AcceleratorModifiers.AltGr) | AcceleratorModifiers.Alt : m;
}

/// <summary>An Electron accelerator string such as <c>CmdOrCtrl+Shift+Z</c>, parsed for one OS.</summary>
/// <param name="KeyCode">The Windows virtual-key code, which CEF uses on every OS.</param>
/// <param name="MacKeyEquivalent">The key as an <c>NSMenuItem.keyEquivalent</c>.</param>
/// <param name="Modifiers">The modifier keys.</param>
internal readonly record struct Accelerator(int KeyCode, string MacKeyEquivalent, AcceleratorModifiers Modifiers)
{
    private static readonly Dictionary<string, (int KeyCode, string Mac)> s_keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Plus"] = (0xBB, "+"),
        ["Space"] = (0x20, " "),
        ["Tab"] = (0x09, "\t"),
        ["Backspace"] = (0x08, "\b"),
        ["Delete"] = (0x2E, "\uF728"),
        ["Insert"] = (0x2D, "\uF727"),
        ["Return"] = (0x0D, "\r"),
        ["Enter"] = (0x0D, "\r"),
        ["Escape"] = (0x1B, "\u001B"),
        ["Esc"] = (0x1B, "\u001B"),
        ["Up"] = (0x26, "\uF700"),
        ["Down"] = (0x28, "\uF701"),
        ["Left"] = (0x25, "\uF702"),
        ["Right"] = (0x27, "\uF703"),
        ["Home"] = (0x24, "\uF729"),
        ["End"] = (0x23, "\uF72B"),
        ["PageUp"] = (0x21, "\uF72C"),
        ["PageDown"] = (0x22, "\uF72D"),
        // No character, so no key equivalent in a macOS menu.
        ["MediaNextTrack"] = (0xB0, ""),
        ["MediaPreviousTrack"] = (0xB1, ""),
        ["MediaStop"] = (0xB2, ""),
        ["MediaPlayPause"] = (0xB3, ""),
        ["VolumeMute"] = (0xAD, ""),
        ["VolumeDown"] = (0xAE, ""),
        ["VolumeUp"] = (0xAF, ""),
        ["-"] = (0xBD, "-"),
        ["="] = (0xBB, "="),
        [","] = (0xBC, ","),
        ["."] = (0xBE, "."),
        ["/"] = (0xBF, "/"),
        [";"] = (0xBA, ";"),
        ["'"] = (0xDE, "'"),
        ["["] = (0xDB, "["),
        ["]"] = (0xDD, "]"),
        ["\\"] = (0xDC, "\\"),
        ["`"] = (0xC0, "`"),
    };

    /// <summary>Parses <paramref name="text"/> for macOS (<paramref name="macOS"/>) or for Windows and Linux.</summary>
    /// <exception cref="ArgumentException">An unknown modifier or key, no key, or more than one key.</exception>
    internal static Accelerator Parse(string text, bool macOS)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var modifiers = AcceleratorModifiers.None;
        (int KeyCode, string Mac)? key = null;
        // "CmdOrCtrl++" names the Plus key with the literal character, so a trailing empty part is '+'.
        var parts = text.EndsWith("++", StringComparison.Ordinal) ? [.. text[..^2].Split('+'), "Plus"] : text.Split('+');
        foreach (var part in parts)
        {
            var modifier = ModifierOf(part, macOS);
            if (modifier is { } m)
            {
                modifiers |= m;
                continue;
            }
            if (key is not null) throw new ArgumentException($"'{text}' names more than one key.", nameof(text));
            key = KeyOf(part) ?? throw new ArgumentException($"'{part}' in '{text}' is not a supported key.", nameof(text));
        }
        if (key is not { } k) throw new ArgumentException($"'{text}' has no key.", nameof(text));
        return new Accelerator(k.KeyCode, k.Mac, modifiers);
    }

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

    private static (int KeyCode, string Mac)? KeyOf(string part)
    {
        if (s_keys.TryGetValue(part, out var named)) return named;
        if (part.Length == 1 && char.IsAsciiLetter(part[0])) return (char.ToUpperInvariant(part[0]), char.ToLowerInvariant(part[0]).ToString());
        if (part.Length == 1 && char.IsAsciiDigit(part[0])) return (part[0], part);
        if (part.Length is 2 or 3 && (part[0] is 'F' or 'f') && int.TryParse(part.AsSpan(1), out var n) && n is >= 1 and <= 24)
            return (0x6F + n, ((char)(0xF703 + n)).ToString());
        return null;
    }
}
