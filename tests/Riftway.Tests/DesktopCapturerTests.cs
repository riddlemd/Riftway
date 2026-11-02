using System.Text;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class DesktopCapturerTests
{
    [Theory]
    [InlineData(true, 69733378L, 0L, false, "screen:69733378:0")]
    [InlineData(false, 4242L, 4242L, false, "window:4242:4242")]
    [InlineData(false, -1L, 0L, true, "window:-1:0:s")]
    public void FormatId_IsDesktopMediaIdToString(bool screen, long id, long windowId, bool session, string expected)
        => Assert.Equal(expected, DesktopMedia.FormatId(new DesktopMediaId(screen, id, windowId, session)));

    [Theory]
    [InlineData("screen:1:0", true, 1L, 0L, false)]
    [InlineData("window:123:7", false, 123L, 7L, false)]
    [InlineData(" window : 5 : 0 ", false, 5L, 0L, false)]
    [InlineData("window:+5:0", false, 5L, 0L, false)]
    [InlineData("screen:-1:0", true, -1L, 0L, false)]
    [InlineData("window:9:0:s", false, 9L, 0L, true)]
    public void TryParseId_AcceptsChromiumsForms(string text, bool screen, long id, long windowId, bool session)
    {
        Assert.True(DesktopMedia.TryParseId(text, out var parsed));
        Assert.Equal(new DesktopMediaId(screen, id, windowId, session), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("screen:1")]
    [InlineData("screen:1:0:s:x")]
    [InlineData("tab:1:0")]
    [InlineData("screen:x:0")]
    [InlineData("screen:1:")]
    [InlineData("screen:1:0:t")]
    [InlineData("screen:9223372036854775808:0")]
    [InlineData("screen:1 2:0")]
    [InlineData("screen:-:0")]
    public void TryParseId_RejectsWhatChromiumGivesTypeNone(string? text) => Assert.False(DesktopMedia.TryParseId(text, out _));

    [Fact]
    public void TryParseId_AcceptsTheExtremes()
    {
        Assert.True(DesktopMedia.TryParseId("screen:-9223372036854775808:9223372036854775807", out var parsed));
        Assert.Equal(new DesktopMediaId(true, long.MinValue, long.MaxValue), parsed);
    }

    [Theory]
    [InlineData(150, 150, 1920, 1080, 150, 84)]
    [InlineData(150, 150, 1080, 1920, 84, 150)]
    [InlineData(150, 150, 800, 800, 150, 150)]
    [InlineData(320, 240, 1366, 768, 320, 180)]
    [InlineData(100, 100, 3, 2, 100, 67)]
    [InlineData(100, 50, 2, 2, 50, 50)]
    [InlineData(150, 150, 0, 100, 0, 0)]
    [InlineData(150, 150, 100, 0, 0, 0)]
    public void LetterboxSize_FitsTheAspectIntoTheBox(int boxW, int boxH, int srcW, int srcH, int w, int h)
        => Assert.Equal((w, h), DesktopMedia.LetterboxSize(boxW, boxH, srcW, srcH));

    [Fact]
    public void ScreenName_EntireScreenAloneElseNumbered()
    {
        Assert.Equal("Entire screen", DesktopMedia.ScreenName(0, 1));
        Assert.Equal(["Screen 1", "Screen 2"], [DesktopMedia.ScreenName(0, 2), DesktopMedia.ScreenName(1, 2)]);
    }

    [Fact]
    public void ScaleThumbnail_LetterboxesAndMakesEveryPixelOpaque()
    {
        var frame = new CaptureBitmap(4, 2, Enumerable.Repeat((byte)10, 4 * 2 * 4).ToArray());
        var thumb = DesktopMedia.ScaleThumbnail(frame, 2, 2)!;
        Assert.Equal((2, 1), (thumb.Width, thumb.Height));
        Assert.Equal([10, 10, 10, 255, 10, 10, 10, 255], thumb.Bgra);
    }

    [Fact]
    public void ScaleThumbnail_NullForAnEmptyFrame() => Assert.Null(DesktopMedia.ScaleThumbnail(new CaptureBitmap(0, 0, []), 150, 150));

    [Fact]
    public void ScaleBilinear_HalvingSamplesBetweenThePixels()
    {
        var src = new CaptureBitmap(2, 1, [0, 0, 0, 0, 200, 100, 50, 255]);
        Assert.Equal([100, 50, 25, 127], DesktopMedia.ScaleBilinear(src, 1, 1).Bgra);
    }

    [Fact]
    public void ScaleBilinear_UpscaleKeepsTheEnds()
    {
        var src = new CaptureBitmap(2, 1, [0, 0, 0, 0, 200, 200, 200, 200]);
        var up = DesktopMedia.ScaleBilinear(src, 3, 1).Bgra;
        Assert.Equal((0, 100, 200), (up[0], up[4], up[8]));
    }

    [Theory]
    [InlineData(new[] { "screen" }, false, true)]
    [InlineData(new[] { "window" }, true, false)]
    [InlineData(new[] { "window", "screen" }, true, true)]
    [InlineData(new[] { "tab", "Screen" }, false, false)]
    [InlineData(new string[0], false, false)]
    public void Kinds_FollowElectronsIncludes(string[] types, bool window, bool screen)
        => Assert.Equal((window, screen), DesktopCapturer.Kinds(types));

    [Fact]
    public async Task GetSourcesAsync_WithoutTypes_IsInvalidOptions()
    {
        var e = await Assert.ThrowsAsync<ArgumentException>(() => DesktopCapturer.GetSourcesAsync(new SourcesOptions()));
        Assert.Equal("Invalid options", e.Message);
    }

    [Fact]
    public void DesktopCapturerSource_IsADisplayMediaSource()
    {
        DisplayMediaSource video = new DesktopCapturerSource("screen:1:0", "Entire screen", NativeImage.CreateEmpty(), "1", null);
        Assert.Equal(new DisplayMediaSource("screen:1:0", "Entire screen"), video);
    }

    private static MacWindowEntry Mac(uint id, string? title, int pid = 1, int layer = 0, double w = 400, double h = 300, string owner = "App", bool onScreen = true)
        => new(id, title, owner, pid, layer, 0, 0, w, h, onScreen);

    [Theory]
    [InlineData(0, 40, 40, true)]
    [InlineData(1, 400, 300, false)]
    [InlineData(0, 39.9, 300, false)]
    [InlineData(0, 300, 39.9, false)]
    public void IsMacShareable_LayerZeroAndAtLeast40Points(int layer, double w, double h, bool expected)
        => Assert.Equal(expected, DesktopMedia.IsMacShareable(Mac(1, "t", layer: layer, w: w, h: h)));

    [Fact]
    public void MacScreenCaptureKitWindows_SortsByCGWindowListAndDropsUnshareable()
    {
        var list = DesktopMedia.MacScreenCaptureKitWindows([Mac(1, "a"), Mac(2, "b", pid: 2), Mac(3, "menu", layer: 25)], [2, 3, 1], []);
        Assert.Equal([2u, 1u], list.Select(w => w.Id));
    }

    [Fact]
    public void MacScreenCaptureKitWindows_KeepsScreenCaptureKitsOrderWithoutZOrder()
        => Assert.Equal([1u, 2u], DesktopMedia.MacScreenCaptureKitWindows([Mac(1, "a"), Mac(2, "b")], null, []).Select(w => w.Id));

    [Fact]
    public void MacScreenCaptureKitWindows_UntitledKeptOnlyAloneOrFullScreen_NamedAfterItsApp()
    {
        var windows = new[]
        {
            Mac(1, "", pid: 1, owner: "Solo"),
            Mac(2, null, pid: 2, owner: "Busy"),
            Mac(3, "Titled", pid: 2, owner: "Busy"),
            new MacWindowEntry(4, "", "Full", 2, 0, 0, 0, 1440, 900),
        };
        var list = DesktopMedia.MacScreenCaptureKitWindows(windows, null, [(0, 0, 1440, 900)]);
        Assert.Equal([(1u, "Solo"), (3u, "Titled"), (4u, "Full")], list.Select(w => (w.Id, w.Title!)));
    }

    [Fact]
    public void MacCoreGraphicsWindows_WebrtcRules()
    {
        var windows = new[]
        {
            Mac(1, "Doc", pid: 1),
            Mac(2, "menu", pid: 1, layer: 24),
            Mac(3, "hidden", pid: 1, onScreen: false),
            new MacWindowEntry(4, "StatusIndicator", "Window Server", 9, 0, 0, 0, 10, 10),
            Mac(0, "no id"),
        };
        Assert.Equal([1u], DesktopMedia.MacCoreGraphicsWindows(windows).Select(w => w.Id));
    }

    [Fact]
    public void MacCoreGraphicsWindows_UntitledUntilTheAppShowsATitledWindow()
    {
        var windows = new[]
        {
            Mac(1, "", pid: 5, owner: "Five"),
            Mac(2, null, pid: 5, owner: "Five"),
            Mac(3, "Real", pid: 5),
            Mac(4, "", pid: 5, owner: "Five"),
            Mac(5, "", pid: 6, owner: ""),
            Mac(6, "", pid: 7, owner: "Seven"),
        };
        Assert.Equal([(3u, "Real"), (6u, "Seven")], DesktopMedia.MacCoreGraphicsWindows(windows).Select(w => (w.Id, w.Title!)));
    }

    [Fact]
    public void MacCoreGraphicsWindows_TitledFirstKeepsALaterUntitledOne()
        => Assert.Equal([1u, 2u], DesktopMedia.MacCoreGraphicsWindows([Mac(1, "Real", pid: 5), Mac(2, "", pid: 5, owner: "Five")]).Select(w => w.Id));

    [Fact]
    public void ScreenshotSize_BoxSideThenRoundedAspect()
    {
        Assert.Equal((150, 84), DesktopMedia.MacScreenshotSize(150, 150, 1920, 1080));
        Assert.Equal((84, 150), DesktopMedia.MacScreenshotSize(150, 150, 1080, 1920));
        Assert.Equal((150, 150), DesktopMedia.MacScreenshotSize(150, 150, 500, 500));
    }

    [Theory]
    [InlineData(new long[] { 7, 8 }, false)]
    [InlineData(new long[] { 9 }, true)]
    [InlineData(null, true)]
    public void IsX11DesktopElement_WindowTypeDecides(long[]? types, bool expected)
        => Assert.Equal(expected, DesktopMedia.IsX11DesktopElement(types, 8, "gnome-panel"));

    [Theory]
    [InlineData("gnome-panel", true)]
    [InlineData("desktop_window", true)]
    [InlineData("xterm", false)]
    [InlineData(null, false)]
    public void IsX11DesktopElement_ClassHintWithoutWindowType(string? resName, bool expected)
        => Assert.Equal(expected, DesktopMedia.IsX11DesktopElement(Array.Empty<long>(), 8, resName));

    [Fact]
    public void PickX11Icon_TakesTheLargestCompleteIcon_Premultiplied()
    {
        uint[] data = [1, 1, 0xFF0000FF, 2, 1, 0x80FF0000, 0xFF00FF00];
        var icon = DesktopMedia.PickX11Icon(data)!;
        Assert.Equal((2, 1), (icon.Width, icon.Height));
        Assert.Equal([0, 0, 128, 128, 0, 255, 0, 255], icon.Bgra);
    }

    [Fact]
    public void PickX11Icon_KeepsAnEarlierLargerIcon()
    {
        uint[] data = [2, 1, 0xFF000001, 0xFF000002, 1, 1, 0xFF000003, 0];
        Assert.Equal((2, 1), DesktopMedia.PickX11Icon(data) is { } i ? (i.Width, i.Height) : (0, 0));
    }

    [Fact]
    public void PickX11Icon_IgnoresARunThatReachesTheEnd()
    {
        // Chromium wants i + 1 + w*h strictly inside: a run ending exactly at the end is skipped.
        uint[] data = [1, 1, 0xFF0000FF, 2, 2, 1, 2, 3];
        Assert.Equal((1, 1), DesktopMedia.PickX11Icon(data) is { } i ? (i.Width, i.Height) : (0, 0));
    }

    [Fact]
    public void PickX11Icon_NullWithoutIcons()
    {
        Assert.Null(DesktopMedia.PickX11Icon([]));
        Assert.Null(DesktopMedia.PickX11Icon([0, 5]));
    }

    private static DesktopMedia.WindowsWindowFacts Win(bool visible = true, bool iconic = false, bool owned = false, long exStyle = 0, bool responding = true,
        bool ours = false, string title = "Title", string cls = "Notepad", bool onDesktop = true, bool cloaked = false)
        => new(visible, iconic, owned, exStyle, responding, ours, title, cls, onDesktop, cloaked);

    public static TheoryData<string, DesktopMedia.WindowsWindowFacts, bool> WindowsCases => new()
    {
        { "plain", Win(), true },
        { "invisible", Win(visible: false), false },
        { "minimized", Win(iconic: true), false },
        { "owned", Win(owned: true), false },
        { "owned app window", Win(owned: true, exStyle: 0x00040000), true },
        { "hung", Win(responding: false), false },
        { "this process", Win(ours: true), false },
        { "untitled", Win(title: ""), false },
        { "no class", Win(cls: ""), false },
        { "Progman", Win(cls: "Progman"), false },
        { "Button", Win(cls: "Button"), false },
        { "other desktop", Win(onDesktop: false), false },
        { "cloaked", Win(cloaked: true), false },
    };

    [Theory]
    [MemberData(nameof(WindowsCases))]
    public void IsWindowsCapturable_WebrtcRules(string name, DesktopMedia.WindowsWindowFacts facts, bool expected)
    {
        _ = name;
        Assert.Equal(expected, DesktopMedia.IsWindowsCapturable(facts));
    }

    public static TheoryData<string, DesktopMedia.WindowsWindowFacts, bool> OwnCases => new()
    {
        { "ours", Win(ours: true), true },
        { "ours, hung and cloaked", Win(ours: true, responding: false, cloaked: true, cls: ""), true },
        { "other process", Win(), false },
        { "invisible", Win(ours: true, visible: false), false },
        { "minimized", Win(ours: true, iconic: true), false },
        { "owned", Win(ours: true, owned: true), false },
        { "owned app window", Win(ours: true, owned: true, exStyle: 0x00040000), true },
        { "untitled", Win(ours: true, title: ""), false },
    };

    [Theory]
    [MemberData(nameof(OwnCases))]
    public void IsWindowsOwnCapturable_ChromiumsCollector(string name, DesktopMedia.WindowsWindowFacts facts, bool expected)
    {
        _ = name;
        Assert.Equal(expected, DesktopMedia.IsWindowsOwnCapturable(facts));
    }

    [Theory]
    [InlineData(false, 10, 500)]
    [InlineData(false, 600, 500)]
    [InlineData(true, 10, 11)]
    [InlineData(true, 600, 601)]
    public void WindowsTitleBuffer_WebrtcCapsOthers_OwnReadInFull(bool ours, int length, int expected)
        => Assert.Equal(expected, DesktopMedia.WindowsTitleBuffer(ours, length));

    [Fact]
    public void MergeByZOrder_InterleavesByZOrder_FirstListWinsTies()
    {
        var a = new[] { new CaptureSource(3, "a3"), new CaptureSource(1, "a1") };
        var b = new[] { new CaptureSource(2, "b2"), new CaptureSource(1, "b1") };
        Assert.Equal(["a1", "b2", "a3"], DesktopMedia.MergeByZOrder(a, b, [1, 2, 3, 4]).Select(s => s.Title));
        Assert.Equal(["a3", "a1", "b2", "b1"], DesktopMedia.MergeByZOrder(a, b, null).Select(s => s.Title));
    }

    [Theory]
    [InlineData("a", 291415938u)]
    [InlineData("ab", 1366002500u)]
    [InlineData("abc", 3535673738u)]
    [InlineData("abcd", 3671636187u)]
    [InlineData("abcde", 1374488366u)]
    [InlineData("DELL U2720Q", 27104343u)]
    public void SuperFastHash_MatchesChromiumsC(string text, uint expected)
        => Assert.Equal(expected, DesktopMedia.SuperFastHash(Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void SuperFastHash_SignExtendsTrailingBytes_AndEmptyIsZero()
    {
        Assert.Equal(3160264424u, DesktopMedia.SuperFastHash([0xe9, 0xff, 0x80]));
        Assert.Equal(0u, DesktopMedia.SuperFastHash([]));
    }

    private static byte[] Edid(string? name, int nameSlot = 1)
    {
        var edid = new byte[128];
        edid[8] = 0x10;
        edid[9] = 0xAC;
        if (name is not null)
        {
            var o = 54 + nameSlot * 18;
            edid[o + 3] = 0xfc;
            var text = Encoding.ASCII.GetBytes(name.PadRight(13));
            text.CopyTo(edid, o + 5);
        }
        return edid;
    }

    [Fact]
    public void EdidDisplayName_ReadsTheMonitorNameDescriptor()
    {
        Assert.Equal("DELL U2720Q", DesktopMedia.EdidDisplayName(Edid("DELL U2720Q\n")));
        Assert.Equal("", DesktopMedia.EdidDisplayName(Edid(null)));
        Assert.Equal("A B", DesktopMedia.EdidDisplayName(Edid("A\u0001B")));
        Assert.Equal("", DesktopMedia.EdidDisplayName(Edid("X", nameSlot: 3)[..125]));
    }

    [Fact]
    public void EdidIndexBasedDisplayId_PacksManufacturerNameHashAndIndex()
        => Assert.Equal((0x10ACL << 40) | (27104343L << 8) | 3, DesktopMedia.EdidIndexBasedDisplayId(Edid("DELL U2720Q\n"), 3));

    [Fact]
    public void EdidIndexBasedDisplayId_ZeroWithoutEdid() => Assert.Equal(0, DesktopMedia.EdidIndexBasedDisplayId([], 0));

    [Fact]
    public void X11DisplayId_TruncatesTo32BitsAsElectronsMap()
        => Assert.Equal(unchecked((uint)((27104343L << 8) | 3)), DesktopMedia.X11DisplayId(Edid("DELL U2720Q\n"), 3, 7));

    [Theory]
    [InlineData(0x100u, 4, 4u)]
    [InlineData(0u, 2, 2u)]
    public void X11DisplayId_FallsBackToTheOutputsPlace(uint output, int index, uint expected)
        => Assert.Equal(expected, DesktopMedia.X11DisplayId(output == 0 ? [] : Edid("DELL U2720Q\n"), output, index));

    [Fact]
    public void WindowsDisplayId_HashesThePathTargetElseTheDevice()
    {
        Assert.Equal(2387232039L, DesktopMedia.WindowsDisplayId(@"\\.\DISPLAY1", (1, 0, 2)));
        Assert.Equal(2528732444L, DesktopMedia.WindowsDisplayId(@"\\.\DISPLAY1", null));
    }

    [Fact]
    public void Mac_EnumeratesDisplaysAndWindowsWithoutAsking()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // Read-only CoreGraphics calls: no prompt, no window. Titles need screen-recording access, so only shape is checked.
        _ = MacDesktopCapture.HasScreenCaptureAccess();
        var displays = MacDesktopCapture.ActiveDisplays();
        Assert.NotEmpty(displays);
        Assert.All(displays, id => Assert.NotEqual(0u, id));
        var windows = MacDesktopCapture.CoreGraphicsWindows();
        Assert.All(windows, w => Assert.NotEqual(0u, w.Id));
        Assert.All(DesktopMedia.MacCoreGraphicsWindows(windows), w => Assert.False(string.IsNullOrEmpty(w.Title)));
    }
}
