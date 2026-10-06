namespace Oriel.Tests;

public sealed class CssColorTests
{
    [Theory]
    [InlineData("#F00", 0xFFFF0000u)]
    [InlineData("#abc", 0xFFAABBCCu)]
    [InlineData("#F008", 0x88FF0000u)]
    [InlineData("#123456", 0xFF123456u)]
    [InlineData("#12345678", 0x78123456u)]
    [InlineData("#aBcDeF", 0xFFABCDEFu)]
    public void Hex_ExpandsShortForms_AndTakesAlphaLast(string css, uint argb)
    {
        Assert.True(CssColor.TryParse(css, out var parsed));
        Assert.Equal(argb, parsed);
    }

    [Theory]
    [InlineData("rgb(255, 0, 0)", 0xFFFF0000u)]
    [InlineData("rgb(1,2,3)", 0xFF010203u)]
    [InlineData("rgb(1,\t2,\n3)", 0xFF010203u)]
    [InlineData("rgb(007, 0, 0255)", 0xFF0700FFu)]
    [InlineData("rgba(10, 20, 30, 0.5)", 0x7F0A141Eu)]
    [InlineData("rgba(10, 20, 30, 1.)", 0xFF0A141Eu)]
    [InlineData("rgba(10, 20, 30, .25)", 0x3F0A141Eu)]
    [InlineData("rgba(10, 20, 30, 0)", 0x000A141Eu)]
    public void Rgb_TakesIntegerChannels_AndAlphaZeroToOneTruncated(string css, uint argb)
    {
        Assert.True(CssColor.TryParse(css, out var parsed));
        Assert.Equal(argb, parsed);
    }

    [Theory]
    [InlineData("hsl(0, 100%, 50%)", 0xFFFF0000u)]
    [InlineData("hsl(120, 100%, 25%)", 0xFF008000u)]
    [InlineData("hsl(240,100%,50%)", 0xFF0000FFu)]
    [InlineData("hsl(-120, 100%, 50%)", 0xFF0000FFu)]
    [InlineData("hsl(-330, 100%, 75%)", 0xFFFFBF80u)]
    [InlineData("hsl(360, 100%, 50%)", 0xFFFF0000u)]
    [InlineData("hsl(120.9, 100%, 25%)", 0xFF008000u)]
    [InlineData("hsl(0, 0%, 50%)", 0xFF808080u)]
    [InlineData("hsl(0, 0%, 30%)", 0xFF4D4D4Du)]
    [InlineData("hsl(20, 100%, 50%)", 0xFFFF5500u)]
    [InlineData("hsl(20, 100%, 75%)", 0xFFFFAA80u)]
    [InlineData("hsl(330, 100%, 50%)", 0xFFFF007Fu)]
    [InlineData("hsl(0, 150%, 50%)", 0xFFFF0000u)]
    [InlineData("hsl(20, 150%, 50%)", 0xFFFF5500u)]
    [InlineData("hsl(0, 100%, 150%)", 0xFFFFFFFFu)]
    [InlineData("hsla(0, 100%, 50%, 0.5)", 0x7FFF0000u)]
    [InlineData("hsla(0, 100%, 50%, 2)", 0xFFFF0000u)]
    [InlineData("hsl(99999999999, 100%, 50%)", 0xFF0022FFu)]
    public void Hsl_TruncatesTheHue_ClampsTheRest_AsChromium(string css, uint argb)
    {
        Assert.True(CssColor.TryParse(css, out var parsed));
        Assert.Equal(argb, parsed);
    }

    [Theory]
    [InlineData("red", 0xFFFF0000u)]
    [InlineData("lightgrey", 0xFFD3D3D3u)]
    [InlineData("darkgray", 0xFFA9A9A9u)]
    [InlineData("yellowgreen", 0xFF9ACD32u)]
    [InlineData("aliceblue", 0xFFF0F8FFu)]
    [InlineData("red\0junk", 0xFFFF0000u)]
    public void Named_AreSkiasExactLowercaseNames(string css, uint argb)
    {
        Assert.True(CssColor.TryParse(css, out var parsed));
        Assert.Equal(argb, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#123456789")]
    [InlineData("#GGG")]
    [InlineData("#red")]
    [InlineData("rgb(256, 0, 0)")]
    [InlineData("rgb(0%, 0%, 0%)")]
    [InlineData("rgb( 1, 2, 3)")]
    [InlineData("rgb(1 ,2,3)")]
    [InlineData("rgb(1,2,3,0.5)")]
    [InlineData("rgba(1,2,3)")]
    [InlineData("rgba(1,2,3,1.5)")]
    [InlineData("rgba(1,2,3,.)")]
    [InlineData("rgba(1,2,3,1.2.3)")]
    [InlineData("rgb(1,2,-3)")]
    [InlineData("rgb(1,2,٣)")]
    [InlineData("rgb(٣,2,3)")]
    [InlineData("rgb(1,\v2,3)")]
    [InlineData("rgb(1, 2,3)")]
    [InlineData("rgbz")]
    [InlineData("RGB(1,2,3)")]
    [InlineData("hsl(0, 100, 50%)")]
    [InlineData("hsl(0, 100%, 50%")]
    [InlineData("hsl(-., 100%, 50%)")]
    [InlineData("hsl(0, -1%, 50%)")]
    [InlineData("hsla(0, 100%, 50%)")]
    [InlineData("hslred")]
    [InlineData("Red")]
    [InlineData("lightgray")]
    [InlineData("transparent")]
    [InlineData("rebeccapurple")]
    [InlineData(" red")]
    public void Rejects_WhatChromiumRejects(string? css) => Assert.False(CssColor.TryParse(css, out _));

    [Fact]
    public void Hsl_NumbersOver200Characters_Fail_AfterLeadingZerosCollapse()
    {
        Assert.False(CssColor.TryParse($"hsl({new string('1', 201)}, 100%, 50%)", out _));
        Assert.True(CssColor.TryParse($"hsl({new string('1', 200)}, 100%, 50%)", out var big));
        Assert.Equal(0xFF0022FFu, big);
        Assert.True(CssColor.TryParse($"hsl({new string('0', 300)}120, 100%, 25%)", out var padded));
        Assert.Equal(0xFF008000u, padded);
    }
}
