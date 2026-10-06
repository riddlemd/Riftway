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

    private const uint WsPopup = 0x80000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);
}
