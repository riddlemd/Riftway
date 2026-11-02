namespace Riftway;

/// <summary>Electron's printing option rules (print-to-pdf.ts, web-contents.ts) and Chromium's
/// <c>GetPrintPagesParams</c>/<c>TextPageRangesToPageRanges</c> checks, with their messages.</summary>
internal static class PrintRules
{
    // print-to-pdf.ts paperFormats, in inches; looked up lower-cased.
    private static readonly Dictionary<string, (double Width, double Height)> s_paperFormats = new(StringComparer.Ordinal)
    {
        ["letter"] = (8.5, 11), ["legal"] = (8.5, 14), ["tabloid"] = (11, 17), ["ledger"] = (17, 11),
        ["a0"] = (33.1, 46.8), ["a1"] = (23.4, 33.1), ["a2"] = (16.54, 23.4), ["a3"] = (11.7, 16.54),
        ["a4"] = (8.27, 11.7), ["a5"] = (5.83, 8.27), ["a6"] = (4.13, 5.83),
    };

    // web-contents.ts PDFPageSizes for print(), in microns; exact-case keys.
    private static readonly Dictionary<string, (int Width, int Height)> s_printPageSizes = new(StringComparer.Ordinal)
    {
        ["Letter"] = (215900, 279400), ["Legal"] = (215900, 355600), ["Tabloid"] = (279400, 431800),
        ["A0"] = (841000, 1189000), ["A1"] = (594000, 841000), ["A2"] = (420000, 594000), ["A3"] = (297000, 420000),
        ["A4"] = (210000, 297000), ["A5"] = (148000, 210000), ["A6"] = (105000, 148000),
    };

    private const double PointsPerInch = 72;
    private const double DefaultMargin = 0.4;

    /// <summary>The resolved Page.printToPDF parameters.</summary>
    internal sealed record PdfSettings(
        bool Landscape, bool DisplayHeaderFooter, string HeaderTemplate, string FooterTemplate, bool PrintBackground, double Scale,
        double MarginTop, double MarginBottom, double MarginLeft, double MarginRight, string PageRanges, bool PreferCSSPageSize,
        bool GenerateTaggedPDF, bool GenerateDocumentOutline, double PaperWidth, double PaperHeight);

    /// <summary>Electron's <c>printToPDF</c> option handling and Chromium's parameter checks.</summary>
    /// <exception cref="ArgumentException">A bad page size or margins, or Chromium's "Invalid print parameters: …".</exception>
    /// <exception cref="InvalidOperationException">"Failed to generate PDF: …" for bad page ranges.</exception>
    public static PdfSettings ResolvePdf(PrintToPdfOptions options)
    {
        var margins = options.Margins ?? new PdfMargins();
        var (paperWidth, paperHeight) = ParsePdfPageSize(options.PageSize ?? "letter");
        var validHeight = new[] { margins.Top, margins.Bottom }.All(m => m is null || m <= paperHeight);
        var validWidth = new[] { margins.Left, margins.Right }.All(m => m is null || m <= paperWidth);
        if (!validHeight || !validWidth) throw new ArgumentException("margins must be less than or equal to pageSize");

        var settings = new PdfSettings(
            options.Landscape, options.DisplayHeaderFooter, options.HeaderTemplate ?? "", options.FooterTemplate ?? "",
            options.PrintBackground, options.Scale ?? 1.0,
            margins.Top ?? DefaultMargin, margins.Bottom ?? DefaultMargin, margins.Left ?? DefaultMargin, margins.Right ?? DefaultMargin,
            options.PageRanges ?? "", options.PreferCSSPageSize, options.GenerateTaggedPDF, options.GenerateDocumentOutline,
            paperWidth, paperHeight);

        if (PrintPagesParamsError(settings) is { } error) throw new ArgumentException("Invalid print parameters: " + error);
        if (PageRangesError(settings.PageRanges) is { } rangeError) throw new InvalidOperationException("Failed to generate PDF: " + rangeError);
        return settings;
    }

    /// <summary>print-to-pdf.ts parsePageSize: inches for a name (any case) or the given size.</summary>
    /// <exception cref="ArgumentException">An unknown name.</exception>
    public static (double Width, double Height) ParsePdfPageSize(PageSize size)
    {
        if (size.Name is { } name)
            return s_paperFormats.TryGetValue(name.ToLowerInvariant(), out var format) ? format : throw new ArgumentException($"Invalid pageSize {name}");
        return (size.Width, size.Height);
    }

