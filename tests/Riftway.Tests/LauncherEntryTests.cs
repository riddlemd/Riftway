using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class LauncherEntryTests
{
    [Fact]
    public void AppUri_IsApplicationSchemeAndDesktopName_OrNullWithoutOne()
    {
        Assert.Equal("application://my-app.desktop", LinuxLauncherEntry.AppUri("my-app.desktop"));
        Assert.Null(LinuxLauncherEntry.AppUri(""));
        Assert.Null(LinuxLauncherEntry.AppUri(null));
    }

    [Theory]
    [InlineData(7L, true)]
    [InlineData(1L, true)]
    [InlineData(-1L, true)]
    [InlineData(0L, false)]
    public void BadgeProperties_AreCountAndVisibleUnlessZero(long count, bool visible)
        => Assert.Equal([("count", (object)count), ("count-visible", visible)], LinuxLauncherEntry.BadgeProperties(count));

    [Fact]
    public void BadgeProperties_CountIsInt64()
        => Assert.IsType<long>(LinuxLauncherEntry.BadgeProperties(3)[0].Value);

    [Theory]
    [InlineData(0.5, true)]
    [InlineData(0.0001, true)]
    [InlineData(0.9999, true)]
    [InlineData(0.0, false)]
    [InlineData(1.0, false)]
    [InlineData(-1.0, false)]
    [InlineData(2.0, false)]
    [InlineData(double.NaN, false)]
    public void ProgressProperties_AreTheRawValueAndVisibleOnlyInsideZeroToOne(double progress, bool visible)
    {
        var properties = LinuxLauncherEntry.ProgressProperties(progress);
        Assert.Equal(["progress", "progress-visible"], properties.Select(p => p.Key));
        Assert.Equal(progress, Assert.IsType<double>(properties[0].Value));
        Assert.Equal(visible, Assert.IsType<bool>(properties[1].Value));
    }

    [Theory]
    [InlineData("Riftway.WindowTest", "riftway-windowtest.desktop")]
    [InlineData("My Cool App", "my-cool-app.desktop")]
    [InlineData("  --Café Über!! ", "cafe-uber.desktop")]
    [InlineData("ﬁle Ⅸ", "file-ix.desktop")]
    [InlineData("a__b", "a-b.desktop")]
    public void DefaultDesktopName_IsElectronsSlug(string name, string expected)
        => Assert.Equal(expected, RiftwayApp.DefaultDesktopName(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("日本語")]
    [InlineData("--")]
    public void DefaultDesktopName_FallsBackToTheExecutable(string? name)
        => Assert.Equal(Path.GetFileName(Environment.ProcessPath) + ".desktop", RiftwayApp.DefaultDesktopName(name));
}
