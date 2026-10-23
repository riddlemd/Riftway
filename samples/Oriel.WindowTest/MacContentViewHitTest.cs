using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Oriel;
using Oriel.Cef;
using Oriel.Cef.Platform;

/// <summary>Which NSView AppKit would hand a click to, asked of the window's frame view as AppKit asks it. Synthetic
/// input (sendInputEvent, DevTools) goes straight to the page and never takes this path.</summary>
[SupportedOSPlatform("macos")]
static class MacContentViewHitTest
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var win = new BrowserWindow(new BrowserWindowOptions { Width = 400, Height = 300, Show = true });
        try
        {
            await win.LoadUrl("data:text/html,<body style='margin:0;background:#fff'>hit</body>");
            await Task.Delay(step);

            var handle = win.GetNativeWindowHandle();
            var height = win.GetContentBounds().Height;
            string? At(int x, int y) => CefThread.InvokeOnUi(() => ViewClassAt(handle, x, height - y));

            const string Page = "RenderWidgetHostViewCocoa";
            check("Mac hit test: wrapped (Chromium's BridgedContentView found)", MacContentHitTest.IsInstalled);
            check("Mac hit test: a click on an empty content view reaches the page", At(200, 150) == Page, At(200, 150));

            // A plain view covering the top-left corner: Views takes clicks on it, the page the rest (Electron's targeter).
            var cover = new View();
            cover.SetBackgroundColor("#FF336699");
            cover.SetBounds(new Rectangle(0, 0, 100, 100));
            win.ContentView.AddChildView(cover);
            await Task.Delay(step);
            check("Mac hit test: a child view takes the clicks on it", At(50, 50) is { } onCover && onCover != Page, At(50, 50));
            check("Mac hit test: beside a child view, clicks reach the page", At(200, 150) == Page, At(200, 150));

            cover.SetVisible(false);
            await Task.Delay(step);
            check("Mac hit test: a hidden child view lets clicks through", At(50, 50) == Page, At(50, 50));
            win.ContentView.RemoveChildView(cover);

            win.ContentView.SetBackgroundColor("#80FF0000");
            await Task.Delay(step);
            check("Mac hit test: the content view's own background does not take clicks", At(200, 150) == Page, At(200, 150));
        }
        finally
        {
            win.Destroy();
        }
    }

    // The point is in the frame view's own coordinates, bottom-up, as AppKit passes it to hitTest:.
    private static unsafe string? ViewClassAt(nint contentView, double x, double y)
    {
        var frameView = Send(Send(Send(contentView, Sel("window")), Sel("contentView")), Sel("superview"));
        var hit = ((delegate* unmanaged<nint, nint, double, double, nint>)s_msgSend)(frameView, Sel("hitTest:"), x, y);
        return hit == 0 ? null : Marshal.PtrToStringUTF8(object_getClassName(hit));
    }

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    private static unsafe nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Sel(string name) => sel_registerName(name);

    [DllImport(ObjC, CharSet = CharSet.Ansi)] private static extern nint sel_registerName(string name);

    [DllImport(ObjC)] private static extern nint object_getClassName(nint obj);
}
