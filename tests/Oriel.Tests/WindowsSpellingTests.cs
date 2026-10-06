using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class WindowsSpellingTests
{
    private static readonly string[] Installed = ["en-US", "fr-FR", "fr-CA"];

    [Fact]
    public void WindowsTag_MatchesTheSameTagIgnoringCase() => Assert.Equal("en-US", WindowsSpelling.WindowsTag("EN-us", Installed));

    [Fact]
    public void WindowsTag_MapsALanguageWithoutRegionToTheFirstInstalledRegion() => Assert.Equal("fr-FR", WindowsSpelling.WindowsTag("fr", Installed));

    [Fact]
    public void WindowsTag_IsNullForARegionNotInstalled() => Assert.Null(WindowsSpelling.WindowsTag("en-GB", Installed));

    [Fact]
    public void WindowsTag_IsNullForALanguageNotInstalled() => Assert.Null(WindowsSpelling.WindowsTag("de", Installed));

    [Fact]
    public void Interleave_TakesOneFromEachLanguageInTurnWithoutRepeats()
        => Assert.Equal(["a", "x", "b", "c"], WindowsSpelling.Interleave([["a", "b", "c"], ["x", "a"]]));

    [Fact]
    public void Interleave_StopsAtFive()
        => Assert.Equal(["1", "2", "3", "4", "5"], WindowsSpelling.Interleave([["1", "2", "3", "4", "5", "6"]]));

    [Fact]
    public void Interleave_OfNoLanguagesIsEmpty() => Assert.Empty(WindowsSpelling.Interleave([]));
}
