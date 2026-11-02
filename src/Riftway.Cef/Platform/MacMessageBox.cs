using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>What <see cref="MacMessageBox.Create"/> sets up; Electron's <c>MessageBoxSettings</c> as macOS uses them.</summary>
/// <param name="Type">0 none, 1 info, 2 warning, 3 error, 4 question.</param>
/// <param name="Buttons">The button titles, in order; empty leaves AppKit's own OK.</param>
/// <param name="DefaultId">The button Return chooses, or -1 for the first.</param>
/// <param name="CancelId">The button Escape chooses when there are several.</param>
/// <param name="Message">The bold text.</param>
/// <param name="Detail">The informative text.</param>
/// <param name="CheckboxLabel">The suppression checkbox's title; empty for none.</param>
/// <param name="CheckboxChecked">Whether the checkbox starts on.</param>
/// <param name="IconPng">An icon in place of the app's, as PNG; null for none.</param>
/// <param name="TextWidth">The text's width in points; 0 for AppKit's.</param>
public sealed record MacAlertSettings(int Type, IReadOnlyList<string> Buttons, int DefaultId, int CancelId, string Message, string Detail,
    string CheckboxLabel, bool CheckboxChecked, byte[]? IconPng, int TextWidth);

/// <summary>NSAlert, set up and run as Electron's <c>shell/browser/ui/message_box_mac.mm</c>. Main thread only.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacMessageBox
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    // NSAlertStyleWarning, NSAlertStyleInformational, NSAlertStyleCritical.
    private const nint StyleWarning = 0, StyleInformational = 1, StyleCritical = 2;
    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }

    /// <summary>A retained NSAlert set up as Electron's <c>CreateNSAlert</c>; pass it to <see cref="Release"/>.</summary>
    public static nint Create(MacAlertSettings settings)
    {
        var alert = Send(Send(Class("NSAlert"u8), Sel("alloc"u8)), Sel("init"u8));
        SendObject(alert, Sel("setMessageText:"u8), NSString(settings.Message));
        SendObject(alert, Sel("setInformativeText:"u8), NSString(settings.Detail));
        // Warning and error are both critical, as AppKit has no error style; none and question keep the default.
        var style = settings.Type switch { 1 => StyleInformational, 2 or 3 => StyleCritical, _ => StyleWarning };
        SendInteger(alert, Sel("setAlertStyle:"u8), style);
        for (var i = 0; i < settings.Buttons.Count; i++)
        {
            // An empty title crashes AppKit.
            var title = settings.Buttons[i].Length == 0 ? "(empty)" : settings.Buttons[i];
            var button = SendObject(alert, Sel("addButtonWithTitle:"u8), NSString(title));
            SendInteger(button, Sel("setTag:"u8), i);
        }
        var buttons = Send(alert, Sel("buttons"u8));
        var count = (int)Send(buttons, Sel("count"u8));
        nint ButtonAt(int index) => SendInteger(buttons, Sel("objectAtIndex:"u8), index);
        if (settings.DefaultId >= 0 && settings.DefaultId < count)
        {
            SendObject(ButtonAt(0), Sel("setKeyEquivalent:"u8), NSString(""));
            SendObject(ButtonAt(settings.DefaultId), Sel("setKeyEquivalent:"u8), NSString("\r"));
        }
        if (count > 1 && settings.CancelId >= 0)
        {
            if (settings.CancelId < count) SendObject(ButtonAt(settings.CancelId), Sel("setKeyEquivalent:"u8), NSString("\u001b"));
            if (settings.CancelId == settings.DefaultId) SendBool(ButtonAt(settings.DefaultId), Sel("highlight:"u8), true);
        }
        if (settings.CheckboxLabel.Length > 0)
        {
            SendBool(alert, Sel("setShowsSuppressionButton:"u8), true);
            var suppression = Send(alert, Sel("suppressionButton"u8));
            SendObject(suppression, Sel("setTitle:"u8), NSString(settings.CheckboxLabel));
            SendInteger(suppression, Sel("setState:"u8), settings.CheckboxChecked ? 1 : 0);
        }
        if (settings.IconPng is { Length: > 0 } png)
        {
            nint data;
            fixed (byte* p = png) data = ((delegate* unmanaged<nint, nint, byte*, nuint, nint>)s_msgSend)(Class("NSData"u8), Sel("dataWithBytes:length:"u8), p, (nuint)png.Length);
            var image = SendObject(Send(Class("NSImage"u8), Sel("alloc"u8)), Sel("initWithData:"u8), data);
            SendObject(alert, Sel("setIcon:"u8), image);
            Send(image, Sel("release"u8));
        }
        if (settings.TextWidth > 0)
        {
            var view = ((delegate* unmanaged<nint, nint, NSRect, nint>)s_msgSend)(Send(Class("NSView"u8), Sel("alloc"u8)), Sel("initWithFrame:"u8),
                new NSRect { Width = settings.TextWidth });
            SendObject(alert, Sel("setAccessoryView:"u8), view);
            Send(view, Sel("release"u8));
        }
        return alert;
    }

    /// <summary>[alert runModal]: the chosen button's tag, AppKit's 1000 for its own OK, or a negative response when
    /// stopped.</summary>
    public static int RunModal(nint alert) => (int)Send(alert, Sel("runModal"u8));

    /// <summary>Electron's sync sheet: begins the alert as a sheet on the window behind <paramref name="contentView"/> and
    /// runs that window modally until it answers.</summary>
    public static int RunSheetModal(nint alert, nint contentView)
    {
        var window = Send(contentView, Sel("window"u8));
        var app = Send(Class("NSApplication"u8), Sel("sharedApplication"u8));
        var chosen = -1;
        var block = MacFileDialog.NewBlock(response =>
        {
            chosen = (int)response;
            Send(app, Sel("stopModal"u8));
        });
        ((delegate* unmanaged<nint, nint, nint, MacFileDialog.Block*, void>)s_msgSend)(alert, Sel("beginSheetModalForWindow:completionHandler:"u8), window, &block);
        SendObject(app, Sel("runModalForWindow:"u8), window);
        return chosen;
    }

    /// <summary>Begins the alert as a sheet on the window behind <paramref name="contentView"/>; <paramref name="completed"/>
    /// gets AppKit's response.</summary>
    public static void BeginSheet(nint alert, nint contentView, Action<int> completed)
    {
        var block = MacFileDialog.NewBlock(response => completed((int)response));
        ((delegate* unmanaged<nint, nint, nint, MacFileDialog.Block*, void>)s_msgSend)(alert, Sel("beginSheetModalForWindow:completionHandler:"u8),
            Send(contentView, Sel("window"u8)), &block);
    }

    /// <summary>Electron's <c>CloseMessageBox</c>: [NSApp endSheet:alert.window], which answers with a negative response.</summary>
    public static void EndSheet(nint alert)
        => SendObject(Send(Class("NSApplication"u8), Sel("sharedApplication"u8)), Sel("endSheet:"u8), Send(alert, Sel("window"u8)));

    /// <summary>Stops a running <see cref="RunModal"/> or <see cref="RunSheetModal"/>, which then answers with a negative
    /// response.</summary>
    public static void AbortModal() => Send(Send(Class("NSApplication"u8), Sel("sharedApplication"u8)), Sel("abortModal"u8));

    /// <summary>Runs <paramref name="action"/> on the main thread in every run loop mode, including a modal loop's, where
    /// CEF's UI tasks wait (drift P5).</summary>
    public static void PostToMainLoop(Action action)
    {
        var block = MacFileDialog.NewBlock(_ => action());
        var main = CFRunLoopGetMain();
        CFRunLoopPerformBlock(main, s_commonModes, &block);
        CFRunLoopWakeUp(main);
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly nint s_commonModes = *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFRunLoopCommonModes");

    [DllImport(CoreFoundation)] private static extern nint CFRunLoopGetMain();
    [DllImport(CoreFoundation)] private static extern void CFRunLoopPerformBlock(nint runLoop, nint mode, MacFileDialog.Block* block);
    [DllImport(CoreFoundation)] private static extern void CFRunLoopWakeUp(nint runLoop);

    /// <summary>Whether the alert's suppression checkbox is on.</summary>
    public static bool Checked(nint alert) => Send(Send(alert, Sel("suppressionButton"u8)), Sel("state"u8)) == 1;

    /// <summary>Releases an alert from <see cref="Create"/>.</summary>
    public static void Release(nint alert) => Send(alert, Sel("release"u8));

    /// <summary>Electron's mac <c>ShowErrorBox</c>: a critical alert with the title and content, run modally.</summary>
    public static void ShowError(string title, string content)
    {
        var alert = Send(Send(Class("NSAlert"u8), Sel("alloc"u8)), Sel("init"u8));
        SendObject(alert, Sel("setMessageText:"u8), NSString(title));
        SendObject(alert, Sel("setInformativeText:"u8), NSString(content));
        SendInteger(alert, Sel("setAlertStyle:"u8), StyleCritical);
        Send(alert, Sel("runModal"u8));
        Send(alert, Sel("release"u8));
    }

    private static nint NSString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* p = bytes) return ((delegate* unmanaged<nint, nint, byte*, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithUTF8String:"u8), p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint SendObject(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static nint SendInteger(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static void SendBool(nint receiver, nint selector, bool value) => ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(receiver, selector, value ? (byte)1 : (byte)0);

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
}
