namespace Riftway.Tests;

// Electron v44's NativeWindowMac::SetKiosk.
public sealed class MacKioskTests
{
    private readonly List<string> _log = [];
    private nuint _current = 7;

    private MacKiosk Kiosk() => new(() => _current, o => { _log.Add($"options {o}"); _current = o; });

    private void FullScreen(bool on) => _log.Add($"fullscreen {on}");

    [Fact]
    public void TheOptionsAreElectrons() => Assert.Equal((nuint)506, MacKiosk.Options);

    [Fact]
    public void EnteringSavesTheOptionsSetsKiosksAndGoesFullScreen()
    {
        var kiosk = Kiosk();
        kiosk.Set(true, isFullScreen: false, FullScreen);
        Assert.True(kiosk.IsKiosk);
        Assert.Equal(["options 506", "fullscreen True"], _log);
        kiosk.Set(true, isFullScreen: true, FullScreen);
        Assert.Equal(2, _log.Count);
    }

    [Fact]
    public void LeavingLeavesFullScreenFirstThenPutsTheOptionsBack()
    {
        var kiosk = Kiosk();
        kiosk.Set(true, isFullScreen: false, FullScreen);
        _log.Clear();
        kiosk.Set(false, isFullScreen: true, FullScreen);
        Assert.False(kiosk.IsKiosk);
        Assert.Equal(["fullscreen False", "options 7"], _log);
        kiosk.Set(false, isFullScreen: false, FullScreen);
        Assert.Equal(2, _log.Count);
    }

    [Fact]
    public void WithSimpleFullScreenKiosksOptionsComeAfterIt_AndLeavingStillEndsOnTheSavedOnes()
    {
        var kiosk = Kiosk();
        kiosk.Set(true, isFullScreen: false, FullScreen, optionsAfterFullScreen: true);
        Assert.Equal(["fullscreen True", "options 506"], _log);
        _log.Clear();
        kiosk.Set(false, isFullScreen: true, FullScreen, optionsAfterFullScreen: true);
        Assert.Equal(["fullscreen False", "options 7"], _log);
    }

    [Fact]
    public void AWindowAlreadyFullScreenStaysFullScreenAfterKiosk()
    {
        var kiosk = Kiosk();
        kiosk.Set(true, isFullScreen: true, FullScreen);
        kiosk.Set(false, isFullScreen: true, FullScreen);
        Assert.Equal(["options 506", "options 7"], _log);
    }
}
