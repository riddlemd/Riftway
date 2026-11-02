using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class LinuxPowerAndShortcutTests
{
    [Theory]
    [InlineData("A", 0x61u)]
    [InlineData("z", 0x7Au)]
    [InlineData("0", 0x30u)]
    [InlineData("9", 0x39u)]
    [InlineData("F1", 0xFFBEu)]
    [InlineData("F12", 0xFFC9u)]
    [InlineData("F24", 0xFFD5u)]
    [InlineData("Space", 0x20u)]
    [InlineData("Tab", 0xFF09u)]
    [InlineData("Backspace", 0xFF08u)]
    [InlineData("Delete", 0xFFFFu)]
    [InlineData("Insert", 0xFF63u)]
    [InlineData("Enter", 0xFF0Du)]
    [InlineData("Esc", 0xFF1Bu)]
    [InlineData("Up", 0xFF52u)]
    [InlineData("Down", 0xFF54u)]
    [InlineData("Left", 0xFF51u)]
    [InlineData("Right", 0xFF53u)]
    [InlineData("Home", 0xFF50u)]
    [InlineData("End", 0xFF57u)]
    [InlineData("PageUp", 0xFF55u)]
    [InlineData("PageDown", 0xFF56u)]
    [InlineData("Plus", 0x3Du)]
    [InlineData("-", 0x2Du)]
    [InlineData(",", 0x2Cu)]
    [InlineData(".", 0x2Eu)]
    [InlineData("/", 0x2Fu)]
    [InlineData(";", 0x3Bu)]
    [InlineData("'", 0x27u)]
    [InlineData("[", 0x5Bu)]
    [InlineData("]", 0x5Du)]
    [InlineData("\\", 0x5Cu)]
    [InlineData("`", 0x60u)]
    [InlineData("num0", 0xFFB0u)]
    [InlineData("num9", 0xFFB9u)]
    [InlineData("numdec", 0xFFAEu)]
    [InlineData("numadd", 0xFFABu)]
    [InlineData("numsub", 0xFFADu)]
    [InlineData("nummult", 0xFFAAu)]
    [InlineData("numdiv", 0xFFAFu)]
    [InlineData("numlock", 0xFF7Fu)]
    [InlineData("capslock", 0xFFE5u)]
    [InlineData("scrolllock", 0xFF14u)]
    [InlineData("!", 0x31u)]
    [InlineData("@", 0x32u)]
    [InlineData(")", 0x30u)]
    [InlineData("~", 0x60u)]
    [InlineData("?", 0x2Fu)]
    [InlineData("printscreen", 0u)] // Chromium's table has no VKEY_SNAPSHOT
    public void KeysymOf_IsChromiumsUnshiftedKeysym(string key, uint keysym)
        => Assert.Equal(keysym, X11Keys.KeysymOf((int)GlobalShortcuts.Key("Ctrl+" + key).VirtualKey));

    [Theory]
    [InlineData(0x60, 0xFFB0u)] // VKEY_NUMPAD0 → XK_KP_0
    [InlineData(0x69, 0xFFB9u)] // VKEY_NUMPAD9 → XK_KP_9
    [InlineData(0x6A, 0xFFAAu)] // VKEY_MULTIPLY
    [InlineData(0x6F, 0xFFAFu)] // VKEY_DIVIDE
    [InlineData(0x10, 0xFFE1u)] // VKEY_SHIFT → Shift_L
    [InlineData(0xA0, 0xFFE1u)] // VKEY_LSHIFT → Shift_L
    [InlineData(0xA1, 0xFFE2u)] // VKEY_RSHIFT → Shift_R
    [InlineData(0xF3, 0xFF2Au)] // VKEY_DBE_SBCSCHAR → Zenkaku_Hankaku
    [InlineData(0xF4, 0xFF2Au)] // VKEY_DBE_DBCSCHAR → Zenkaku_Hankaku
    [InlineData(0xB3, 0x1008FF14u)] // VKEY_MEDIA_PLAY_PAUSE → XF86AudioPlay
    [InlineData(0xE8, 0x1008FF05u)] // VKEY_KBD_BRIGHTNESS_UP
    [InlineData(0x6C, 0u)] // VKEY_SEPARATOR: not in Chromium's table
    [InlineData(0x00, 0u)]
    [InlineData(0x88, 0u)] // past F24
    public void KeysymOf_CoversChromiumsTableAndNothingElse(int vk, uint keysym) => Assert.Equal(keysym, X11Keys.KeysymOf(vk));

    [Fact]
    public void Modifiers_AreShiftControlMod1_AndMod4ForSuper()
    {
        Assert.Equal(X11Keys.ShiftMask, X11Keys.Modifiers(alt: false, control: false, shift: true, super: false));
        Assert.Equal(X11Keys.ControlMask, X11Keys.Modifiers(alt: false, control: true, shift: false, super: false));
        Assert.Equal(X11Keys.Mod1Mask, X11Keys.Modifiers(alt: true, control: false, shift: false, super: false));
        Assert.Equal(X11Keys.Mod4Mask, X11Keys.Modifiers(alt: false, control: false, shift: false, super: true));
        Assert.Equal(0, X11Keys.Modifiers(false, false, false, false));
        Assert.Equal((ushort)(1 | 4 | 8), GlobalShortcuts.X11Modifiers(GlobalShortcuts.Key("Ctrl+Alt+Shift+F9").Modifiers));
        Assert.Equal((ushort)64, GlobalShortcuts.X11Modifiers(GlobalShortcuts.Key("Super+K").Modifiers));
        Assert.Equal((ushort)4, GlobalShortcuts.X11Modifiers(GlobalShortcuts.Key("Control+K").Modifiers));
    }

    [Fact]
    public void LockMasks_AreEveryCombinationOfNumCapsAndScrollLock()
    {
        Assert.Equal(8, X11Keys.LockMasks.Length);
        Assert.Equal(8, X11Keys.LockMasks.Distinct().Count());
        Assert.All(X11Keys.LockMasks, m => Assert.Equal(0, m & ~(X11Keys.Mod2Mask | X11Keys.LockMask | X11Keys.Mod5Mask)));
        Assert.Equal(0, X11Keys.LockMasks[0]);
    }

    [Fact]
    public void Relevant_DropsLockModifiers()
    {
        Assert.Equal((ushort)(1 | 4 | 8), X11Keys.Relevant(1 | 4 | 8 | 2 | 16 | 128));
        Assert.Equal((ushort)64, X11Keys.Relevant(64 | 16));
        Assert.Equal((ushort)0, X11Keys.Relevant(2 | 16 | 32 | 128));
    }

    [Fact]
    public void KeycodeOf_IsTheFirstKeycodeWithTheKeysymInAnyColumn()
    {
        // Keycodes 8, 9, 10 with two keysyms each.
        uint[] map = [0x61, 0x41, 0x62, 0x31, 0x31, 0x21];
        Assert.Equal(8, X11Keys.KeycodeOf(0x61, map, 2, 8));
        Assert.Equal(8, X11Keys.KeycodeOf(0x41, map, 2, 8));
        Assert.Equal(9, X11Keys.KeycodeOf(0x31, map, 2, 8));
        Assert.Equal(10, X11Keys.KeycodeOf(0x21, map, 2, 8));
        Assert.Equal(0, X11Keys.KeycodeOf(0x7A, map, 2, 8));
        Assert.Equal(0, X11Keys.KeycodeOf(0x61, map, 0, 8));
    }

    [Theory]
    [InlineData((byte)0, false)] // Off
    [InlineData((byte)1, true)] // On
    [InlineData((byte)2, true)] // Cycle
    [InlineData((byte)3, false)] // Disabled
    [InlineData(null, false)]
    public void MitScreenSaverActive_OnAndCycle(byte? state, bool active) => Assert.Equal(active, X11Keys.MitScreenSaverActive(state));

    [Fact]
    public void LinuxLocked_DBusStateWinsAndTheX11ScreenSaverIsOnlyAskedWithoutIt()
    {
        var asked = 0;
        bool X11(bool value) { asked++; return value; }
        Assert.True(PowerRules.LinuxLocked(true, () => X11(false)));
        Assert.False(PowerRules.LinuxLocked(false, () => X11(true)));
        Assert.Equal(0, asked);
        Assert.True(PowerRules.LinuxLocked(null, () => X11(true)));
        Assert.False(PowerRules.LinuxLocked(null, () => X11(false)));
        Assert.Equal(2, asked);
    }

    [Fact]
    public void GetSystemIdleState_RejectsAThresholdNotAboveZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerMonitor.GetSystemIdleState(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerMonitor.GetSystemIdleState(-5));
    }

    [Fact]
    public void LockStateOf_IsASingleBoolean()
    {
        Assert.True(LinuxScreenLock.LockStateOf([true]));
        Assert.False(LinuxScreenLock.LockStateOf([false]));
        Assert.Null(LinuxScreenLock.LockStateOf([]));
        Assert.Null(LinuxScreenLock.LockStateOf([true, 1u]));
        Assert.Null(LinuxScreenLock.LockStateOf(["true"]));
    }

    [Fact]
    public void ScreenSaverServices_InChromiumsOrder()
        => Assert.Equal(["org.freedesktop.ScreenSaver", "org.cinnamon.ScreenSaver", "org.gnome.ScreenSaver", "org.mate.ScreenSaver", "org.xfce.ScreenSaver"],
            LinuxScreenLock.Services.Select(s => s.Service));

    [Fact]
    public void Inhibit_GnomeFlagsAndFallbacksFollowChromium()
    {
        Assert.Equal(4u, LinuxInhibit.GnomeFlags(display: false));
        Assert.Equal(8u, LinuxInhibit.GnomeFlags(display: true));
        Assert.Equal(LinuxInhibitApi.FreedesktopPower, LinuxInhibit.FallbackOf(display: false));
        Assert.Equal(LinuxInhibitApi.FreedesktopScreenSaver, LinuxInhibit.FallbackOf(display: true));
        Assert.Equal("Uninhibit", LinuxInhibit.UninhibitMethodOf(LinuxInhibitApi.Gnome));
        Assert.Equal("UnInhibit", LinuxInhibit.UninhibitMethodOf(LinuxInhibitApi.FreedesktopPower));
        Assert.Equal("UnInhibit", LinuxInhibit.UninhibitMethodOf(LinuxInhibitApi.FreedesktopScreenSaver));
        Assert.Equal(("org.gnome.SessionManager", "org.gnome.SessionManager", "/org/gnome/SessionManager"), LinuxInhibit.ServiceOf(LinuxInhibitApi.Gnome));
        Assert.Equal(("org.freedesktop.PowerManagement", "org.freedesktop.PowerManagement.Inhibit", "/org/freedesktop/PowerManagement/Inhibit"),
            LinuxInhibit.ServiceOf(LinuxInhibitApi.FreedesktopPower));
        Assert.Equal(("org.freedesktop.ScreenSaver", "org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver"), LinuxInhibit.ServiceOf(LinuxInhibitApi.FreedesktopScreenSaver));
    }

    [Fact]
    public void InhibitArgs_GnomeHasXidAndFlags_FreedesktopOnlyAppAndReason()
    {
        Assert.Equal(["/opt/app/App", 0u, "App", 8u], LinuxInhibit.InhibitArgs(LinuxInhibitApi.Gnome, "/opt/app/App", "App", display: true));
        Assert.Equal(["/opt/app/App", 0u, "App", 4u], LinuxInhibit.InhibitArgs(LinuxInhibitApi.Gnome, "/opt/app/App", "App", display: false));
        Assert.Equal(["./App", "Why"], LinuxInhibit.InhibitArgs(LinuxInhibitApi.FreedesktopPower, "./App", "Why", display: false));
        Assert.Equal(["./App", "Why"], LinuxInhibit.InhibitArgs(LinuxInhibitApi.FreedesktopScreenSaver, "./App", "Why", display: true));
    }
}
