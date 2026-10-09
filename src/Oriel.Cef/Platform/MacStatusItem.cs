using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>What Electron's <c>StatusItemView</c> tells its <c>TrayIconCocoa</c>.</summary>
public enum MacStatusItemSignal
{
    /// <summary><c>NotifyMouseDown</c>.</summary>
    MouseDown,

    /// <summary><c>NotifyMouseUp</c>.</summary>
    MouseUp,

    /// <summary><c>handleClickNotifications:</c>, which decides between a click and a double click.</summary>
    Click,

    /// <summary><c>rightMouseUp:</c>.</summary>
    RightMouseUp,

    /// <summary><c>mouseEntered:</c>.</summary>
    MouseEntered,

    /// <summary><c>mouseExited:</c>.</summary>
    MouseExited,

    /// <summary><c>mouseMoved:</c>.</summary>
    MouseMoved,

    /// <summary><c>draggingEntered:</c>.</summary>
    DragEntered,

    /// <summary><c>draggingExited:</c>.</summary>
    DragExited,

    /// <summary><c>draggingEnded:</c>; the location is <c>draggingLocation</c> and the frame the view's own.</summary>
    DragEnded,
}

/// <summary>An NSEvent as <c>StatusItemView</c> reads it: <c>locationInWindow</c>, <c>modifierFlags</c>,
/// <c>clickCount</c> and <c>window.frame</c> (zero without a window), in AppKit's bottom-left coordinates, with the
/// primary display's height for <c>gfx::ScreenRectFromNSRect</c>.</summary>
public readonly record struct MacStatusItemEvent(double X, double Y, ulong ModifierFlags, long ClickCount,
    double FrameX, double FrameY, double FrameWidth, double FrameHeight, double PrimaryHeight);

/// <summary>Receives a status item view's events. Main thread.</summary>
public interface IMacStatusItemHandler
{
    void Signal(MacStatusItemSignal signal, in MacStatusItemEvent e);

    /// <summary><c>NotifyDropFiles</c>: the <c>NSFilenamesPboardType</c> paths.</summary>
    void DropFiles(string[] files);

    /// <summary><c>NotifyDropText</c>: the <c>NSPasteboardTypeString</c> text.</summary>
    void DropText(string text);
}

/// <summary>One run of a tray title: its text and the ANSI colours <c>attributedStringParsingANSICodes</c> gave it, as
/// 0xRRGGBB.</summary>
public readonly record struct MacTitleRun(string Text, int? Foreground, int? Background);

