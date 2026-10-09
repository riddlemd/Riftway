using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Oriel;
using Oriel.Cef;
using Oriel.Cef.Platform;

/// <summary>Tray on macOS: the NSStatusItem read back through AppKit and the accessibility tree, and Electron's
/// StatusItemView events driven by sending the view the NSEvents AppKit would.</summary>
[SupportedOSPlatform("macos")]
static class MacTrayTest
{
    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var guid = Guid.NewGuid();
        var icon = Square(16, 16);
        icon.SetTemplateImage(true);
        var tray = new Tray(icon, guid);
        var backend = (MacTrayBackend)tray.Backend;
        var view = backend.View;
        T Ui<T>(Func<T> f) => CefThread.InvokeOnUi(f);
        try
        {
            check("new Tray() puts a visible NSStatusItem in the system status bar", Ui(() => MacStatusItem.IsVisible(view)));
            check("its guid becomes the status item's autosave name, lowercase", Ui(() => MacStatusItem.AutosaveName(view)) == guid.ToString("D"), Ui(() => MacStatusItem.AutosaveName(view)));
            check("the image is a 16x16-point template image on the button", Ui(() => MacStatusItem.ImageInfo(view)) == (16, 16, true), Ui(() => MacStatusItem.ImageInfo(view)));
            // The status bar sizes the item window, then places it, after statusItemWithLength: returns; until then the
            // frame is empty, then below the screen at the origin.
            var bounds = tray.GetBounds();
            var placed = Eventually(() => (bounds = tray.GetBounds()) is { Width: > 0, Height: > 0, Y: < 50 }, step);
            check("getBounds() is the status item window's frame, at the top of the primary display", placed, bounds);

            tray.SetToolTip("Oriel tip");
            check("setToolTip() sets the view's tooltip", Ui(() => MacStatusItem.ToolTip(view)) == "Oriel tip");

            tray.SetTitle("CPU \u001b[1;31mhot\u001b[0m ok", new TrayTitleOptions { FontType = TrayTitleFontType.Monospaced });
            var (runs, font) = Ui(() => MacStatusItem.AttributedTitle(view));
            check("setTitle() parses the ANSI codes: getTitle() is the plain text", tray.GetTitle() == "CPU hot ok", tray.GetTitle());
            check("the bold red run is #cd0000 in calibrated RGB", runs.Any(r => r.Text == "hot" && r.Foreground == 0xCD0000), string.Join(" | ", runs));
            check("fontType monospaced covers the title with the monospaced system font", font.Contains("Mono", StringComparison.OrdinalIgnoreCase), font);
            check("a non-empty title puts the image to its left (NSImageLeft)", Ui(() => MacStatusItem.ImagePosition(view)) == 2);
            check("the title shows in the accessibility tree (needs Accessibility for osascript)", Accessibility().Contains("CPU hot ok", StringComparison.Ordinal), Accessibility());
            tray.SetTitle("");
            check("an empty title shows the image only (NSImageOnly)", Ui(() => MacStatusItem.ImagePosition(view)) == 1 && tray.GetTitle() == "");

            var pressed = Square(32, 32);
            pressed.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 2, Width = 64, Height = 64, Buffer = Pixels(64, 64) });
            tray.SetPressedImage(pressed);
            check("setPressedImage() sets the button's alternate image, sized from its 1x representation", Ui(() => MacStatusItem.ImageInfo(view, alternate: true)) == (32, 32, false),
                Ui(() => MacStatusItem.ImageInfo(view, alternate: true)));

            tray.SetIgnoreDoubleClickEvents(true);
            check("setIgnoreDoubleClickEvents() round-trips", tray.GetIgnoreDoubleClickEvents());
            tray.SetIgnoreDoubleClickEvents(false);

            await EventsAsync(check, step, tray, view);
            await MenuAsync(check, step, tray, view);
        }
        finally
        {
            tray.Destroy();
        }
        check("destroy() removes the status item", !Ui(() => MacStatusItem.IsLive(view)) && tray.IsDestroyed());
    }

    private static async Task EventsAsync(MessageBoxTest.CheckFn check, TimeSpan step, Tray tray, nint view)
    {
        var log = new List<string>();
        TrayEventArgs? last = null;
        void On(string name, TrayEventArgs e) { log.Add(name); last = e; }
        tray.Clicked += (_, e) => On("click", e);
        tray.DoubleClicked += (_, e) => On("double", e);
        tray.RightClicked += (_, e) => On("right", e);
        tray.MousePressed += (_, e) => On("down", e);
        tray.MouseReleased += (_, e) => On("up", e);
        tray.MouseEntered += (_, e) => On("enter", e);
        tray.MouseLeft += (_, e) => On("leave", e);
        tray.MouseMoved += (_, e) => On("move", e);
        tray.DragEntered += (_, _) => log.Add("drag-enter");
        tray.DragLeft += (_, _) => log.Add("drag-leave");
        tray.DragEnded += (_, _) => log.Add("drag-end");
        tray.Dropped += (_, _) => log.Add("drop");
        var files = new List<string>();
        string? text = null;
        tray.FilesDropped += (_, e) => files.AddRange(e.Files);
        tray.TextDropped += (_, e) => text = e.Text;
        string Log() => string.Join(",", log);
        void Send(string selector, uint type, long clicks, ulong flags = 0) => CefThread.InvokeOnUi(() =>
        {
            SendEvent(view, selector, type, clicks, flags);
            return true;
        });

        // NSEventTypeLeftMouseDown 1, LeftMouseUp 2, RightMouseUp 4, MouseMoved 5, MouseEntered 8, MouseExited 9.
        Send("mouseDown:", 1, 1, 1 << 20);
        check("mouseDown: without a menu raises mouse-down with Command as metaKey, and highlights", Log() == "down" && last!.MetaKey, Log());
        log.Clear();
        Send("mouseUp:", 2, 1);
        var frame = CefThread.InvokeOnUi(() => MacStatusItem.WindowFrame(view));
        check("mouseUp: raises mouse-up then click, the click with the window's bounds and the flipped location",
            Log() == "up,click" && last!.Bounds == tray.GetBounds() && last.Position == new Point(5, (int)(frame.PrimaryHeight - 5)), $"{Log()} {last?.Bounds} {last?.Position}");
        log.Clear();
        Send("mouseUp:", 2, 2);
        check("a second click raises double-click with no position", Log() == "up,double" && last!.Position == default, Log());
        log.Clear();
        tray.SetIgnoreDoubleClickEvents(true);
        Send("mouseUp:", 2, 2);
        check("with double clicks ignored, the second click is a click", Log() == "up,click", Log());
        tray.SetIgnoreDoubleClickEvents(false);
        log.Clear();
        Send("rightMouseUp:", 4, 1, 1 << 17);
        check("rightMouseUp: raises right-click with its keys", Log() == "right" && last!.ShiftKey, Log());
        log.Clear();
        Send("mouseEntered:", 8, 0);
        Send("mouseMoved:", 5, 0);
        Send("mouseExited:", 9, 0);
        check("the tracking area's enter, move and exit raise mouse-enter, mouse-move and mouse-leave", Log() == "enter,move,leave", Log());

        log.Clear();
        var inside = CefThread.InvokeOnUi(() => FakeDrag(view, files: ["/tmp/a.txt", "/tmp/b.txt"], text: null, inside: true));
        CefThread.InvokeOnUi(() =>
        {
            SendObject(view, "draggingEntered:", inside);
            SendObject(view, "performDragOperation:", inside);
            SendObject(view, "draggingEnded:", inside);
            return true;
        });
        check("a file drag raises drag-enter, drop-files with the paths, then drag-end and drop inside the frame",
            Log() == "drag-enter,drag-end,drop" && files.SequenceEqual(["/tmp/a.txt", "/tmp/b.txt"]), $"{Log()} {string.Join(";", files)}");
        log.Clear();
        var outside = CefThread.InvokeOnUi(() => FakeDrag(view, files: null, text: "dropped é", inside: false));
        CefThread.InvokeOnUi(() =>
        {
            SendObject(view, "performDragOperation:", outside);
            SendObject(view, "draggingExited:", outside);
            SendObject(view, "draggingEnded:", outside);
            return true;
        });
        check("a text drop raises drop-text; a drag ending outside the frame raises drag-leave and drag-end without drop",
            text == "dropped é" && Log() == "drag-leave,drag-end", $"{Log()} {text}");
        await Task.Delay(step / 10);
    }

    private static async Task MenuAsync(MessageBoxTest.CheckFn check, TimeSpan step, Tray tray, nint view)
    {
        var ran = new List<string>();
        var shown = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var menu = new Menu
        {
            new MenuItem { Label = "&Open", Click = (_, _, _) => ran.Add("open") },
            new MenuItem { Type = MenuItemType.Separator },
            new MenuItem { Label = "Pinned", Type = MenuItemType.Checkbox, Checked = true },
            new MenuItem { Label = "More", Submenu = new Menu { new MenuItem { Label = "Deep" } } },
        };
        menu.MenuWillShow += (_, _) => shown.Enqueue("show");
        menu.MenuWillClose += (_, _) => shown.Enqueue("close");
        tray.SetContextMenu(menu);
        var titles = CefThread.InvokeOnUi(() =>
        {
            var ns = MacStatusItem.ItemMenu(view);
            return Enumerable.Range(0, MacMenu.ItemCount(ns)).Select(i => MacMenu.ItemAt(ns, i)).Select(i => MacMenu.IsSeparator(i) ? "-" : MacMenu.Title(i)).ToList();
        });
        check("setContextMenu() makes an NSMenu the status item's menu, labels without mnemonics",
            titles.SequenceEqual(["Open", "-", "Pinned", "More"]), string.Join(",", titles));
        check("the checkbox item is checked", CefThread.InvokeOnUi(() => MacMenu.IsChecked(MacMenu.ItemAt(MacStatusItem.ItemMenu(view), 2))));
        CefThread.InvokeOnUi(() => { MacMenu.PerformAction(MacStatusItem.ItemMenu(view), 0); return true; });
        check("choosing an item runs its click", ran.SequenceEqual(["open"]), string.Join(",", ran));
        check("the accessibility tree has the status item (needs Accessibility for osascript)", Accessibility().Length > 0, Accessibility());

        // popUpContextMenu runs the menu in a nested loop; closeContextMenu from the menu's run-loop mode ends it.
        tray.PopUpContextMenu();
        await Task.Delay(step / 4);
        MacMenu.PerformOnMainThread(tray.CloseContextMenu);
        check("popUpContextMenu() opens the context menu, closeContextMenu() closes it", Eventually(() => string.Join(",", shown) == "show,close", step), string.Join(",", shown));

        var other = new Menu { new MenuItem { Label = "Other" } };
        var otherShown = new System.Collections.Concurrent.ConcurrentQueue<string>();
        other.MenuWillShow += (_, _) => otherShown.Enqueue("show");
        other.MenuWillClose += (_, _) => otherShown.Enqueue("close");
        var contextMenu = CefThread.InvokeOnUi(() => MacStatusItem.ItemMenu(view));
        tray.PopUpContextMenu(other);
        await Task.Delay(step / 4);
        // closeContextMenu reaches only the context menu, as Electron: cancel the custom menu's tracking directly.
        MacMenu.PerformOnMainThread(() => MacStatusItem.CancelTracking(MacStatusItem.ItemMenu(view)));
        check("popUpContextMenu(menu) shows that menu from the status item", Eventually(() => string.Join(",", otherShown) == "show,close", step), string.Join(",", otherShown));
        check("and puts the context menu back on the item afterwards", Eventually(() => CefThread.InvokeOnUi(() => MacStatusItem.ItemMenu(view)) == contextMenu, step));
        tray.SetContextMenu(null);
        check("setContextMenu(null) clears the item's menu", CefThread.InvokeOnUi(() => MacStatusItem.ItemMenu(view)) == 0);
    }

    // System Events' view of this process's status items: their titles and descriptions.
    private static string Accessibility()
    {
        var script = $"tell application \"System Events\" to tell (first process whose unix id is {Environment.ProcessId}) to get {{title, description}} of every menu bar item of menu bar 2";
        using var p = Process.Start(new ProcessStartInfo("/usr/bin/osascript", ["-e", script]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    private static NativeImage Square(int w, int h) => NativeImage.CreateFromBitmap(Pixels(w, h), new NativeImageBitmapOptions { Width = w, Height = h });

    private static byte[] Pixels(int w, int h)
    {
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (0, 0, 0, 255);
        return pixels;
    }

    private static bool Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint
    {
        public double X, Y;
    }

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");
    private static nint s_fakeDragClass;
    private static readonly Dictionary<nint, (nint Board, NSPoint Location)> s_drags = [];

    // An NSEvent at (5, 5) in the status item's window, as AppKit would deliver it.
    private unsafe static void SendEvent(nint view, string selector, uint type, long clicks, ulong flags)
    {
        var window = Send(view, Sel("window"));
        var windowNumber = window == 0 ? 0 : Send(window, Sel("windowNumber"));
        nint e;
        if (type is 8 or 9)
            e = ((delegate* unmanaged<nint, nint, nuint, NSPoint, nuint, double, nint, nint, nint, nint, nint, nint>)s_msgSend)(objc_getClass("NSEvent"),
                Sel("enterExitEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:trackingNumber:userData:"),
                type, new NSPoint { X = 5, Y = 5 }, (nuint)flags, 0, windowNumber, 0, 0, 0, 0);
        else
            e = ((delegate* unmanaged<nint, nint, nuint, NSPoint, nuint, double, nint, nint, nint, nint, float, nint>)s_msgSend)(objc_getClass("NSEvent"),
                Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"),
                type, new NSPoint { X = 5, Y = 5 }, (nuint)flags, 0, windowNumber, 0, 0, (nint)clicks, 1.0f);
        SendObject(view, selector, e);
    }

    // A stand-in NSDraggingInfo: a private pasteboard holding the files or text, and a location in or outside the view.
    private unsafe static nint FakeDrag(nint view, string[]? files, string? text, bool inside)
    {
        if (s_fakeDragClass == 0)
        {
            s_fakeDragClass = objc_allocateClassPair(objc_getClass("NSObject"), "OrielFakeDraggingInfo", 0);
            class_addMethod(s_fakeDragClass, Sel("draggingPasteboard"), (delegate* unmanaged<nint, nint, nint>)&DraggingPasteboard, "@@:");
            class_addMethod(s_fakeDragClass, Sel("draggingLocation"), (delegate* unmanaged<nint, nint, NSPoint>)&DraggingLocation, "{CGPoint=dd}@:");
            objc_registerClassPair(s_fakeDragClass);
        }
        var board = Send(objc_getClass("NSPasteboard"), Sel("pasteboardWithUniqueName"));
        Send(board, Sel("clearContents"));
        if (files is not null)
        {
            var array = Send(objc_getClass("NSMutableArray"), Sel("array"));
            foreach (var f in files) SendObject(array, "addObject:", NSString(f));
            SendObject2(board, "setPropertyList:forType:", array, NSString("NSFilenamesPboardType"));
        }
        if (text is not null) SendObject2(board, "setString:forType:", NSString(text), NSString("public.utf8-plain-text"));
        var info = Send(Send(s_fakeDragClass, Sel("alloc")), Sel("init"));
        s_drags[info] = (board, inside ? new NSPoint { X = 1, Y = 1 } : new NSPoint { X = -50, Y = -50 });
        return info;
    }

    [UnmanagedCallersOnly]
    private static nint DraggingPasteboard(nint self, nint cmd) => s_drags[self].Board;

    [UnmanagedCallersOnly]
    private static NSPoint DraggingLocation(nint self, nint cmd) => s_drags[self].Location;

    private unsafe static nint NSString(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = bytes) return ((delegate* unmanaged<nint, nint, byte*, nint>)s_msgSend)(objc_getClass("NSString"), Sel("stringWithUTF8String:"), p);
    }

    private static nint Sel(string name) => sel_registerName(name);

    private unsafe static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private unsafe static nint SendObject(nint receiver, string selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, Sel(selector), arg);

    private unsafe static nint SendObject2(nint receiver, string selector, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_msgSend)(receiver, Sel(selector), a, b);

    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern nint objc_allocateClassPair(nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint extra);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern void objc_registerClassPair(nint cls);
    [DllImport("/usr/lib/libobjc.A.dylib")] private unsafe static extern byte class_addMethod(nint cls, nint name, void* imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);
}
