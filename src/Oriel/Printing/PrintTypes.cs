namespace Oriel;

/// <summary>A paper size: a name (<c>A4</c>, <c>Letter</c>, …) or a width and height. Inches for
/// <see cref="WebContents.PrintToPDFAsync"/>, microns for <see cref="WebContents.Print"/>, as in Electron.</summary>
public readonly record struct PageSize
{
    /// <summary>The named size, or null for a custom one.</summary>
    public string? Name { get; init; }

    /// <summary>The custom width.</summary>
    public double Width { get; init; }

    /// <summary>The custom height.</summary>
    public double Height { get; init; }

    /// <summary>A named size.</summary>
    public static PageSize FromName(string name) => new() { Name = name ?? throw new ArgumentNullException(nameof(name)) };

    /// <summary>A custom size.</summary>
    public static PageSize Custom(double width, double height) => new() { Width = width, Height = height };

    /// <summary>A named size.</summary>
    public static implicit operator PageSize(string name) => FromName(name);

    /// <inheritdoc/>
    public override string ToString() => Name ?? $"{{ width: {Width}, height: {Height} }}";
}

/// <summary>Page margins for <see cref="WebContents.PrintToPDFAsync"/>, in inches; null sides are 0.4 inches.</summary>
public sealed record PdfMargins
{
    /// <summary>Top margin.</summary>
    public double? Top { get; init; }

    /// <summary>Bottom margin.</summary>
    public double? Bottom { get; init; }

    /// <summary>Left margin.</summary>
    public double? Left { get; init; }

    /// <summary>Right margin.</summary>
    public double? Right { get; init; }
}

/// <summary>Options for <see cref="WebContents.PrintToPDFAsync"/>. Electron's <c>printToPDF</c> options, with its defaults.</summary>
public sealed record PrintToPdfOptions
{
    /// <summary>Landscape rather than portrait.</summary>
    public bool Landscape { get; init; }

    /// <summary>Print <see cref="HeaderTemplate"/> and <see cref="FooterTemplate"/>.</summary>
    public bool DisplayHeaderFooter { get; init; }

    /// <summary>Print background graphics.</summary>
    public bool PrintBackground { get; init; }

    /// <summary>Rendering scale, 0.1 to 2; 1 when null.</summary>
    public double? Scale { get; init; }

    /// <summary>The paper: A0–A6, Letter, Legal, Tabloid, Ledger (any case), or inches; Letter when null.</summary>
    public PageSize? PageSize { get; init; }

    /// <summary>Margins in inches.</summary>
    public PdfMargins? Margins { get; init; }

    /// <summary>Pages to print, e.g. <c>1-5, 8, 11-13</c>; every page when empty.</summary>
    public string? PageRanges { get; init; }

    /// <summary>HTML for the header, with classes <c>date</c>, <c>title</c>, <c>url</c>, <c>pageNumber</c>, <c>totalPages</c>.</summary>
    public string? HeaderTemplate { get; init; }

    /// <summary>HTML for the footer, as <see cref="HeaderTemplate"/>.</summary>
    public string? FooterTemplate { get; init; }

    /// <summary>Prefer the page's CSS <c>@page</c> size.</summary>
    public bool PreferCSSPageSize { get; init; }

    /// <summary>Generate a tagged (accessible) PDF.</summary>
    public bool GenerateTaggedPDF { get; init; }

    /// <summary>Generate an outline from the page's headings.</summary>
    public bool GenerateDocumentOutline { get; init; }
}

/// <summary>Margin kinds for <see cref="WebContents.Print"/>. Electron's <c>marginType</c>.</summary>
public enum PrintMarginType
{
    /// <summary>The printer's default margins.</summary>
    Default,
    /// <summary>No margins.</summary>
    None,
    /// <summary>The printer's minimum margins.</summary>
    PrintableArea,
    /// <summary><see cref="PrintMargins"/>' values.</summary>
    Custom,
}

/// <summary>Margins for <see cref="WebContents.Print"/>, in pixels.</summary>
public sealed record PrintMargins
{
    /// <summary>The kind of margins.</summary>
    public PrintMarginType MarginType { get; init; }

    /// <summary>Top margin, for <see cref="PrintMarginType.Custom"/>.</summary>
    public double Top { get; init; }

    /// <summary>Bottom margin.</summary>
    public double Bottom { get; init; }

    /// <summary>Left margin.</summary>
    public double Left { get; init; }

    /// <summary>Right margin.</summary>
    public double Right { get; init; }
}

/// <summary>Duplex modes for <see cref="WebContents.Print"/>.</summary>
public enum PrintDuplexMode
{
    /// <summary>One-sided.</summary>
    Simplex,
    /// <summary>Two-sided, flipped on the short edge.</summary>
    ShortEdge,
    /// <summary>Two-sided, flipped on the long edge.</summary>
    LongEdge,
}

/// <summary>A zero-based page range for <see cref="WebContents.Print"/>.</summary>
/// <param name="From">First page.</param>
/// <param name="To">Last page, inclusive.</param>
public sealed record PrintPageRange(int From, int To);

/// <summary>Options for <see cref="WebContents.Print"/>. Electron's <c>print</c> options.</summary>
public sealed record PrintOptions
{
    /// <summary>Print without asking.</summary>
    public bool Silent { get; init; }

    /// <summary>Print background graphics.</summary>
    public bool PrintBackground { get; init; }

    /// <summary>The printer; the default printer when null.</summary>
    public string? DeviceName { get; init; }

    /// <summary>Colour rather than greyscale; true when null.</summary>
    public bool? Color { get; init; }

    /// <summary>Margins.</summary>
    public PrintMargins? Margins { get; init; }

    /// <summary>Landscape rather than portrait.</summary>
    public bool Landscape { get; init; }

    /// <summary>Scale factor in percent; 100 when null.</summary>
    public double? ScaleFactor { get; init; }

    /// <summary>Pages per sheet; 1 when null.</summary>
    public int? PagesPerSheet { get; init; }

    /// <summary>Collate copies; true when null.</summary>
    public bool? Collate { get; init; }

    /// <summary>Copies; 1 when null.</summary>
    public int? Copies { get; init; }

    /// <summary>Pages to print.</summary>
    public IReadOnlyList<PrintPageRange>? PageRanges { get; init; }

    /// <summary>Duplex mode.</summary>
    public PrintDuplexMode? DuplexMode { get; init; }

    /// <summary>Resolution, horizontal and vertical dpi.</summary>
    public (int Horizontal, int Vertical)? Dpi { get; init; }

    /// <summary>Text for the page header.</summary>
    public string? Header { get; init; }

    /// <summary>Text for the page footer.</summary>
    public string? Footer { get; init; }

    /// <summary>The paper: A0–A6, Letter, Legal, Tabloid (exact case), or microns.</summary>
    public PageSize? PageSize { get; init; }

    /// <summary>Use the printer's default paper; not with <see cref="PageSize"/>.</summary>
    public bool? UsePrinterDefaultPageSize { get; init; }
}
