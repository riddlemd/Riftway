namespace Oriel;

/// <summary>Options for <see cref="WebContents.PrintToPdfAsync"/> and <see cref="WebFrameMain.PrintToPdfAsync"/>:
/// Electron's <c>PrintToPDFOptions</c>, with its defaults.</summary>
public sealed record PrintToPdfOptions
{
    /// <summary>Landscape pages; ignored when the page has an <c>@page</c> rule. Electron's <c>landscape</c>.</summary>
    public bool Landscape { get; init; }

    /// <summary>Prints <see cref="HeaderTemplate"/> and <see cref="FooterTemplate"/>. Electron's <c>displayHeaderFooter</c>.</summary>
    public bool DisplayHeaderFooter { get; init; }

    /// <summary>Prints background colours and images. Electron's <c>printBackground</c>.</summary>
    public bool PrintBackground { get; init; }

    /// <summary>The page's scale, 0.1 to 2. Electron's <c>scale</c>.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>The paper: a name (<c>A0</c> to <c>A6</c>, <c>Legal</c>, <c>Letter</c>, <c>Tabloid</c>, <c>Ledger</c>) or a
    /// size in inches. Electron's <c>pageSize</c>, <c>Letter</c> by default.</summary>
    public PdfPageSize PageSize { get; init; } = "Letter";

    /// <summary>The page margins in inches. Electron's <c>margins</c>, 0.4 inches each by default.</summary>
    public PdfMargins Margins { get; init; } = new();

    /// <summary>The pages to print, such as <c>1-5, 8, 11-13</c>; empty for all. Electron's <c>pageRanges</c>.</summary>
    public string PageRanges { get; init; } = string.Empty;

    /// <summary>HTML for the header; elements with the classes <c>date</c>, <c>title</c>, <c>url</c>, <c>pageNumber</c>
    /// and <c>totalPages</c> are filled in. Electron's <c>headerTemplate</c>.</summary>
    public string HeaderTemplate { get; init; } = string.Empty;

    /// <summary>HTML for the footer, as <see cref="HeaderTemplate"/>. Electron's <c>footerTemplate</c>.</summary>
    public string FooterTemplate { get; init; } = string.Empty;

    /// <summary>Prefers the page's CSS page size to <see cref="PageSize"/>. Electron's <c>preferCSSPageSize</c>.</summary>
    public bool PreferCssPageSize { get; init; }

    /// <summary>Writes a tagged (accessible) PDF. Electron's experimental <c>generateTaggedPDF</c>.</summary>
    public bool GenerateTaggedPdf { get; init; }

    /// <summary>Writes an outline from the page's headings. Electron's experimental <c>generateDocumentOutline</c>.</summary>
    public bool GenerateDocumentOutline { get; init; }
}

/// <summary>A paper size: one of Electron's names, or a width and height in inches.</summary>
public sealed record PdfPageSize
{
    private PdfPageSize(string? name, double width, double height) => (Name, Width, Height) = (name, width, height);

    /// <summary>The paper's name as given, or null for a custom size.</summary>
    public string? Name { get; }

    /// <summary>The custom width in inches; 0 for a named size.</summary>
    public double Width { get; }

    /// <summary>The custom height in inches; 0 for a named size.</summary>
    public double Height { get; }

    /// <summary>A named paper, matched without regard to case when printing.</summary>
    public static PdfPageSize Named(string name) => new(name ?? throw new ArgumentNullException(nameof(name)), 0, 0);

    /// <summary>A paper <paramref name="width"/> by <paramref name="height"/> inches.</summary>
    public static PdfPageSize Inches(double width, double height) => new(null, width, height);

    /// <summary>A named paper, like Electron's string form.</summary>
    public static implicit operator PdfPageSize(string name) => Named(name);
}

/// <summary>Page margins in inches. Electron's <c>margins</c>; one left unset is 0.4 inches and is not checked against
/// the paper size.</summary>
public sealed record PdfMargins
{
    /// <summary>Top margin; null for 0.4 inches (about 1 cm).</summary>
    public double? Top { get; init; }

    /// <summary>Bottom margin; null for 0.4 inches.</summary>
    public double? Bottom { get; init; }

    /// <summary>Left margin; null for 0.4 inches.</summary>
    public double? Left { get; init; }

    /// <summary>Right margin; null for 0.4 inches.</summary>
    public double? Right { get; init; }
}
