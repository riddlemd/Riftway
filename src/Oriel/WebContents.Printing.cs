using Oriel.Cef;
using System.Runtime.Versioning;
using Oriel.Cef.Platform;

namespace Oriel;

public sealed unsafe partial class WebContents
{
    // Concurrent PDF jobs in one page conflict in the renderer, so Electron queues them per page.
    private Task _pdfQueue = Task.CompletedTask;
    private readonly Lock _pdfGate = new();

    /// <summary>Renders the page as a PDF, with Electron's defaults (Letter, 0.4-inch margins, scale 1).
    /// Electron's <c>webContents.printToPDF()</c>.</summary>
    /// <returns>The PDF's bytes.</returns>
    /// <exception cref="ArgumentException">Bad options, with Electron's message (the task faults; nothing is thrown).</exception>
    /// <exception cref="InvalidOperationException">"Failed to generate PDF: …", or the page has closed.</exception>
    public Task<byte[]> PrintToPDFAsync(PrintToPdfOptions? options = null)
    {
        PrintRules.PdfSettings settings;
        try { settings = PrintRules.ResolvePdf(options ?? new PrintToPdfOptions()); }
        catch (Exception e) { return Task.FromException<byte[]>(e); }

        lock (_pdfGate)
        {
            var job = _pdfQueue.ContinueWith(_ => SendPrintToPdf(settings), TaskScheduler.Default).Unwrap();
            _pdfQueue = job;
            return job;
        }
    }

    // DevTools prints only the main frame, so frame.printToPDF refuses the others (drift C51).
    internal Task<byte[]> PrintFrameToPDFAsync(FrameState frame, PrintToPdfOptions? options)
    {
        var checkedFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            if (frame.Detached) checkedFrame.TrySetException(new InvalidOperationException("Render frame was disposed before WebFrameMain could be accessed"));
            else if (frame.Id != _mainFrameId) checkedFrame.TrySetException(new NotSupportedException("Only a page's main frame can be printed to PDF here (drift C51)."));
            else checkedFrame.TrySetResult();
        });
        return checkedFrame.Task.ContinueWith(t => t.IsFaulted ? Task.FromException<byte[]>(t.Exception!.InnerException!) : PrintToPDFAsync(options), TaskScheduler.Default).Unwrap();
    }

    private Task<byte[]> SendPrintToPdf(PrintRules.PdfSettings s)
    {
        var done = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            if (_browser is null)
            {
                done.TrySetException(new InvalidOperationException("The page has closed."));
                return;
            }
            _devTools.Send("Page.printToPDF", w =>
            {
                w.WriteBoolean("landscape", s.Landscape);
                w.WriteBoolean("displayHeaderFooter", s.DisplayHeaderFooter);
                w.WriteBoolean("printBackground", s.PrintBackground);
                w.WriteNumber("scale", s.Scale);
                w.WriteNumber("paperWidth", s.PaperWidth);
                w.WriteNumber("paperHeight", s.PaperHeight);
                w.WriteNumber("marginTop", s.MarginTop);
                w.WriteNumber("marginBottom", s.MarginBottom);
                w.WriteNumber("marginLeft", s.MarginLeft);
                w.WriteNumber("marginRight", s.MarginRight);
                w.WriteString("pageRanges", s.PageRanges);
                w.WriteString("headerTemplate", s.HeaderTemplate);
                w.WriteString("footerTemplate", s.FooterTemplate);
                w.WriteBoolean("preferCSSPageSize", s.PreferCSSPageSize);
                w.WriteBoolean("generateTaggedPDF", s.GenerateTaggedPDF);
                w.WriteBoolean("generateDocumentOutline", s.GenerateDocumentOutline);
            }, (result, error) =>
            {
                if (error is not null) done.TrySetException(new InvalidOperationException(PrintRules.PdfErrorMessage(error)));
                else done.TrySetResult(Convert.FromBase64String(result.GetProperty("data").GetString() ?? ""));
            });
        });
        return done.Task;
    }

    /// <summary>Prints the page. Electron's <c>webContents.print()</c>.</summary>
    /// <remarks>CEF opens Chromium's print dialog and takes no settings, so only the checks Electron makes before
    /// printing apply: option validation and, when <see cref="PrintOptions.DeviceName"/> is set, that the printer
    /// exists. Silent printing is not possible and reports <c>failed</c>; after the dialog opens,
    /// <paramref name="callback"/> is not called, as CEF does not report the outcome.</remarks>
    /// <param name="options">Print settings.</param>
    /// <param name="callback">Electron's <c>callback(success, failureReason)</c>.</param>
    /// <exception cref="ArgumentException">Bad options, with Electron's message.</exception>
    public void Print(PrintOptions? options = null, Action<bool, string>? callback = null)
    {
        options ??= new PrintOptions();
        PrintRules.ResolvePrintMediaSize(options);
        if (options == new PrintOptions())
        {
            WithHost(host => host->print(host));
            return;
        }
        var error = OperatingSystem.IsWindows() ? DeviceError(options.DeviceName) : null;
        if (error is not null)
        {
            callback?.Invoke(false, error);
            return;
        }
        if (options.Silent)
        {
            callback?.Invoke(false, "failed");
            return;
        }
        WithHost(host => host->print(host));
    }

    /// <summary>The system's printers. Electron's <c>webContents.getPrintersAsync()</c>.</summary>
    /// <remarks>Windows lists local and connected printers as Chromium does; other systems return none.</remarks>
    public Task<IReadOnlyList<PrinterInfo>> GetPrintersAsync()
        => Task.Run(() => OperatingSystem.IsWindows() ? ListPrinters() : (IReadOnlyList<PrinterInfo>)[]);

    [SupportedOSPlatform("windows")]
    private static string? DeviceError(string? deviceName)
        => PrintRules.DeviceNameToUse(deviceName, WindowsPrinters.IsValid, WindowsPrinters.GetDefault, PrinterNames).Error;

    [SupportedOSPlatform("windows")]
    private static List<string>? PrinterNames() => WindowsPrinters.Enumerate()?.Select(p => p.Name).ToList();

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<PrinterInfo> ListPrinters()
    {
        var result = new List<PrinterInfo>();
        foreach (var printer in WindowsPrinters.Enumerate() ?? [])
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            // Chromium reads the location from the registry rather than the slow PRINTER_INFO_2.
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers\" + printer.Name);
            if (key?.GetValue("Location") is string { Length: > 0 } location) options["printer-location"] = location;
            result.Add(new PrinterInfo(printer.Name, printer.Name, printer.Comment ?? "", options));
        }
        return result;
    }
}
