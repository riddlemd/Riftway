namespace Riftway.Tests;

public sealed class PrintToPdfRulesTests
{
    [Theory]
    [InlineData("a4", 8.27, 11.7)]
    [InlineData("A4", 8.27, 11.7)]
    [InlineData("Letter", 8.5, 11)]
    [InlineData("ledger", 17, 11)]
    [InlineData("tabloid", 11, 17)]
    public void ParsePdfPageSize_NamesAnyCase(string name, double width, double height)
        => Assert.Equal((width, height), PrintRules.ParsePdfPageSize(name));

    [Fact]
    public void ParsePdfPageSize_Unknown() => Assert.Equal("Invalid pageSize A9", Assert.Throws<ArgumentException>(() => PrintRules.ParsePdfPageSize("A9")).Message);

    [Fact]
    public void ParsePdfPageSize_Custom() => Assert.Equal((5.0, 7.0), PrintRules.ParsePdfPageSize(PageSize.Custom(5, 7)));

    [Fact]
    public void ResolvePdf_ElectronDefaults()
    {
        var s = PrintRules.ResolvePdf(new PrintToPdfOptions());
        Assert.Equal(new PrintRules.PdfSettings(false, false, "", "", false, 1.0, 0.4, 0.4, 0.4, 0.4, "", false, false, false, 8.5, 11), s);
    }

    [Fact]
    public void ResolvePdf_PassesOptionsThrough()
    {
        var s = PrintRules.ResolvePdf(new PrintToPdfOptions
        {
            Landscape = true, DisplayHeaderFooter = true, HeaderTemplate = "h", FooterTemplate = "f", PrintBackground = true, Scale = 1.5,
            Margins = new PdfMargins { Top = 1, Bottom = 2, Left = 0.5, Right = 0.25 }, PageRanges = "1-2", PreferCSSPageSize = true,
            GenerateTaggedPDF = true, GenerateDocumentOutline = true, PageSize = "a4",
        });
        Assert.Equal(new PrintRules.PdfSettings(true, true, "h", "f", true, 1.5, 1, 2, 0.5, 0.25, "1-2", true, true, true, 8.27, 11.7), s);
    }

    [Fact]
    public void ResolvePdf_TopMarginOverHeight() => Assert.Equal("margins must be less than or equal to pageSize",
        Assert.Throws<ArgumentException>(() => PrintRules.ResolvePdf(new() { Margins = new PdfMargins { Top = 11.1 } })).Message);

    [Fact]
    public void ResolvePdf_TopMarginComparesWithTheHeight() => Assert.Equal(10, PrintRules.ResolvePdf(new() { Margins = new PdfMargins { Top = 10 } }).MarginTop);

    [Fact]
    public void ResolvePdf_BottomMarginOverHeight() => Assert.Equal("margins must be less than or equal to pageSize",
        Assert.Throws<ArgumentException>(() => PrintRules.ResolvePdf(new() { Margins = new PdfMargins { Bottom = 11.1 } })).Message);

    [Fact]
    public void ResolvePdf_LeftMarginOverWidth() => Assert.Equal("margins must be less than or equal to pageSize",
        Assert.Throws<ArgumentException>(() => PrintRules.ResolvePdf(new() { Margins = new PdfMargins { Left = 8.6 } })).Message);

    [Fact]
    public void ResolvePdf_RightMarginOverWidth() => Assert.Equal("margins must be less than or equal to pageSize",
        Assert.Throws<ArgumentException>(() => PrintRules.ResolvePdf(new() { Margins = new PdfMargins { Right = 8.6 } })).Message);

    [Fact]
    public void ResolvePdf_MarginEqualToTheSideIsAllowedByElectron()
        => Assert.Equal("Invalid print parameters: invalid print parameters: content area is empty",
            Assert.Throws<ArgumentException>(() => PrintRules.ResolvePdf(new() { Margins = new PdfMargins { Left = 8.5 } })).Message);

    [Fact]
    public void ResolvePdf_ElectronsMarginCheckComesFirst() => Assert.Equal("margins must be less than or equal to pageSize",
        Assert.Throws<ArgumentException>(() => PrintRules.ResolvePdf(new() { Scale = 9, Margins = new PdfMargins { Top = 99 } })).Message);

    [Fact]
    public void ResolvePdf_ChromiumErrorIsPrefixed() => Assert.Equal("Invalid print parameters: scale is outside of [0.1 - 2] range",
        Assert.Throws<ArgumentException>(() => PrintRules.ResolvePdf(new() { Scale = 2.5 })).Message);

