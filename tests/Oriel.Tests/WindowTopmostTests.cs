using System.Runtime.InteropServices;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class WindowTopmostTests
{
    [Fact]
    public void EnsureTopmost_MakesAPlainWindowTopmost()
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = CreateWindowExW(0, "STATIC", "", WsPopup, 0, 0, 10, 10, 0, 0, 0, 0);
        Assert.NotEqual(0, hwnd);
        try
        {
            Assert.False(WindowsWindow.IsTopmost(hwnd));
            Assert.True(WindowsWindow.EnsureTopmost(hwnd));
            Assert.True(WindowsWindow.IsTopmost(hwnd));
        }
        finally { _ = DestroyWindow(hwnd); }
    }

    [Theory]
    [InlineData(true, 3L, false, true)]
    [InlineData(true, 0L, false, true)]
    [InlineData(false, 3L, false, false)]
    [InlineData(true, 3L, true, true)]
    [InlineData(true, -1L, true, true)]
    [InlineData(true, 0L, true, false)]
    [InlineData(false, 3L, true, false)]
    public void StaysOnTop_FollowsElectron(bool flag, long macLevel, bool mac, bool expected)
        => Assert.Equal(expected, BrowserWindow.StaysOnTop(flag, macLevel, mac));

    [Theory]
    [InlineData("normal", 0L)]
    [InlineData("floating", 3L)]
    [InlineData("torn-off-menu", 3L)]
    [InlineData("modal-panel", 8L)]
    [InlineData("main-menu", 24L)]
    [InlineData("status", 25L)]
    [InlineData("pop-up-menu", 101L)]
    [InlineData("screen-saver", 1000L)]
    [InlineData("bogus", 0L)]
    [InlineData("", 0L)]
    [InlineData("Floating", 0L)]
    public void MacWindowLevel_MapsElectronNames_UnknownIsNormal(string level, long expected)
        => Assert.Equal(expected, BrowserWindow.MacWindowLevel(level));

    [Theory]
    [InlineData(3L, 3L)]
    [InlineData(-5L, -5L)]
    [InlineData(10L, 10L)]
    [InlineData(-6L, -5L)]
    [InlineData(11L, 10L)]
    [InlineData(1000L + int.MaxValue, 10L)]
    public void ClampMacLevel_KeepsTheLevelInTheWindowServersRange(long level, long expected)
        => Assert.Equal(expected, BrowserWindow.ClampMacLevel(level, (-5, 10)));

    [Fact]
    public void MacLevelRange_IsTheWindowServersMinimumAndMaximum()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal((int.MinValue + 5, int.MaxValue - 16), MacWindow.LevelRange());
    }

    // An unknown level: macOS reads it as NSNormalWindowLevel, Windows and Linux only look at the flag.
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, true, true)]
    [InlineData(-1, true, true)]
    [InlineData(0, false, true)]
    public void UnknownLevel_StaysOnTopPerOs(int relativeLevel, bool mac, bool expected)
        => Assert.Equal(expected, BrowserWindow.StaysOnTop(true, BrowserWindow.MacWindowLevel("bogus") + relativeLevel, mac));

    [Theory]
    [InlineData(true, "floating", true)]
    [InlineData(true, "torn-off-menu", true)]
    [InlineData(true, "modal-panel", true)]
    [InlineData(true, "main-menu", true)]
    [InlineData(true, "status", true)]
    [InlineData(true, "pop-up-menu", false)]
    [InlineData(true, "screen-saver", false)]
    [InlineData(true, "normal", false)]
    [InlineData(true, "bogus", false)]
    [InlineData(false, "floating", false)]
    public void PlacedBehindTaskbar_OnlyTheLevelsBelowPopUpMenu(bool onTop, string level, bool expected)
        => Assert.Equal(expected, BrowserWindow.PlacedBehindTaskbar(onTop, level));

    private const uint WsPopup = 0x80000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3, 3, true)]
    [InlineData(-9, -5, true)]
    [InlineData(99, 10, true)]
    public void MacLevel_IsOnTopForAnyLevelButNormal_AfterTheClamp(long level, long clamped, bool onTop)
        => Assert.Equal((clamped, onTop), BrowserWindow.MacLevel(level, (-5, 10)));
}
