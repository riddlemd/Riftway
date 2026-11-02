using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class MacShellAndShortcutTests
{
    private static HotKey K(string accelerator) => GlobalShortcuts.Key(accelerator);

    [Theory]
    [InlineData("num0", 0x60u)]
    [InlineData("num9", 0x69u)]
    [InlineData("numdec", 0x6Eu)]
    [InlineData("numadd", 0x6Bu)]
    [InlineData("numsub", 0x6Du)]
    [InlineData("nummult", 0x6Au)]
    [InlineData("numdiv", 0x6Fu)]
    [InlineData("numlock", 0x90u)]
    [InlineData("capslock", 0x14u)]
    [InlineData("scrolllock", 0x91u)]
    [InlineData("printscreen", 0x2Cu)]
    public void Key_NewNamesAreWindowsVirtualKeys(string key, uint virtualKey)
        => Assert.Equal(new HotKey(0x2, virtualKey), K("Ctrl+" + key));

    [Theory]
    [InlineData("!", 0x4u, 0x31u)]
    [InlineData("@", 0x4u, 0x32u)]
    [InlineData(")", 0x4u, 0x30u)]
    [InlineData("Plus", 0x4u, 0xBBu)]
    [InlineData("=", 0u, 0xBBu)]
    public void Key_ShiftedCharacterRegistersWithShift(string key, uint shift, uint virtualKey)
        => Assert.Equal(new HotKey(0x2 | shift, virtualKey), K("Ctrl+" + key));

    [Theory]
    [InlineData("Shift+K", 0x200u)]
    [InlineData("Ctrl+K", 0x1000u)]
    [InlineData("Alt+K", 0x800u)]
    [InlineData("Option+K", 0x800u)]
    [InlineData("Command+K", 0x100u)]
    [InlineData("Super+K", 0x100u)]
    [InlineData("AltGr+K", 0u)]
    [InlineData("K", 0u)]
    [InlineData("Cmd+Ctrl+Alt+Shift+K", 0x1B00u)]
    public void CarbonModifiers_AreChromiumsMacMapping(string accelerator, uint carbon)
        => Assert.Equal(carbon, GlobalShortcuts.CarbonModifiers(GlobalShortcuts.Modifiers(Accelerator.Parse(accelerator, macOS: true).Modifiers)));

    [Fact]
    public void IsMediaKey_OnlyTheFourBareMediaKeys()
    {
        Assert.True(GlobalShortcuts.IsMediaKey(K("MediaPlayPause")));
        Assert.True(GlobalShortcuts.IsMediaKey(K("MediaStop")));
        Assert.True(GlobalShortcuts.IsMediaKey(K("MediaPreviousTrack")));
        Assert.True(GlobalShortcuts.IsMediaKey(K("MediaNextTrack")));
        Assert.False(GlobalShortcuts.IsMediaKey(K("VolumeUp")));
        Assert.False(GlobalShortcuts.IsMediaKey(new HotKey(0, 0xB4)));
        Assert.False(GlobalShortcuts.IsMediaKey(K("Shift+MediaPlayPause")));
        Assert.False(GlobalShortcuts.IsMediaKey(K("AltGr+MediaPlayPause")));
    }

    [Theory]
    [InlineData(0x4B, 0x28, 'k')] // K
    [InlineData(0x41, 0x00, 'a')]
    [InlineData(0x30, 0x1D, '0')]
    [InlineData(0x39, 0x19, '9')]
    [InlineData(0x70, 0x7A, '')] // F1
    [InlineData(0x83, 0x5A, '')] // F20
    [InlineData(0x2D, 0x72, '')] // Insert is kVK_Help, as Chromium has it
    [InlineData(0x2E, 0x75, '')] // Delete
    [InlineData(0x08, 0x33, '\b')] // Backspace
    [InlineData(0x0D, 0x24, '\r')]
    [InlineData(0x20, 0x31, ' ')]
    [InlineData(0xBB, 0x18, '=')] // Plus is the = key
    [InlineData(0xDE, 0x27, '\'')]
    [InlineData(0x10, 0x38, '\0')] // Shift has no character
    [InlineData(0x14, 0x39, '\0')] // CapsLock
    [InlineData(0x60, 0x52, '0')] // Num0
    [InlineData(0x69, 0x5C, '9')] // Num9
    [InlineData(0x6A, 0x43, '*')] // NumMult
    [InlineData(0x6B, 0x45, '+')] // NumAdd
    [InlineData(0x6D, 0x4E, '-')] // NumSub
    [InlineData(0x6E, 0x41, '.')] // NumDec
    [InlineData(0x6F, 0x4B, '/')] // NumDiv
    public void ForWindowsKeyCode_IsChromiumsTable(int vk, int macKeyCode, char character)
        => Assert.Equal((macKeyCode, character), MacKeyCodes.ForWindowsKeyCode(vk));

    [Theory]
    [InlineData(0x84)] // F21
    [InlineData(0x87)] // F24
    [InlineData(0xB3)] // MediaPlayPause
    [InlineData(0xAF)] // VolumeUp
    [InlineData(0x13)] // Pause: Chromium lists a character but no key code, so neither counts
    [InlineData(0x2C)] // PrintScreen
    [InlineData(0x90)] // NumLock
    [InlineData(0x91)] // ScrollLock
    public void ForWindowsKeyCode_NoMacKeyCode_IsMinusOneWithoutCharacter(int vk)
        => Assert.Equal((MacKeyCodes.None, '\0'), MacKeyCodes.ForWindowsKeyCode(vk));

    // The US layout as UCKeyTranslate gives it: each fixed key code types its table character.
    private static (char, bool) Us(int keyCode)
    {
        for (var vk = 0; vk < 256; vk++)
        {
            var (code, ch) = MacKeyCodes.ForWindowsKeyCode(vk);
            if (code == keyCode && ch is >= ' ' and < '\u007F') return (ch, false);
        }
        return ('\u0010', false);
    }

    [Fact]
    public void ForHotKey_UsLayout_GivesTheFixedKeyCodes()
    {
        Assert.Equal(0x28, MacKeyCodes.ForHotKey(0x4B, Us));
        Assert.Equal(0x1D, MacKeyCodes.ForHotKey(0x30, Us));
        Assert.Equal(0x7A, MacKeyCodes.ForHotKey(0x70, Us));
    }

    [Fact]
    public void ForHotKey_FindsTheKeyThatTypesTheCharacterInTheCurrentLayout()
    {
        // AZERTY: 'a' is on the US Q key (0x0C), 'q' on the US A key (0x00).
        static (char, bool) Azerty(int code) => code switch { 0x0C => ('a', false), 0x00 => ('q', false), _ => ('z', false) };
        Assert.Equal(0x0C, MacKeyCodes.ForHotKey(0x41, Azerty));
        Assert.Equal(0x00, MacKeyCodes.ForHotKey(0x51, Azerty));
    }

    [Fact]
    public void ForHotKey_TakesTheLowestKeyCode_AndSkipsDeadKeys()
    {
        Assert.Equal(0x05, MacKeyCodes.ForHotKey(0x4B, code => code is 0x05 or 0x40 ? ('k', false) : ('z', false)));
        Assert.Equal(0x40, MacKeyCodes.ForHotKey(0x4B, code => code switch { 0x05 => ('k', true), 0x40 => ('k', false), _ => ('z', false) }));
    }

    [Fact]
    public void ForHotKey_FallsBackToTheFixedKeyCode()
    {
        // No key types it in this layout.
        Assert.Equal(0x28, MacKeyCodes.ForHotKey(0x4B, _ => ('z', false)));
        // A key without a character is never searched for, even where keys translate to '\0'.
        Assert.Equal(0x38, MacKeyCodes.ForHotKey(0x10, _ => ('\0', false)));
        // Function-key characters never come out of UCKeyTranslate.
        Assert.Equal(0x7A, MacKeyCodes.ForHotKey(0x70, Us));
        Assert.Equal(MacKeyCodes.None, MacKeyCodes.ForHotKey(0x84, Us));
    }

    [Theory]
    [InlineData((16L << 16) | 0x0A00, 0xB3)] // NX_KEYTYPE_PLAY down
    [InlineData((17L << 16) | 0x0A00, 0xB0)] // NEXT
    [InlineData((19L << 16) | 0x0A00, 0xB0)] // FAST
    [InlineData((18L << 16) | 0x0A00, 0xB1)] // PREVIOUS
    [InlineData((20L << 16) | 0x0A00, 0xB1)] // REWIND
    [InlineData((16L << 16) | 0x0A01, 0xB3)] // a repeat is still a press
    [InlineData((1L << 32) | (16L << 16) | 0x0A00, 0xB3)] // bits above data1's low word are ignored
    public void MediaKeyPressed_ReadsData1AsChromium(long data1, int vk) => Assert.Equal(vk, MacKeyCodes.MediaKeyPressed(data1));

    [Theory]
    [InlineData((16L << 16) | 0x0B00)] // key up
    [InlineData((16L << 16) | 0x0000)]
    [InlineData((0L << 16) | 0x0A00)] // NX_KEYTYPE_SOUND_UP
    [InlineData((21L << 16) | 0x0A00)]
    public void MediaKeyPressed_IgnoresReleasesAndOtherKeys(long data1) => Assert.Null(MacKeyCodes.MediaKeyPressed(data1));

    [Theory]
    [InlineData("https://example.com/a{b}|c^d", "https://example.com/a%7Bb%7D%7Cc%5Ed")]
    [InlineData("https://example.com/p?q=[x]`y", "https://example.com/p?q=%5Bx%5D%60y")]
    [InlineData("https://example.com/p#frag \"z\"<>", "https://example.com/p#frag%20%22z%22%3C%3E")]
    [InlineData("https://example.com/p#a#b?c", "https://example.com/p#a%23b?c")]
    [InlineData("http://[::1]:8080/p[1]", "http://[::1]:8080/p%5B1%5D")]
    [InlineData("https://u:p@example.com/%41%zz~", "https://u:p@example.com/%41%25zz~")]
    [InlineData("https://example.com/café\\", "https://example.com/caf%C3%A9%5C")]
    [InlineData("mailto:a{b}@c", "mailto:a%7Bb%7D@c")]
    [InlineData("myapp://host", "myapp://host")]
    [InlineData("myapp://[::1]", "myapp://[::1]")]
    [InlineData("https://e.com/a%4", "https://e.com/a%254")]
    [InlineData("myapp://host?x=|", "myapp://host?x=%7C")]
    [InlineData("myapp:/x\u007F", "myapp:/x%7F")]
    [InlineData("data:,a\tb", "data:,a%09b")]
    public void NSURLString_EscapesPathQueryAndRefAsNSURLWithGURL(string spec, string expected)
        => Assert.Equal(expected, ShellRules.NSURLString(spec));

    [Fact]
    public void NSURLString_InvalidGurl_IsNull() => Assert.Null(ShellRules.NSURLString(""));

    [Fact]
    public void MacAbsolutePath_ResolvesOnlyRelativePaths()
    {
        Assert.Equal("/abs/x", ShellRules.MacAbsolutePath("/abs/x", _ => throw new InvalidOperationException()));
        Assert.Equal("/cwd/rel", ShellRules.MacAbsolutePath("rel", p => "/cwd/" + p));
        Assert.Equal("", ShellRules.MacAbsolutePath("missing", _ => null));
    }

    [Fact]
    public async Task Mac_InvalidUrl_RejectsWithElectronsText()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Shell.OpenExternalAsync("not a url"));
        Assert.Equal("Invalid URL", error.Message);
    }

    [Fact]
    public async Task Mac_EmptyPath_IsElectronsInvalidPath()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal("Invalid path", await Shell.OpenPathAsync(""));
        var error = await Assert.ThrowsAsync<IOException>(() => Shell.TrashItemAsync(""));
        Assert.Equal("Invalid file path: ", error.Message);
    }

    [Fact]
    public async Task Mac_TrashingAMissingFile_RejectsWithTheSystemsDescription()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var path = Path.Combine(Path.GetTempPath(), $"riftway-missing-{Guid.NewGuid():N}");
        var error = await Assert.ThrowsAsync<IOException>(() => Shell.TrashItemAsync(path));
        Assert.NotEqual("", error.Message);
        Assert.DoesNotContain("Invalid file path", error.Message);
    }

    [Fact]
    public void Mac_ShortcutLinksAreWindowsOnly()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Throws<PlatformNotSupportedException>(() => Shell.ReadShortcutLink("/nonexistent.lnk"));
    }
}