    [Fact]
    public void ResolvePdf_PageRangeErrorIsAPrintFailure() => Assert.Equal("Failed to generate PDF: Page range syntax error",
        Assert.Throws<InvalidOperationException>(() => PrintRules.ResolvePdf(new() { PageRanges = "x" })).Message);

    private static PrintRules.PdfSettings Valid() => PrintRules.ResolvePdf(new PrintToPdfOptions());

    [Theory]
    [InlineData(2.0, null)]
    [InlineData(0.1, null)]
    [InlineData(2.01, "scale is outside of [0.1 - 2] range")]
    [InlineData(0.09, "scale is outside of [0.1 - 2] range")]
    public void PrintPagesParamsError_Scale(double scale, string? error) => Assert.Equal(error, PrintRules.PrintPagesParamsError(Valid() with { Scale = scale }));

    [Fact]
    public void PrintPagesParamsError_MarginsInChromiumsOrder()
    {
        var all = Valid() with { MarginLeft = -1, MarginRight = -1, MarginTop = -1, MarginBottom = -1 };
        Assert.Equal("left margin is negative", PrintRules.PrintPagesParamsError(all));
        Assert.Equal("right margin is negative", PrintRules.PrintPagesParamsError(all with { MarginLeft = 0 }));
        Assert.Equal("top margin is negative", PrintRules.PrintPagesParamsError(all with { MarginLeft = 0, MarginRight = 0 }));
        Assert.Equal("bottom margin is negative", PrintRules.PrintPagesParamsError(all with { MarginLeft = 0, MarginRight = 0, MarginTop = 0 }));
    }

    [Fact]
    public void PrintPagesParamsError_ZeroMarginsAreFine() => Assert.Null(PrintRules.PrintPagesParamsError(Valid() with { MarginLeft = 0, MarginRight = 0, MarginTop = 0, MarginBottom = 0 }));

    [Fact]
    public void PrintPagesParamsError_PaperWidth() => Assert.Equal("paper width is zero or negative", PrintRules.PrintPagesParamsError(Valid() with { PaperWidth = 0 }));

    [Fact]
    public void PrintPagesParamsError_PaperHeight() => Assert.Equal("paper height is zero or negative", PrintRules.PrintPagesParamsError(Valid() with { PaperHeight = -1 }));

    [Fact]
    public void PrintPagesParamsError_EmptyWidth()
        => Assert.Equal("invalid print parameters: content area is empty", PrintRules.PrintPagesParamsError(Valid() with { MarginLeft = 4.25, MarginRight = 4.25 }));

    [Fact]
    public void PrintPagesParamsError_EmptyHeight()
        => Assert.Equal("invalid print parameters: content area is empty", PrintRules.PrintPagesParamsError(Valid() with { MarginTop = 5.5, MarginBottom = 5.5 }));

    [Fact]
    public void PrintPagesParamsError_LandscapeTurnsThePaper()
        => Assert.Null(PrintRules.PrintPagesParamsError(Valid() with { Landscape = true, MarginLeft = 4.5, MarginRight = 4.5 }));

    [Fact]
    public void PrintPagesParamsError_PortraitWithTheSameMarginsIsEmpty()
        => Assert.NotNull(PrintRules.PrintPagesParamsError(Valid() with { MarginLeft = 4.5, MarginRight = 4.5 }));

    [Fact]
    public void PrintPagesParamsError_PointsRoundTheWayChromiumDoes()
    {
        // Paper ceiled to 1 pt, margins floored to 0 pt: one point of content remains.
        Assert.Null(PrintRules.PrintPagesParamsError(Valid() with { PaperWidth = 0.001, MarginLeft = 0.01, MarginRight = 0.01 }));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("1", null)]
    [InlineData(" 1 - 3 , 5", null)]
    [InlineData("-", null)]
    [InlineData("-3", null)]
    [InlineData("2-", null)]
    [InlineData("abc", "Page range syntax error")]
    [InlineData("+1", "Page range syntax error")]
    [InlineData("1-2-3", "Page range syntax error")]
    [InlineData("-x", "Page range syntax error")]
    [InlineData("x-", "Page range syntax error")]
    [InlineData("0", "Page range is invalid (start > end)")]
    [InlineData("3-1", "Page range is invalid (start > end)")]
    [InlineData("0-", "Page range is invalid (start > end)")]
    public void PageRangesError_FollowsChromium(string text, string? error) => Assert.Equal(error, PrintRules.PageRangesError(text));