/// <summary>Electron's <c>StatusItemView</c>: an NSView subclass over an <c>NSStatusItem</c>'s button that tracks the
/// mouse and drops, built through the Objective-C runtime. Main thread only.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacStatusItem
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint
    {
        public double X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjcSuper
    {
        public nint Receiver;
        public nint SuperClass;
    }

    private sealed class State(IMacStatusItemHandler handler, nint statusItem)
    {
        internal IMacStatusItemHandler Handler { get; } = handler;
        internal nint StatusItem { get; set; } = statusItem;
        internal nint TrackingArea { get; set; }
        // ElectronMenuController's menu, kept apart from the item's menu, which popUpContextMenu swaps.
        internal nint ControllerMenu { get; set; }
    }

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_appKit = NativeLibrary.Load(AppKit);
    private static readonly Dictionary<nint, State> s_views = [];
    private static nint s_viewClass;
    private static nint s_nsView;

    /// <summary>Electron's <c>initWithIcon:</c>: a variable-length status item whose button holds the new view (+1, owned
    /// by the caller) as target of its action, for VoiceOver.</summary>
    public static nint Create(IMacStatusItemHandler handler)
    {
        var view = Send(ViewClass(), Sel("alloc"u8));
        view = SendRect(view, Sel("initWithFrame:"u8), default);
        var pool = objc_autoreleasePoolPush();
        try
        {
            nint* types = stackalloc nint[2];
            types[0] = Constant("NSFilenamesPboardType");
            types[1] = Constant("NSPasteboardTypeString");
            Send(view, Sel("registerForDraggedTypes:"u8), SendArray(Class("NSArray"u8), Sel("arrayWithObjects:count:"u8), types, 2));
            // NSVariableStatusItemLength
            var item = SendDouble(Send(Class("NSStatusBar"u8), Sel("systemStatusBar"u8)), Sel("statusItemWithLength:"u8), -1.0);
            Send(item, Sel("retain"u8));
            s_views[view] = new State(handler, item);
            var button = Button(view);
            Send(button, Sel("addSubview:"u8), view);
            Send(button, Sel("setTarget:"u8), view);
            Send(button, Sel("setAction:"u8), Sel("mouseDown:"u8));
            UpdateDimensions(view);
        }
        finally { objc_autoreleasePoolPop(pool); }
        return view;
    }

    /// <summary>Electron's <c>removeItem</c>, then releases the view.</summary>
    public static void Remove(nint view)
    {
        if (!s_views.TryGetValue(view, out var state)) return;
        // Tracking goes first, so no event reaches a dead icon.
        if (state.TrackingArea != 0)
        {
            Send(view, Sel("removeTrackingArea:"u8), state.TrackingArea);
            Send(state.TrackingArea, Sel("release"u8));
            state.TrackingArea = 0;
        }
        var menu = Send(state.StatusItem, Sel("menu"u8));
        if (menu != 0) Send(menu, Sel("cancelTracking"u8));
        Send(Send(Class("NSStatusBar"u8), Sel("systemStatusBar"u8)), Sel("removeStatusItem:"u8), state.StatusItem);
        Send(view, Sel("removeFromSuperview"u8));
        Send(state.StatusItem, Sel("release"u8));
        state.StatusItem = 0;
        s_views.Remove(view);
        Send(view, Sel("release"u8));
    }

    /// <summary>Whether <paramref name="view"/> is still a live status item view.</summary>
    public static bool IsLive(nint view) => s_views.ContainsKey(view);

    public static void SetAutosaveName(nint view, string name) => WithPool(() => Send(Item(view), Sel("setAutosaveName:"u8), NSString(name)));

    /// <summary><c>setImage:</c>: the button's image from PNG representations (none clears it), then
    /// <c>updateDimensions</c>.</summary>
    public static void SetImage(nint view, IReadOnlyList<(byte[] Png, double Scale)> representations, bool template)
    {
        var image = Image(representations, template);
        Send(Button(view), Sel("setImage:"u8), image);
        if (image != 0) Send(image, Sel("release"u8));
        UpdateDimensions(view);
    }

    /// <summary><c>setAlternateImage:</c>, which also makes the button momentary-change so it shows the image while
    /// pressed.</summary>
    public static void SetAlternateImage(nint view, IReadOnlyList<(byte[] Png, double Scale)> representations, bool template)
    {
        var image = Image(representations, template);
        var button = Button(view);
        Send(button, Sel("setAlternateImage:"u8), image);
        if (image != 0) Send(image, Sel("release"u8));
        // NSButtonTypeMomentaryChange
        Send(button, Sel("setButtonType:"u8), 5);
        UpdateDimensions(view);
    }

    /// <summary>Electron sets the tooltip on its view, not on the button.</summary>
    public static void SetToolTip(nint view, string toolTip) => WithPool(() => Send(view, Sel("setToolTip:"u8), NSString(toolTip)));

    /// <summary>Electron's <c>setTitle:font_type:</c>: the runs as an attributed title, the font type (1 monospaced, 2
    /// monospaced digits) over the whole of it at the button's size, and the image to the left of a non-empty title.</summary>
    /// <param name="rawLength">The UTF-16 length of the title as given, escape codes included.</param>
    public static void SetTitle(nint view, IReadOnlyList<MacTitleRun> runs, int fontType, int rawLength) => WithPool(() =>
    {
        var button = Button(view);
        var title = Send(Send(Class("NSMutableAttributedString"u8), Sel("alloc"u8)), Sel("init"u8));
        foreach (var run in runs)
        {
            var attributes = Send(Class("NSMutableDictionary"u8), Sel("dictionary"u8));
            if (run.Foreground is { } fg) SetObject(attributes, Color(fg), Constant("NSForegroundColorAttributeName"));
            if (run.Background is { } bg) SetObject(attributes, Color(bg), Constant("NSBackgroundColorAttributeName"));
            var piece = Send(Send(Class("NSAttributedString"u8), Sel("alloc"u8)), Sel("initWithString:attributes:"u8), NSString(run.Text), attributes);
            Send(title, Sel("appendAttributedString:"u8), piece);
            Send(piece, Sel("release"u8));
        }
        if (fontType is 1 or 2)
        {
            var size = SendDoubleReturn(Send(button, Sel("font"u8)), Sel("pointSize"u8));
            var weight = *(double*)NativeLibrary.GetExport(s_appKit, "NSFontWeightRegular");
            var font = SendDoubleDouble(Class("NSFont"u8), fontType == 1 ? Sel("monospacedSystemFontOfSize:weight:"u8) : Sel("monospacedDigitSystemFontOfSize:weight:"u8), size, weight);
            var attributes = Send(Class("NSMutableDictionary"u8), Sel("dictionary"u8));
            SetObject(attributes, font, Constant("NSFontAttributeName"));
            var length = Send(title, Sel("length"u8));
            ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)s_msgSend)(title, Sel("addAttributes:range:"u8), attributes, 0, length);
        }
        Send(button, Sel("setAttributedTitle:"u8), title);
        Send(title, Sel("release"u8));
        // NSImageLeft, NSImageOnly
        Send(button, Sel("setImagePosition:"u8), rawLength > 0 ? 2 : 1);
        UpdateDimensions(view);
    });

    /// <summary><c>[statusItem.button title]</c>: the attributed title's plain text.</summary>
    public static string Title(nint view) => ReadString(Send(Button(view), Sel("title"u8)));

    /// <summary>The colours of the button's attributed title, run by run, as 0xRRGGBB in calibrated RGB, and whether a
    /// monospaced font covers it; for tests.</summary>
    public static (IReadOnlyList<MacTitleRun> Runs, string FontName) AttributedTitle(nint view)
    {
        var runs = new List<MacTitleRun>();
        var title = Send(Button(view), Sel("attributedTitle"u8));
        var length = (long)Send(title, Sel("length"u8));
        var string_ = ReadString(Send(title, Sel("string"u8)));
        string font = "";
        for (long at = 0; at < length;)
        {
            NSRangeStruct range;
            var attributes = ((delegate* unmanaged<nint, nint, nint, NSRangeStruct*, nint>)s_msgSend)(title, Sel("attributesAtIndex:effectiveRange:"u8), (nint)at, &range);
            var fg = Send(attributes, Sel("objectForKey:"u8), Constant("NSForegroundColorAttributeName"));
            var bg = Send(attributes, Sel("objectForKey:"u8), Constant("NSBackgroundColorAttributeName"));
            var f = Send(attributes, Sel("objectForKey:"u8), Constant("NSFontAttributeName"));
            if (f != 0) font = ReadString(Send(f, Sel("fontName"u8)));
            runs.Add(new MacTitleRun(string_.Substring((int)range.Location, (int)range.Length), RgbOf(fg), RgbOf(bg)));
            at = (long)(range.Location + range.Length);
        }
        return (runs, font);
    }

    /// <summary><c>[statusItem.button imagePosition]</c>; for tests.</summary>
    public static long ImagePosition(nint view) => (long)Send(Button(view), Sel("imagePosition"u8));

    /// <summary>The button's image size in points and whether it is a template, or null without one; for tests.</summary>
    public static (double Width, double Height, bool Template)? ImageInfo(nint view, bool alternate = false)
    {
        var image = Send(Button(view), alternate ? Sel("alternateImage"u8) : Sel("image"u8));
        if (image == 0) return null;
        var size = ((delegate* unmanaged<nint, nint, NSPoint>)s_msgSend)(image, Sel("size"u8));
        return (size.X, size.Y, (byte)Send(image, Sel("isTemplate"u8)) != 0);
    }

    /// <summary>The view's tooltip; for tests.</summary>
    public static string ToolTip(nint view) => ReadString(Send(view, Sel("toolTip"u8)));

    /// <summary>The status item's autosave name; for tests.</summary>
    public static string AutosaveName(nint view) => ReadString(Send(Item(view), Sel("autosaveName"u8)));

    /// <summary>Electron's <c>setMenuController:</c>: remembers the controller's menu and makes it the item's.</summary>
    public static void SetControllerMenu(nint view, nint menu)
    {
        s_views[view].ControllerMenu = menu;
        Send(Item(view), Sel("setMenu:"u8), menu);
    }

    /// <summary><c>[statusItem setMenu:]</c> alone, as <c>popUpContextMenu:</c> swaps a custom menu in.</summary>
    public static void SetItemMenu(nint view, nint menu) => Send(Item(view), Sel("setMenu:"u8), menu);

    /// <summary>The status item's menu; for tests.</summary>
    public static nint ItemMenu(nint view) => Send(Item(view), Sel("menu"u8));

    /// <summary>The menu <see cref="SetControllerMenu"/> set, or 0.</summary>
    public static nint ControllerMenu(nint view) => s_views.TryGetValue(view, out var state) ? state.ControllerMenu : 0;

    /// <summary><c>[[statusItem button] performClick:view]</c>; with a menu on the item it runs the menu until closed.</summary>
    public static void PerformClick(nint view) => Send(Button(view), Sel("performClick:"u8), view);

    /// <summary><c>[menu cancelTracking]</c>.</summary>
    public static void CancelTracking(nint menu) => Send(menu, Sel("cancelTracking"u8));

    /// <summary>The view's <c>window.frame</c> and the primary display's height, for <c>gfx::ScreenRectFromNSRect</c>.</summary>
    public static (double X, double Y, double Width, double Height, double PrimaryHeight) WindowFrame(nint view)
    {
        var window = Send(view, Sel("window"u8));
        var frame = window == 0 ? default : Frame(window);
        return (frame.X, frame.Y, frame.Width, frame.Height, PrimaryHeight());
    }

    /// <summary>The status item exists in the system status bar with a visible button; for tests.</summary>
    public static bool IsVisible(nint view)
        => s_views.TryGetValue(view, out var state) && state.StatusItem != 0 && (byte)Send(state.StatusItem, Sel("isVisible"u8)) != 0 && Button(view) != 0;

    private static nint Item(nint view) => s_views[view].StatusItem;

    private static nint Button(nint view) => Send(Item(view), Sel("button"u8));

    private static void UpdateDimensions(nint view) => SendRect(view, Sel("setFrame:"u8), Frame(Button(view)));

    private static nint Image(IReadOnlyList<(byte[] Png, double Scale)> representations, bool template)
    {
        if (representations.Count == 0) return 0;
        var image = Send(Send(Class("NSImage"u8), Sel("alloc"u8)), Sel("init"u8));
        NSPoint? size = null;
        WithPool(() =>
        {
            foreach (var (png, scale) in representations)
            {
                nint data;
                fixed (byte* p = png) data = ((delegate* unmanaged<nint, nint, byte*, nuint, nint>)s_msgSend)(Class("NSData"u8), Sel("dataWithBytes:length:"u8), p, (nuint)png.Length);
                var rep = Send(Class("NSBitmapImageRep"u8), Sel("imageRepWithData:"u8), data);
                if (rep == 0) continue;
                var points = new NSPoint { X = Send(rep, Sel("pixelsWide"u8)) / scale, Y = Send(rep, Sel("pixelsHigh"u8)) / scale };
                ((delegate* unmanaged<nint, nint, NSPoint, void>)s_msgSend)(rep, Sel("setSize:"u8), points);
                Send(image, Sel("addRepresentation:"u8), rep);
                size ??= points;
            }
        });
        // gfx's NSImageFromImageSkia sizes the image in DIPs.
        if (size is { } s) ((delegate* unmanaged<nint, nint, NSPoint, void>)s_msgSend)(image, Sel("setSize:"u8), s);
        if (template) ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(image, Sel("setTemplate:"u8), 1);
        return image;
    }

    private static nint Color(int rgb)
        => ((delegate* unmanaged<nint, nint, double, double, double, double, nint>)s_msgSend)(Class("NSColor"u8), Sel("colorWithCalibratedRed:green:blue:alpha:"u8),
            ((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0, 1.0);

    private static int? RgbOf(nint color)
    {
        if (color == 0) return null;
        var calibrated = Send(color, Sel("colorUsingColorSpace:"u8), Send(Class("NSColorSpace"u8), Sel("genericRGBColorSpace"u8)));
        if (calibrated == 0) return null;
        static int Channel(double v) => (int)Math.Round(v * 255);
        return (Channel(SendDoubleReturn(calibrated, Sel("redComponent"u8))) << 16) | (Channel(SendDoubleReturn(calibrated, Sel("greenComponent"u8))) << 8)
            | Channel(SendDoubleReturn(calibrated, Sel("blueComponent"u8)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRangeStruct
    {
        public nuint Location, Length;
    }

    private static void SetObject(nint dictionary, nint value, nint key) => Send(dictionary, Sel("setObject:forKey:"u8), value, key);

    // gfx's PrimaryDisplayHeight: NSMaxY of the first screen, the one with the menu bar.
    private static double PrimaryHeight()
    {
        var screens = Send(Class("NSScreen"u8), Sel("screens"u8));
        if (screens == 0 || Send(screens, Sel("count"u8)) == 0) return 0;
        var frame = Frame(Send(screens, Sel("objectAtIndex:"u8), 0));
        return frame.Y + frame.Height;
    }

    private static MacStatusItemEvent Read(nint e)
    {
        var location = ((delegate* unmanaged<nint, nint, NSPoint>)s_msgSend)(e, Sel("locationInWindow"u8));
        var window = Send(e, Sel("window"u8));
        var frame = window == 0 ? default : Frame(window);
        return new(location.X, location.Y, (ulong)Send(e, Sel("modifierFlags"u8)), Send(e, Sel("clickCount"u8)),
            frame.X, frame.Y, frame.Width, frame.Height, PrimaryHeight());
    }

    private static nint ViewClass()
    {
        if (s_viewClass != 0) return s_viewClass;
        s_nsView = Class("NSView"u8);
        fixed (byte* name = "OrielStatusItemView"u8)
        fixed (byte* v = "v@:@"u8)
        fixed (byte* v0 = "v@:"u8)
        fixed (byte* q = "Q@:@"u8)
        fixed (byte* b = "B@:@"u8)
        {
            var cls = objc_allocateClassPair(s_nsView, name, 0);
            class_addMethod(cls, Sel("mouseDown:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnMouseDown, v);
            class_addMethod(cls, Sel("mouseUp:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnMouseUp, v);
            class_addMethod(cls, Sel("rightMouseUp:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnRightMouseUp, v);
            class_addMethod(cls, Sel("mouseEntered:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnMouseEntered, v);
            class_addMethod(cls, Sel("mouseExited:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnMouseExited, v);
            class_addMethod(cls, Sel("mouseMoved:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnMouseMoved, v);
            class_addMethod(cls, Sel("updateTrackingAreas"u8), (delegate* unmanaged<nint, nint, void>)&OnUpdateTrackingAreas, v0);
            class_addMethod(cls, Sel("draggingEntered:"u8), (delegate* unmanaged<nint, nint, nint, nuint>)&OnDraggingEntered, q);
            class_addMethod(cls, Sel("draggingExited:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnDraggingExited, v);
            class_addMethod(cls, Sel("draggingEnded:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnDraggingEnded, v);
            class_addMethod(cls, Sel("prepareForDragOperation:"u8), (delegate* unmanaged<nint, nint, nint, byte>)&OnPrepareForDragOperation, b);
            class_addMethod(cls, Sel("performDragOperation:"u8), (delegate* unmanaged<nint, nint, nint, byte>)&OnPerformDragOperation, b);
            objc_registerClassPair(cls);
            s_viewClass = cls;
        }
        return s_viewClass;
    }

    private static void Signal(nint view, MacStatusItemSignal signal, in MacStatusItemEvent e)
    {
        if (!s_views.TryGetValue(view, out var state)) return;
        try { state.Handler.Signal(signal, e); }
        catch (Exception ex) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a tray event: {ex}"); }
    }

    [UnmanagedCallersOnly]
    private static void OnMouseDown(nint self, nint cmd, nint e)
    {
        // VoiceOver sends the button's action with the view, not an event: Electron makes a right mouse down at (0, 0)
        // and reports the click itself, as no mouseUp: follows.
        if (((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(e, Sel("respondsToSelector:"u8), Sel("locationInWindow"u8)) == 0)
        {
            var current = Send(App(), Sel("currentEvent"u8));
            var timestamp = current == 0 ? 0 : SendDoubleReturn(current, Sel("timestamp"u8));
            // NSEventTypeRightMouseDown
            var synthetic = ((delegate* unmanaged<nint, nint, nuint, NSPoint, nuint, double, nint, nint, nint, nint, float, nint>)s_msgSend)(
                Class("NSEvent"u8), Sel("mouseEventWithType:location:modifierFlags:timestamp:windowNumber:context:eventNumber:clickCount:pressure:"u8),
                3, default, 0, timestamp, 0, 0, 0, 1, 1.0f);
            if (synthetic == 0) return;
            e = synthetic;
            Signal(self, MacStatusItemSignal.Click, Read(e));
        }
        Signal(self, MacStatusItemSignal.MouseDown, Read(e));
        if (!s_views.TryGetValue(self, out var state)) return;
        // AppKit shows the menu from NSView's mouseDown:, which passes the event up to the button; no mouseUp: follows.
        if (state.ControllerMenu != 0 && Send(state.ControllerMenu, Sel("numberOfItems"u8)) > 0)
        {
            Signal(self, MacStatusItemSignal.Click, Read(e));
            if (!s_views.ContainsKey(self)) return;
            var super = new ObjcSuper { Receiver = self, SuperClass = s_nsView };
            ((delegate* unmanaged<ObjcSuper*, nint, nint, void>)s_msgSendSuper)(&super, cmd, e);
        }
        else ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(Button(self), Sel("highlight:"u8), 1);
    }

    private static readonly nint s_msgSendSuper = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSendSuper");

    [UnmanagedCallersOnly]
    private static void OnMouseUp(nint self, nint cmd, nint e)
    {
        if (!s_views.ContainsKey(self)) return;
        ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(Button(self), Sel("highlight:"u8), 0);
        Signal(self, MacStatusItemSignal.MouseUp, Read(e));
        Signal(self, MacStatusItemSignal.Click, Read(e));
    }

    [UnmanagedCallersOnly]
    private static void OnRightMouseUp(nint self, nint cmd, nint e) => Signal(self, MacStatusItemSignal.RightMouseUp, Read(e));

    [UnmanagedCallersOnly]
    private static void OnMouseEntered(nint self, nint cmd, nint e) => Signal(self, MacStatusItemSignal.MouseEntered, Read(e));

    [UnmanagedCallersOnly]
    private static void OnMouseExited(nint self, nint cmd, nint e) => Signal(self, MacStatusItemSignal.MouseExited, Read(e));

    [UnmanagedCallersOnly]
    private static void OnMouseMoved(nint self, nint cmd, nint e) => Signal(self, MacStatusItemSignal.MouseMoved, Read(e));

    [UnmanagedCallersOnly]
    private static void OnUpdateTrackingAreas(nint self, nint cmd)
    {
        if (!s_views.TryGetValue(self, out var state)) return;
        if (state.TrackingArea != 0)
        {
            Send(self, Sel("removeTrackingArea:"u8), state.TrackingArea);
            Send(state.TrackingArea, Sel("release"u8));
        }
        var bounds = Bounds(self);
        // NSTrackingMouseEnteredAndExited | NSTrackingMouseMoved | NSTrackingActiveAlways
        var area = ((delegate* unmanaged<nint, nint, NSRect, nuint, nint, nint, nint>)s_msgSend)(Send(Class("NSTrackingArea"u8), Sel("alloc"u8)),
            Sel("initWithRect:options:owner:userInfo:"u8), bounds, 0x01 | 0x02 | 0x80, self, 0);
        state.TrackingArea = area;
        Send(self, Sel("addTrackingArea:"u8), area);
    }

    [UnmanagedCallersOnly]
    private static nuint OnDraggingEntered(nint self, nint cmd, nint sender)
    {
        Signal(self, MacStatusItemSignal.DragEntered, default);
        // NSDragOperationCopy
        return 1;
    }

    [UnmanagedCallersOnly]
    private static void OnDraggingExited(nint self, nint cmd, nint sender) => Signal(self, MacStatusItemSignal.DragExited, default);

    [UnmanagedCallersOnly]
    private static void OnDraggingEnded(nint self, nint cmd, nint sender)
    {
        var location = ((delegate* unmanaged<nint, nint, NSPoint>)s_msgSend)(sender, Sel("draggingLocation"u8));
        var frame = Frame(self);
        Signal(self, MacStatusItemSignal.DragEnded, new MacStatusItemEvent(location.X, location.Y, 0, 0, frame.X, frame.Y, frame.Width, frame.Height, 0));
    }

    [UnmanagedCallersOnly]
    private static byte OnPrepareForDragOperation(nint self, nint cmd, nint sender) => 1;

    [UnmanagedCallersOnly]
    private static byte OnPerformDragOperation(nint self, nint cmd, nint sender)
    {
        if (!s_views.TryGetValue(self, out var state)) return 1;
        var board = Send(sender, Sel("draggingPasteboard"u8));
        var types = Send(board, Sel("types"u8));
        var filenames = Constant("NSFilenamesPboardType");
        var text = Constant("NSPasteboardTypeString");
        try
        {
            if (Contains(types, filenames))
            {
                var files = Send(board, Sel("propertyListForType:"u8), filenames);
                var count = files == 0 ? 0 : (int)Send(files, Sel("count"u8));
                var paths = new string[count];
                for (var i = 0; i < count; i++) paths[i] = ReadString(Send(files, Sel("objectAtIndex:"u8), i));
                state.Handler.DropFiles(paths);
            }
            else if (Contains(types, text)) state.Handler.DropText(ReadString(Send(board, Sel("stringForType:"u8), text)));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a tray drop: {ex}"); }
        return 1;
    }

    private static bool Contains(nint array, nint value)
        => array != 0 && ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(array, Sel("containsObject:"u8), value) != 0;

    private static nint App() => Send(Class("NSApplication"u8), Sel("sharedApplication"u8));

    // AppKit's exported NSString constants (pasteboard types, attribute names).
    private static nint Constant(string name) => *(nint*)NativeLibrary.GetExport(s_appKit, name);

    private static void WithPool(Action action)
    {
        var pool = objc_autoreleasePoolPush();
        try { action(); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    // NSRect is four doubles: arm64 returns it in registers, x64 through a hidden pointer (_stret).
    private static NSRect Frame(nint obj) => RectOf(obj, Sel("frame"u8));

    private static NSRect Bounds(nint obj) => RectOf(obj, Sel("bounds"u8));

    private static NSRect RectOf(nint obj, nint selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return ((delegate* unmanaged<nint, nint, NSRect>)s_msgSend)(obj, selector);
        NSRect rect;
        ((delegate* unmanaged<NSRect*, nint, nint, void>)s_msgSendStret)(&rect, obj, selector);
        return rect;
    }

    private static readonly nint s_msgSendStret = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0
        : NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend_stret");

    private static nint NSString(string value)
    {
        fixed (char* chars = value)
            return ((delegate* unmanaged<nint, nint, char*, nuint, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithCharacters:length:"u8), chars, (nuint)value.Length);
    }

    private static string ReadString(nint nsString)
        => nsString == 0 ? string.Empty : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String"u8))) ?? string.Empty;

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static nint Send(nint receiver, nint selector, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_msgSend)(receiver, selector, a, b);

    private static nint SendRect(nint receiver, nint selector, NSRect rect) => ((delegate* unmanaged<nint, nint, NSRect, nint>)s_msgSend)(receiver, selector, rect);

    private static nint SendDouble(nint receiver, nint selector, double value) => ((delegate* unmanaged<nint, nint, double, nint>)s_msgSend)(receiver, selector, value);

    private static nint SendDoubleDouble(nint receiver, nint selector, double a, double b) => ((delegate* unmanaged<nint, nint, double, double, nint>)s_msgSend)(receiver, selector, a, b);

    private static double SendDoubleReturn(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, double>)s_msgSend)(receiver, selector);

    private static nint SendArray(nint receiver, nint selector, nint* objects, nuint count)
        => ((delegate* unmanaged<nint, nint, nint*, nuint, nint>)s_msgSend)(receiver, selector, objects, count);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
}
