using Oriel.Cef.Interop;

namespace Oriel.Tests;

public sealed class LoadTests
{
    [Theory]
    [InlineData("https://a.test/page", "https://a.test/page#x", true)]
    [InlineData("https://a.test/page#x", "https://a.test/page#y", true)]
    [InlineData("https://a.test/page?q=1", "https://a.test/page?q=1#x", true)]
    [InlineData("https://a.test/page#x", "https://a.test/page", false)]
    [InlineData("https://a.test/page", "https://a.test/page", false)]
    [InlineData("https://a.test/page?q=1", "https://a.test/page?q=2#x", false)]
    [InlineData("https://a.test/page", "https://a.test/other#x", false)]
    [InlineData("about:blank", "https://a.test/page#x", false)]
    [InlineData("", "https://a.test/page#x", false)]
    public void IsSameDocument_OnlyForAFragmentOnTheSameUrl(string current, string target, bool same)
        => Assert.Equal(same, WebContents.IsSameDocument(current, target));

    [Fact]
    public void LoadFailedException_CarriesTheDetails()
    {
        var e = new LoadFailedException(-6, "ERR_FILE_NOT_FOUND", "file:///missing.html");
        Assert.Equal((-6, "ERR_FILE_NOT_FOUND", "file:///missing.html"), (e.ErrorCode, e.ErrorDescription, e.ValidatedUrl));
        Assert.Equal("ERR_FILE_NOT_FOUND (-6) loading 'file:///missing.html'", e.Message);
    }
}

public sealed class ContextMenuMediaFlagsTests
{
    [Fact]
    public void From_MapsEveryFlagToItsOwnProperty()
    {
        var pairs = new (cef_context_menu_media_state_flags_t Flag, Func<ContextMenuMediaFlags, bool> Read)[]
        {
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_IN_ERROR, f => f.InError),
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_PAUSED, f => f.IsPaused),
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_MUTED, f => f.IsMuted),
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_LOOP, f => f.IsLooping),
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CAN_SAVE, f => f.CanSave),
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_HAS_AUDIO, f => f.HasAudio),
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CAN_TOGGLE_CONTROLS, f => f.CanToggleControls),
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CONTROLS, f => f.IsControlsVisible),
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CAN_PRINT, f => f.CanPrint),
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CAN_ROTATE, f => f.CanRotate),
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CAN_PICTURE_IN_PICTURE, f => f.CanShowPictureInPicture),
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_PICTURE_IN_PICTURE, f => f.IsShowingPictureInPicture),
            (cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_CAN_LOOP, f => f.CanLoop),
        };
        foreach (var (flag, read) in pairs)
        {
            var flags = ContextMenuMediaFlags.From(flag);
            // Exactly one property is set, and it is this flag's.
            Assert.True(read(flags), flag.ToString());
            Assert.Equal(1, pairs.Count(p => p.Read(flags)));
        }
        Assert.Equal(0, pairs.Count(p => p.Read(ContextMenuMediaFlags.From(cef_context_menu_media_state_flags_t.CM_MEDIAFLAG_NONE))));
    }
}

public sealed class UserAgentTests
{
    [Fact]
    public void BuildUserAgent_IsElectronsFormat()
    {
        var platform = OperatingSystem.IsMacOS() ? "Macintosh; Intel Mac OS X 10_15_7" : OperatingSystem.IsWindows() ? "Windows NT 10.0; Win64; x64" : "X11; Linux x86_64";
        Assert.Equal($"Mozilla/5.0 ({platform}) AppleWebKit/537.36 (KHTML, like Gecko) MyApp/1.2.3 Chrome/154.0.8037.94 Oriel/0.9.0 Safari/537.36",
            OrielApp.BuildUserAgent("My App", "1.2.3", "154.0.8037.94", "0.9.0"));
    }
}