    [Theory]
    [InlineData("Page range exceeds page count", "Failed to generate PDF: Page range exceeds page count")]
    [InlineData("Printing failed", "Failed to generate PDF: Printing failed")]
    [InlineData("paper width is zero or negative", "Invalid print parameters: paper width is zero or negative")]
    public void PdfErrorMessage_PrefixesLikeElectron(string error, string message) => Assert.Equal(message, PrintRules.PdfErrorMessage(error));
}

public sealed class PrintRulesTests
{
    [Fact]
    public void MediaSize_NoneWithoutPageSize() => Assert.Null(PrintRules.ResolvePrintMediaSize(new PrintOptions()));

    [Fact]
    public void MediaSize_DefaultPageSizeAloneIsFine() => Assert.Null(PrintRules.ResolvePrintMediaSize(new PrintOptions { UsePrinterDefaultPageSize = false }));

    [Fact]
    public void MediaSize_NotWithUsePrinterDefault() => Assert.Equal("usePrinterDefaultPageSize cannot be combined with pageSize",
        Assert.Throws<ArgumentException>(() => PrintRules.ResolvePrintMediaSize(new PrintOptions { PageSize = "A4", UsePrinterDefaultPageSize = false })).Message);

    [Fact]
    public void MediaSize_NamedInMicrons() => Assert.Equal((210000, 297000), PrintRules.ResolvePrintMediaSize(new PrintOptions { PageSize = "A4" }));

    [Fact]
    public void MediaSize_NamesAreCaseSensitive() => Assert.Equal("Unsupported pageSize: a4",
        Assert.Throws<ArgumentException>(() => PrintRules.ResolvePrintMediaSize(new PrintOptions { PageSize = "a4" })).Message);

    [Fact]
    public void MediaSize_CustomNeedsHeight() => Assert.Equal("height and width properties are required for pageSize",
        Assert.Throws<ArgumentException>(() => PrintRules.ResolvePrintMediaSize(new PrintOptions { PageSize = PageSize.Custom(1000, 0) })).Message);

    [Fact]
    public void MediaSize_CustomNeedsWidth() => Assert.Throws<ArgumentException>(() => PrintRules.ResolvePrintMediaSize(new PrintOptions { PageSize = PageSize.Custom(0, 1000) }));

    [Fact]
    public void MediaSize_352MicronsIsTooSmall() => Assert.Equal("height and width properties must be minimum 352 microns.",
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintRules.ResolvePrintMediaSize(new PrintOptions { PageSize = PageSize.Custom(352, 1000) })).Message);

    [Fact]
    public void MediaSize_HeightIsCheckedToo() => Assert.Throws<ArgumentOutOfRangeException>(() => PrintRules.ResolvePrintMediaSize(new PrintOptions { PageSize = PageSize.Custom(1000, 352) }));

    [Fact]
    public void MediaSize_CustomIsCeiled() => Assert.Equal((353, 1001), PrintRules.ResolvePrintMediaSize(new PrintOptions { PageSize = PageSize.Custom(352.2, 1000.5) }));

    [Fact]
    public void DeviceName_ValidIsUsed() => Assert.Equal((null, "P1"), PrintRules.DeviceNameToUse("P1", _ => true, () => "D", () => []));

    [Fact]
    public void DeviceName_Invalid() => Assert.Equal(("Invalid deviceName provided", ""), PrintRules.DeviceNameToUse("X", _ => false, () => "D", () => ["D"]));

    [Fact]
    public void DeviceName_DefaultPrinter() => Assert.Equal((null, "D"), PrintRules.DeviceNameToUse(null, _ => false, () => "D", () => null));

    [Fact]
    public void DeviceName_EnumerationFails() => Assert.Equal(("Failed to enumerate printers", ""), PrintRules.DeviceNameToUse("", _ => true, () => "", () => null));

    [Fact]
    public void DeviceName_NoPrinters() => Assert.Equal(("No printers available on the network", ""), PrintRules.DeviceNameToUse(null, _ => true, () => null, () => []));

    [Fact]
    public void DeviceName_FirstPrinterWithoutADefault() => Assert.Equal((null, "A"), PrintRules.DeviceNameToUse(null, _ => true, () => "", () => ["A", "B"]));
}
