namespace Riftway.Tests;

public sealed class WindowsFrameTests
{
    private const uint Caption = 0x00C00000, Thick = 0x00040000, Min = 0x00020000, Max = 0x00010000;

    [Theory]
    [InlineData(true, true, true, true, Caption | Thick | Min | Max)]
    [InlineData(false, true, true, true, Caption | Min)]
    [InlineData(true, false, true, true, Caption | Thick | Max)]
    [InlineData(true, true, false, true, Caption | Thick | Min)]
    [InlineData(true, true, true, false, Min | Max)]
    [InlineData(false, false, false, false, 0u)]
    public void FramelessStyle_IsElectrons(bool resizable, bool minimizable, bool maximizable, bool thickFrame, uint style)
        => Assert.Equal(style, WindowsFrame.FramelessStyle(resizable, minimizable, maximizable, thickFrame));

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, false)]
    public void CanResize_FramedNeedsThickFrame(bool hasFrame, bool resizable, bool thickFrame, bool canResize)
        => Assert.Equal(canResize, WindowsFrame.CanResize(hasFrame, resizable, thickFrame));

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void RoundedCorners_NeedThickFrame(bool thickFrame, bool option, bool rounded)
        => Assert.Equal(rounded, WindowsFrame.RoundedCorners(thickFrame, option));

    [Fact]
    public void FromOption_MapsBoolStringAndUnparsable()
    {
        Assert.Null(WindowsFrame.FromOption(null));
        Assert.Equal(new WindowsFrame.Accent(true, null), WindowsFrame.FromOption(true));
        Assert.Equal(new WindowsFrame.Accent(false, null), WindowsFrame.FromOption(false));
        Assert.Equal(new WindowsFrame.Accent(true, 0xFF102030), WindowsFrame.FromOption("#102030"));
        Assert.Null(WindowsFrame.FromOption("not a colour"));
    }

    private const uint System = 0xFF0078D4, SystemRef = 0x00D47800, Default = 0xFFFFFFFF;

    [Theory]
    // An explicit colour wins whatever the rest says, alpha dropped.
    [InlineData("#112233", false, false, 0x00332211u)]
    [InlineData("rgba(255,0,0,0.5)", true, true, 0x000000FFu)]
    // true: the system colour, active or not.
    [InlineData("true", false, false, SystemRef)]
    // false: DWM's default.
    [InlineData("false", true, true, Default)]
    // Following the system: only when it shows accents and the window is active.
    [InlineData(null, true, true, SystemRef)]
    [InlineData(null, false, true, Default)]
    [InlineData(null, true, false, Default)]
    public void BorderColor_IsUpdateWindowAccentColor(string? option, bool active, bool systemShows, uint colorRef)
    {
        AccentColor? accent = option switch { null => null, "true" => true, "false" => false, _ => option };
        Assert.Equal(colorRef, WindowsFrame.BorderColor(WindowsFrame.FromOption(accent), active, systemShows, System));
    }

    [Fact]
    public void BorderColor_WithoutASystemColour_IsDefault()
    {
        Assert.Equal(Default, WindowsFrame.BorderColor(WindowsFrame.FromOption(true), true, true, null));
        Assert.Equal(Default, WindowsFrame.BorderColor(null, true, true, null));
    }

    [Fact]
    public void Report_IsGetAccentColor()
    {
        Assert.Equal("#ABCDEF", WindowsFrame.Report(WindowsFrame.FromOption("#abcdef"), System));
        Assert.Equal("#0078D4", WindowsFrame.Report(WindowsFrame.FromOption(true), System));
        Assert.Null(WindowsFrame.Report(WindowsFrame.FromOption(true), null));
        Assert.Null(WindowsFrame.Report(WindowsFrame.FromOption(false), System));
        Assert.Equal("#0078D4", WindowsFrame.Report(null, System));
        Assert.Null(WindowsFrame.Report(null, null));
    }

    [Fact]
    public void AccentColor_ConvertsFromBoolAndString()
    {
        Assert.Same(AccentColor.System, (AccentColor)true);
        Assert.Same(AccentColor.None, (AccentColor)false);
        AccentColor red = "red";
        Assert.Equal(("red", true), (red.Color, red.Enabled));
        Assert.False(AccentColor.None.Enabled);
    }

    [Theory]
    // Electron's #ARGB and #AARRGGBB put the alpha first.
    [InlineData("#8F00", 0x88FF0000u)]
    [InlineData("#80112233", 0x80112233u)]
    // Other forms are Chromium's.
    [InlineData("#F00", 0xFFFF0000u)]
    [InlineData("#112233", 0xFF112233u)]
    [InlineData("rgb(1, 2, 3)", 0xFF010203u)]
    public void TryParseElectron_PutsHexAlphaFirst(string css, uint argb)
    {
        Assert.True(CssColor.TryParseElectron(css, out var parsed));
        Assert.Equal(argb, parsed);
    }

    [Theory]
    [InlineData("#8F0G")]
    [InlineData("#80112G33")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseElectron_RejectsWhatChromiumRejects(string? css) => Assert.False(CssColor.TryParseElectron(css, out _));
}