    /// <summary>Chromium's <c>GetPrintPagesParams</c> errors, in its order; null when the settings are valid.</summary>
    public static string? PrintPagesParamsError(PdfSettings s)
    {
        if (s.Scale > 2 || s.Scale < 0.1) return "scale is outside of [0.1 - 2] range";
        if (s.MarginLeft < 0) return "left margin is negative";
        if (s.MarginRight < 0) return "right margin is negative";
        if (s.MarginTop < 0) return "top margin is negative";
        if (s.MarginBottom < 0) return "bottom margin is negative";
        if (s.PaperWidth <= 0) return "paper width is zero or negative";
        if (s.PaperHeight <= 0) return "paper height is zero or negative";
        // PrintSettings works in points: paper ceiled, margins floored, the paper turned for landscape.
        var width = Math.Ceiling(s.PaperWidth * PointsPerInch);
        var height = Math.Ceiling(s.PaperHeight * PointsPerInch);
        if (s.Landscape) (width, height) = (height, width);
        var contentWidth = width - Math.Floor(s.MarginLeft * PointsPerInch) - Math.Floor(s.MarginRight * PointsPerInch);
        var contentHeight = height - Math.Floor(s.MarginTop * PointsPerInch) - Math.Floor(s.MarginBottom * PointsPerInch);
        return contentWidth <= 0 || contentHeight <= 0 ? "invalid print parameters: content area is empty" : null;
    }

    /// <summary>Chromium's <c>TextPageRangesToPageRanges</c>: its <c>PdfPrintResult</c> message, or null when valid.</summary>
    public static string? PageRangesError(string text)
    {
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            uint from, to;
            if (!part.Contains('-'))
            {
                if (!ParseUint(part, out from)) return "Page range syntax error";
                to = from;
            }
            else if (part == "-")
            {
                continue;
            }
            else if (part.StartsWith('-'))
            {
                from = 1;
                if (!ParseUint(part[1..], out to)) return "Page range syntax error";
            }
            else if (part.EndsWith('-'))
            {
                if (!ParseUint(part[..^1], out from)) return "Page range syntax error";
                to = uint.MaxValue;
            }
            else
            {
                var tokens = part.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length != 2 || !ParseUint(tokens[0], out from) || !ParseUint(tokens[1], out to)) return "Page range syntax error";
            }
            if (from < 1 || from > to) return "Page range is invalid (start > end)";
        }
        return null;
    }

    // base::StringToUint: digits only, no sign or spaces.
    private static bool ParseUint(string text, out uint value)
        => uint.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);

    /// <summary>The message for a Page.printToPDF error, with the prefix Electron gives the same failure.</summary>
    public static string PdfErrorMessage(string devToolsError) => devToolsError switch
    {
        "Printing failed" or "Invalid shared memory region" or "Invalid shared memory mapping" or "Page range syntax error"
            or "Page range is invalid (start > end)" or "Page range exceeds page count" or "Page is already being printed"
            => "Failed to generate PDF: " + devToolsError,
        _ => "Invalid print parameters: " + devToolsError,
    };

    /// <summary>web-contents.ts print() page size checks: the media size in microns, or null for none.</summary>
    /// <exception cref="ArgumentException">An unsupported name, or a size without width and height.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A side under 353 microns.</exception>
    public static (int Width, int Height)? ResolvePrintMediaSize(PrintOptions options)
    {
        if (options.UsePrinterDefaultPageSize is not null && options.PageSize is not null)
            throw new ArgumentException("usePrinterDefaultPageSize cannot be combined with pageSize");
        if (options.PageSize is not { } size) return null;
        if (size.Name is { } name)
            return s_printPageSizes.TryGetValue(name, out var known) ? known : throw new ArgumentException($"Unsupported pageSize: {name}");
        if (size.Height == 0 || size.Width == 0) throw new ArgumentException("height and width properties are required for pageSize");
        var height = (int)Math.Ceiling(size.Height);
        var width = (int)Math.Ceiling(size.Width);
        if (width <= 352 || height <= 352) throw new ArgumentOutOfRangeException(null, "height and width properties must be minimum 352 microns.");
        return (width, height);
    }

    /// <summary>Electron's GetDeviceNameToUse: the printer to use, or its error message.</summary>
    /// <param name="deviceName">The requested printer; null or empty for the default.</param>
    /// <param name="isValid">Whether the spooler opens a printer by name.</param>
    /// <param name="defaultPrinter">The default printer; empty for none, null when the query failed.</param>
    /// <param name="printers">Every printer's name; null when enumeration failed.</param>
    public static (string? Error, string Device) DeviceNameToUse(string? deviceName, Func<string, bool> isValid, Func<string?> defaultPrinter, Func<IReadOnlyList<string>?> printers)
    {
        if (!string.IsNullOrEmpty(deviceName))
            return isValid(deviceName) ? (null, deviceName) : ("Invalid deviceName provided", "");
        var name = defaultPrinter() ?? "";
        if (name.Length > 0) return (null, name);
        if (printers() is not { } list) return ("Failed to enumerate printers", "");
        return list.Count == 0 ? ("No printers available on the network", "") : (null, list[0]);
    }
}
