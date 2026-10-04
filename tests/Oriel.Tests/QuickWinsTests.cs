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
