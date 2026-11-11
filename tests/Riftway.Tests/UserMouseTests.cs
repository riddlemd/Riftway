using Riftway.Cef.Platform;

namespace Riftway.Tests;

// The user's mouse for before-mouse-event on Windows: what each page-window message says, and Chromium's click count.
public sealed class UserMouseTests
{
    private static nint Point(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    [Theory]
    [InlineData(0x0201u, WindowsPageMouse.Kind.Down, WindowsPageMouse.Button.Left, false)]
    [InlineData(0x0203u, WindowsPageMouse.Kind.Down, WindowsPageMouse.Button.Left, true)]
    [InlineData(0x0202u, WindowsPageMouse.Kind.Up, WindowsPageMouse.Button.Left, false)]
    [InlineData(0x0204u, WindowsPageMouse.Kind.Down, WindowsPageMouse.Button.Right, false)]
    [InlineData(0x0206u, WindowsPageMouse.Kind.Down, WindowsPageMouse.Button.Right, true)]
    [InlineData(0x0205u, WindowsPageMouse.Kind.Up, WindowsPageMouse.Button.Right, false)]
    [InlineData(0x0207u, WindowsPageMouse.Kind.Down, WindowsPageMouse.Button.Middle, false)]
    [InlineData(0x0209u, WindowsPageMouse.Kind.Down, WindowsPageMouse.Button.Middle, true)]
    [InlineData(0x0208u, WindowsPageMouse.Kind.Up, WindowsPageMouse.Button.Middle, false)]
    public void Buttons_AreDecodedWithTheirClientPosition(uint message, WindowsPageMouse.Kind kind, WindowsPageMouse.Button button, bool doubleClick)
    {
        var m = WindowsPageMouse.Decode(message, 0x4, Point(120, 45));
        Assert.Equal(new WindowsPageMouse.Message(kind, button, 120, 45, doubleClick, 0x4), m);
    }

    [Theory]
    [InlineData(0x020Bu, 1, WindowsPageMouse.Kind.Down, WindowsPageMouse.Button.Back)]
    [InlineData(0x020Bu, 2, WindowsPageMouse.Kind.Down, WindowsPageMouse.Button.Forward)]
    [InlineData(0x020Cu, 1, WindowsPageMouse.Kind.Up, WindowsPageMouse.Button.Back)]
    [InlineData(0x020Cu, 2, WindowsPageMouse.Kind.Up, WindowsPageMouse.Button.Forward)]
    public void XButtons_AreBackAndForward(uint message, int xButton, WindowsPageMouse.Kind kind, WindowsPageMouse.Button button)
    {
        var m = WindowsPageMouse.Decode(message, (nint)(xButton << 16), Point(1, 2))!.Value;
        Assert.Equal((kind, button), (m.Kind, m.Button));
    }

    [Theory]
    [InlineData(0x0u, WindowsPageMouse.Button.None)]
    [InlineData(0x1u, WindowsPageMouse.Button.Left)]
    [InlineData(0x2u, WindowsPageMouse.Button.Right)]
    [InlineData(0x10u, WindowsPageMouse.Button.Middle)]
    [InlineData(0x13u, WindowsPageMouse.Button.Left)]
    [InlineData(0x12u, WindowsPageMouse.Button.Middle)]
    public void AMove_CarriesTheHeldButton_LeftThenMiddleThenRight(uint flags, WindowsPageMouse.Button button)
        => Assert.Equal(button, WindowsPageMouse.Decode(0x0200, (nint)flags, Point(3, 4))!.Value.Button);

    [Fact]
    public void NegativePositions_DuringACapturedDrag_KeepTheirSign()
    {
        var m = WindowsPageMouse.Decode(0x0200, 0x1, Point(-5, -12))!.Value;
        Assert.Equal((-5, -12), (m.ClientX, m.ClientY));
    }

    [Fact]
    public void TheLeave_IsDecoded_TheWheelAndOthersAreNot()
    {
        Assert.Equal(WindowsPageMouse.Kind.Leave, WindowsPageMouse.Decode(0x02A3, 0, 0)!.Value.Kind);
        Assert.True(WindowsPageMouse.IsPageMouseMessage(0x02A3));
        foreach (var other in new uint[] { 0x020A, 0x020E, 0x0100, 0x00A0, 0x0084 })
        {
            Assert.False(WindowsPageMouse.IsPageMouseMessage(other));
            Assert.Null(WindowsPageMouse.Decode(other, 0, 0));
        }
        foreach (var mouse in new uint[] { 0x0200, 0x0201, 0x0209, 0x020B, 0x020D })
            Assert.True(WindowsPageMouse.IsPageMouseMessage(mouse));
    }

    [Fact]
    public void Clicks_CountOnWithinTheTimeAndBox_AndAReleaseCarriesItsPress()
    {
        var c = new ClickCounter();
        Assert.Equal(1, c.Down(MouseButton.Left, 10, 10, 1000, 500, 4, 4));
        Assert.Equal(1, c.Up(MouseButton.Left));
        Assert.Equal(2, c.Down(MouseButton.Left, 11, 9, 1300, 500, 4, 4));
        Assert.Equal(2, c.Up(MouseButton.Left));
        Assert.Equal(3, c.Down(MouseButton.Left, 12, 10, 1700, 500, 4, 4));
    }

    [Fact]
    public void Clicks_StartOver_TooLate_TooFar_OrAnotherButton()
    {
        var c = new ClickCounter();
        c.Down(MouseButton.Left, 10, 10, 1000, 500, 4, 4);
        Assert.Equal(1, c.Down(MouseButton.Left, 10, 10, 1501, 500, 4, 4));
        Assert.Equal(1, c.Down(MouseButton.Left, 13, 10, 1600, 500, 4, 4));
        Assert.Equal(1, c.Down(MouseButton.Left, 13, 13, 1700, 500, 4, 4));
        Assert.Equal(1, c.Down(MouseButton.Right, 13, 13, 1750, 500, 4, 4));
        Assert.Equal(1, c.Up(MouseButton.Left));
    }
}
