namespace Riftway.Tests;

public sealed class FullScreenableTests
{
    // NativeWindowMac::UpdateZoomButton.
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    public void TheZoomButtonNeedsResizableAndMaximizableOrFullScreenable(bool resizable, bool maximizable, bool fullScreenable, bool enabled)
        => Assert.Equal(enabled, BaseWindow.ZoomButtonEnabled(resizable, maximizable, fullScreenable));

    [Fact]
    public void WindowsAreFullScreenableByDefault() => Assert.True(new BaseWindowOptions().FullScreenable);
}
