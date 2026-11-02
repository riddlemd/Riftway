using Riftway.Cef;

namespace Riftway.Tests;

public sealed class SchemeSwitchTests
{
    [Fact]
    public void Format_ThenParse_RoundTrips()
    {
        CustomScheme[] schemes = [new("app", 89), new("media-v2", 25)];

        Assert.Equal(schemes, SchemeSwitch.Parse(SchemeSwitch.Format(schemes)));
    }

    [Fact]
    public void Format_UsesNameColonOptionsJoinedByCommas()
        => Assert.Equal("app:89,media:25", SchemeSwitch.Format([new("app", 89), new("media", 25)]));

    [Fact]
    public void Format_RejectsAnInvalidName()
        => Assert.Throws<ArgumentException>(() => SchemeSwitch.Format([new("App", 1)]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_OfNothing_IsEmpty(string? value) => Assert.Empty(SchemeSwitch.Parse(value));

    [Theory]
    [InlineData("app")]          // no options
    [InlineData(":89")]          // no name
    [InlineData("App:89")]       // uppercase
    [InlineData("app:x9")]       // options not a number
    [InlineData("app:-1")]       // sign not allowed
    [InlineData("1app:89")]      // must start with a letter
    public void Parse_SkipsAMalformedEntry(string entry)
        => Assert.Equal([new CustomScheme("ok", 1)], SchemeSwitch.Parse($"{entry},ok:1"));

    [Fact]
    public void FromArgs_FindsTheSwitchAmongOtherArguments()
        => Assert.Equal(
            [new CustomScheme("app", 89)],
            SchemeSwitch.FromArgs(["--type=renderer", "--riftway-custom-schemes=app:89", "--lang=en-US"]));

    [Fact]
    public void FromArgs_WithoutTheSwitch_IsEmpty()
        => Assert.Empty(SchemeSwitch.FromArgs(["--type=renderer", "--riftway-custom-schemesX=app:89"]));

    [Theory]
    [InlineData("app", true)]
    [InlineData("my-app+v1.2", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("App", false)]
    [InlineData("9app", false)]
    [InlineData("ap:p", false)]
    [InlineData("ap,p", false)]
    [InlineData("ap p", false)]
    public void IsValidName_FollowsLowercaseRfc3986(string name, bool valid)
        => Assert.Equal(valid, SchemeSwitch.IsValidName(name));
}
