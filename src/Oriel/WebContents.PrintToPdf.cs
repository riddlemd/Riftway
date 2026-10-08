using Oriel.Cef;

namespace Oriel;

/// <summary>Printing the page to PDF.</summary>
public sealed unsafe partial class WebContents
{
    // Electron's paperFormats, in inches; Ledger is Tabloid turned on its side.
    private static readonly Dictionary<string, (double Width, double Height)> s_paperSizes = new()
    {
        ["letter"] = (8.5, 11), ["legal"] = (8.5, 14), ["tabloid"] = (11, 17), ["ledger"] = (17, 11),
        ["a0"] = (33.1, 46.8), ["a1"] = (23.4, 33.1), ["a2"] = (16.54, 23.4), ["a3"] = (11.7, 16.54),
        ["a4"] = (8.27, 11.7), ["a5"] = (5.83, 8.27), ["a6"] = (4.13, 5.83),
    };

    // GetPrintPagesParams' own messages in Chromium 154; every other failure is the print job's.
    private static readonly string[] s_printParamErrors =
        ["scale is outside", "margin is negative", "paper width is", "paper height is", "content area is empty"];

    // Electron queues jobs per frame tree: two at once in one renderer conflict.
    private readonly Lock _pdfGate = new();
    private Task _pdfQueue = Task.CompletedTask;

    /// <summary>Prints the page to PDF with Chromium's print-preview settings and returns the file's bytes. Electron's
    /// <c>webContents.printToPDF()</c>; through DevTools, always the main frame (drift C51).</summary>
    /// <exception cref="InvalidOperationException">An unknown paper name, margins larger than the paper, parameters
    /// Chromium refuses or a print that failed; with Electron's messages.</exception>
    public Task<byte[]> PrintToPdfAsync(PrintToPdfOptions? options = null) => PrintToPdf(null, options);

    internal Task<byte[]> PrintToPdf(FrameState? frame, PrintToPdfOptions? options)
    {
        options ??= new PrintToPdfOptions();
        double width, height;
        if (options.PageSize.Name is { } name)
        {
            if (!s_paperSizes.TryGetValue(name.ToLowerInvariant(), out var size)) return Task.FromException<byte[]>(new InvalidOperationException($"Invalid pageSize {name}"));
            (width, height) = size;
        }
        else (width, height) = (options.PageSize.Width, options.PageSize.Height);
        var margins = options.Margins ?? new PdfMargins();
        if (margins.Top > height || margins.Bottom > height || margins.Left > width || margins.Right > width)
            return Task.FromException<byte[]>(new InvalidOperationException("margins must be less than or equal to pageSize"));

        Task<byte[]> next;
        lock (_pdfGate)
        {
            next = _pdfQueue.ContinueWith(_ => Print(frame, options, width, height, margins), TaskScheduler.Default).Unwrap();
            _pdfQueue = next;
        }
        return next;
    }

    private Task<byte[]> Print(FrameState? frame, PrintToPdfOptions options, double width, double height, PdfMargins margins)
    {
        var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        static InvalidOperationException Failed(string why) => new($"Failed to generate PDF: {why}");
        // A page that closes mid-print never answers.
        var registration = _destroyed.Token.Register(() => done.TrySetException(Failed("Printing failed")));
        _ = done.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        CefThread.RunOnUi(() =>
        {
            if (frame is { Detached: true })
            {
                done.TrySetException(new InvalidOperationException("Render frame was disposed before WebFrameMain could be accessed"));
                return;
            }
            if (frame is not null && frame.Id != _mainFrameId)
            {
                done.TrySetException(new NotSupportedException("Only a page's main frame can be printed to PDF here (drift C51)."));
                return;
            }
            if (_browser is null)
            {
                done.TrySetException(Failed("Printing failed"));
                return;
            }
            _devTools.Send("Page.printToPDF", w =>
            {
                w.WriteBoolean("landscape", options.Landscape);
                w.WriteBoolean("displayHeaderFooter", options.DisplayHeaderFooter);
                w.WriteBoolean("printBackground", options.PrintBackground);
                w.WriteNumber("scale", options.Scale);
                w.WriteNumber("paperWidth", width);
                w.WriteNumber("paperHeight", height);
                w.WriteNumber("marginTop", margins.Top ?? 0.4);
                w.WriteNumber("marginBottom", margins.Bottom ?? 0.4);
                w.WriteNumber("marginLeft", margins.Left ?? 0.4);
                w.WriteNumber("marginRight", margins.Right ?? 0.4);
                w.WriteString("pageRanges", options.PageRanges);
                w.WriteString("headerTemplate", options.HeaderTemplate);
                w.WriteString("footerTemplate", options.FooterTemplate);
                w.WriteBoolean("preferCSSPageSize", options.PreferCssPageSize);
                w.WriteBoolean("generateTaggedPDF", options.GenerateTaggedPdf);
                w.WriteBoolean("generateDocumentOutline", options.GenerateDocumentOutline);
                w.WriteString("transferMode", "ReturnAsBase64");
            }, (result, error) =>
            {
                if (error is null) done.TrySetResult(Convert.FromBase64String(result.GetProperty("data").GetString() ?? ""));
                else if (s_printParamErrors.Any(e => error.Contains(e, StringComparison.Ordinal))) done.TrySetException(new InvalidOperationException($"Invalid print parameters: {error}"));
                else done.TrySetException(Failed(error));
            });
        });
        return done.Task;
    }
}
