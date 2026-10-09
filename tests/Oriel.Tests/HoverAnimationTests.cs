using Oriel.Cef.Interop;

namespace Oriel.Tests;

public sealed class HoverAnimationTests
{
    private const cef_button_state_t Normal = cef_button_state_t.CEF_BUTTON_STATE_NORMAL;
    private const cef_button_state_t Hovered = cef_button_state_t.CEF_BUTTON_STATE_HOVERED;
    private const cef_button_state_t Pressed = cef_button_state_t.CEF_BUTTON_STATE_PRESSED;
    private const cef_button_state_t Disabled = cef_button_state_t.CEF_BUTTON_STATE_DISABLED;

    [Fact]
    public void Hovering_FadesInOver150Ms_EasingOut()
    {
        var a = new HoverAnimation();
        a.OnStateChanged(Normal, Hovered, 1000);
        Assert.True(a.IsAnimating);
        a.Step(1075);
        Assert.Equal(0.75, a.Value, 9);
        Assert.True(a.IsAnimating);
        a.Step(1150);
        Assert.Equal(1.0, a.Value);
        Assert.False(a.IsAnimating);
    }

    [Fact]
    public void Leaving_FadesOut()
    {
        var a = new HoverAnimation();
        a.OnStateChanged(Normal, Hovered, 0);
        a.Step(150);
        a.OnStateChanged(Hovered, Normal, 200);
        Assert.True(a.IsAnimating);
        a.Step(275);
        Assert.Equal(0.25, a.Value, 9);
        a.Step(350);
        Assert.Equal(0.0, a.Value);
        Assert.False(a.IsAnimating);
    }

    [Fact]
    public void Reversing_MidFade_TakesTheRemainingShareOfTheDuration()
    {
        var a = new HoverAnimation();
        a.OnStateChanged(Normal, Hovered, 0);
        a.Step(75);
        a.OnStateChanged(Hovered, Normal, 75);
        a.Step(75 + 56.25);
        Assert.Equal(0.1875, a.Value, 9);
        a.Step(75 + 112.5);
        Assert.Equal(0.0, a.Value);
        Assert.False(a.IsAnimating);
    }

    [Theory]
    [InlineData(Hovered, Pressed, 0.0)]
    [InlineData(Normal, Pressed, 0.0)]
    [InlineData(Hovered, Disabled, 0.0)]
    [InlineData(Pressed, Hovered, 1.0)]
    [InlineData(Disabled, Hovered, 1.0)]
    public void OtherChanges_JumpWithoutAnimating(cef_button_state_t from, cef_button_state_t to, double value)
    {
        var a = new HoverAnimation();
        a.OnStateChanged(Normal, Hovered, 0);
        a.Step(30);
        a.OnStateChanged(from, to, 40);
        Assert.Equal(value, a.Value);
        Assert.False(a.IsAnimating);
    }

    [Fact]
    public void TheSameState_ChangesNothing()
    {
        var a = new HoverAnimation();
        a.OnStateChanged(Normal, Hovered, 0);
        a.Step(75);
        a.OnStateChanged(Hovered, Hovered, 80);
        Assert.True(a.IsAnimating);
        a.Step(150);
        Assert.Equal(1.0, a.Value);
    }

    [Fact]
    public void LeavingAsTheFadeStarts_StaysAtZero()
    {
        var a = new HoverAnimation();
        a.OnStateChanged(Normal, Hovered, 0);
        a.OnStateChanged(Hovered, Normal, 0);
        a.Step(100);
        Assert.Equal(0.0, a.Value);
    }

    [Fact]
    public void FrameInterval_Is60Hz() => Assert.Equal(TimeSpan.FromMicroseconds(16666), HoverAnimation.FrameInterval);
}
