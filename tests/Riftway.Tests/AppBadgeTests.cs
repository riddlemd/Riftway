using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class AppBadgeTests
{
    [Fact]
    public void BadgeText_FollowsChromiumsBadgeManager()
    {
        Assert.Equal("•", RiftwayApp.BadgeText(null));
        Assert.Equal("1", RiftwayApp.BadgeText(1));
        Assert.Equal("99", RiftwayApp.BadgeText(99));
        Assert.Equal("99+", RiftwayApp.BadgeText(100));
        Assert.Equal("-5", RiftwayApp.BadgeText(-5));
        Assert.Equal("-1,000", RiftwayApp.BadgeText(-1000));
    }

    [Fact]
    public void BadgeDescription_IsChromiumsScreenReaderText()
    {
        Assert.Equal("Unread Notifications", RiftwayApp.BadgeDescription(null));
        Assert.Equal("1 Unread Notification", RiftwayApp.BadgeDescription(1));
        Assert.Equal("2 Unread Notifications", RiftwayApp.BadgeDescription(2));
        Assert.Equal("99 Unread Notifications", RiftwayApp.BadgeDescription(99));
        Assert.Equal("More than 99 unread notifications", RiftwayApp.BadgeDescription(100));
        Assert.Equal("0 Unread Notifications", RiftwayApp.BadgeDescription(0));
        Assert.Equal("-1,000 Unread Notifications", RiftwayApp.BadgeDescription(-1000));
    }

    [Fact]
    public void WindowsBadge_IsADarkCircleWithTransparentCorners()
    {
        if (!OperatingSystem.IsWindows()) return;
        var badge = WindowsBadge.Draw("7");
        Assert.Equal(16 * 16 * 4, badge.Length);
        Assert.Equal(0, Pixel(badge, 0, 0).A);
        Assert.Equal(0, Pixel(badge, 15, 15).A);
        Assert.Equal((0x2D, 0x25, 0x26, 255), Pixel(badge, 1, 8));
        // Anti-aliased edge, clipped to the circle a second time as the taskbar host does.
        Assert.Equal(247, Pixel(badge, 0, 7).A);
    }

    [Fact]
    public void WindowsBadge_CentresWhiteText()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var text in new[] { "1", "42", "99", "99+", "•" })
        {
            var badge = WindowsBadge.Draw(text);
            int left = 16, top = 16, right = -1, bottom = -1;
            for (var y = 0; y < 16; y++)
                for (var x = 0; x < 16; x++)
                {
                    if (Pixel(badge, x, y).R < 200) continue;
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
                }
            Assert.True(right >= 0, text);
            Assert.InRange((left + right + 1) / 2.0, 7, 9);
            Assert.InRange((top + bottom + 1) / 2.0, 7, 9);
        }
    }

    [Fact]
    public void WindowsBadge_KeepsFittedTextInsideTheThreePixelMargin()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var text in new[] { "1", "7", "42", "99" })
        {
            var badge = WindowsBadge.Draw(text);
            int left = 16, top = 16, right = -1, bottom = -1;
            for (var y = 0; y < 16; y++)
                for (var x = 0; x < 16; x++)
                {
                    if (Pixel(badge, x, y).R <= 0x28) continue;
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
                }
            Assert.True(right - left + 1 <= 10 && bottom - top + 1 <= 10, $"{text}: {right - left + 1}x{bottom - top + 1}");
            // The largest size that fits: one size up would not.
            Assert.True(right - left + 1 >= 9 || bottom - top + 1 >= 9, $"{text}: {right - left + 1}x{bottom - top + 1}");
        }
    }

    [Fact]
    public void WindowsBadge_PicksTheLargestFontThatFitsFrom24DownTo7()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(24, WindowsBadge.FontSize("•"));
        Assert.Equal(7, WindowsBadge.FontSize("-1,000"));
        Assert.InRange(WindowsBadge.FontSize("1"), 8, 23);
        Assert.True(WindowsBadge.FontSize("1") > WindowsBadge.FontSize("42"));
        Assert.True(WindowsBadge.FontSize("42") > WindowsBadge.FontSize("99+"));
    }

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] bgra, int x, int y)
    {
        var i = (y * 16 + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
    }
}
