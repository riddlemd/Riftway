using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>What one Touch Bar's delegate and target report: the calls AppKit makes on <c>ElectronTouchBar</c>.</summary>
public interface IMacTouchBarHandler
{
    /// <summary><c>touchBar:makeItemForIdentifier:</c>: an autoreleased item, or 0.</summary>
    nint MakeItem(string identifier);

    void ButtonAction(long tag);

    void ColorPickerAction(string identifier, uint argb);

    void SliderAction(string identifier, int value);

    void SegmentedControlAction(long tag, int selectedIndex, bool isSelected);

    void ScrubberSelected(string scrubberId, int index);

    void ScrubberHighlighted(string scrubberId, int index);

    int ScrubberCount(string scrubberId);

    /// <summary>The scrubber item's text, or null for an image item with <paramref name="image"/> (an NSImage, +0, or 0);
    /// false when there is no such item.</summary>
    bool ScrubberItem(string scrubberId, int index, out string? label, out nint image);
}

/// <summary>The AppKit side of Electron's <c>ElectronTouchBar</c>: NSTouchBar and its items, and one object per bar that
/// is its delegate, its controls' target and its scrubbers' delegate and data source. Main thread only.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacTouchBar
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string TextScrubberItem = "scrubber.text.item";
    private const string ImageScrubberItem = "scrubber.image.item";
    // NSImageScaleProportionallyUpOrDown; NSStringDrawingUsesLineFragmentOrigin | NSStringDrawingUsesFontLeading.
    private const nuint ImageScaleProportionallyUpOrDown = 3;
    private const nuint DrawingOptions = 1 | 2;

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_msgSendStret = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? 0 : NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend_stret");
    private static readonly nint s_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    private static readonly Dictionary<nint, IMacTouchBarHandler> s_handlers = [];
    private static nint s_class;

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize
    {
        public double Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }

    /// <summary>AppKit's identifier constants: <c>NSTouchBarItemIdentifierFixedSpaceSmall</c> and the like.</summary>
    public static string SystemIdentifier(string name) => ReadString(*(nint*)NativeLibrary.GetExport(s_appKit, name));

    /// <summary>A new delegate and target for one bar. It lives for the process: AppKit holds it unretained, and a bar
    /// replaced from under a still-open popover can still call it.</summary>
    public static nint CreateDelegate(IMacTouchBarHandler handler)
    {
        var target = Send(Send(DelegateClass(), Sel("alloc"u8)), Sel("init"u8));
        s_handlers[target] = handler;
        return target;
    }

    /// <summary><c>touchBarFromItemIdentifiers:</c>: an autoreleased NSTouchBar.</summary>
    public static nint CreateTouchBar(nint target, IReadOnlyList<string> identifiers)
    {
        var bar = Autorelease(Send(Send(Class("NSTouchBar"u8), Sel("alloc"u8)), Sel("init"u8)));
        Send(bar, Sel("setDelegate:"u8), target);
        Send(bar, Sel("setDefaultItemIdentifiers:"u8), StringArray(identifiers));
        return bar;
    }

    public static nint ItemForIdentifier(nint touchBar, string identifier) => touchBar == 0 ? 0 : Send(touchBar, Sel("itemForIdentifier:"u8), NSString(identifier));

    public static nint PopoverTouchBar(nint item) => item == 0 ? 0 : Send(item, Sel("popoverTouchBar"u8));

    public static nint GroupTouchBar(nint item) => item == 0 ? 0 : Send(item, Sel("groupTouchBar"u8));

    public static bool CanReplaceEscapeKey(nint touchBar)
        => touchBar != 0 && ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(touchBar, Sel("respondsToSelector:"u8), Sel("escapeKeyReplacementItemIdentifier"u8)) != 0;

    public static void SetEscapeKeyReplacement(nint touchBar, string? identifier)
        => Send(touchBar, Sel("setEscapeKeyReplacementItemIdentifier:"u8), identifier is null ? 0 : NSString(identifier));

    /// <summary><c>makeButtonForID:</c>: an NSCustomTouchBarItem around an NSButton sent to the target.</summary>
    public static nint MakeButton(nint target, string identifier, long tag)
    {
        var button = ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)s_msgSend)(Class("NSButton"u8), Sel("buttonWithTitle:target:action:"u8),
            NSString(""), target, Sel("riftwayButtonAction:"u8));
        SendLong(button, Sel("setTag:"u8), tag);
        return CustomItem(identifier, button);
    }

    public static void UpdateButton(nint item, string title, string accessibilityLabel, uint? bezelArgb, nint image, long imagePosition, bool enabled)
    {
        var button = Send(item, Sel("view"u8));
        Send(button, Sel("setBezelColor:"u8), bezelArgb is { } bezel ? DeviceColor(bezel) : 0);
        Send(button, Sel("setAccessibilityLabel:"u8), NSString(accessibilityLabel));
        Send(button, Sel("setTitle:"u8), NSString(title));
        if (image != 0)
        {
            Send(button, Sel("setImage:"u8), image);
            SendLong(button, Sel("setImagePosition:"u8), imagePosition);
        }
        SendBool(button, Sel("setEnabled:"u8), enabled);
    }

    /// <summary><c>makeLabelForID:</c>: an NSCustomTouchBarItem around a label text field.</summary>
    public static nint MakeLabel(string identifier) => CustomItem(identifier, Send(Class("NSTextField"u8), Sel("labelWithString:"u8), NSString("")));

    public static void UpdateLabel(nint item, string text, string accessibilityLabel, uint? textArgb)
    {
        var field = Send(item, Sel("view"u8));
        Send(field, Sel("setStringValue:"u8), NSString(text));
        Send(field, Sel("setAccessibilityLabel:"u8), NSString(accessibilityLabel));
        Send(field, Sel("setTextColor:"u8), textArgb is { } color ? DeviceColor(color) : 0);
    }

    public static nint MakeColorPicker(nint target, string identifier)
    {
        var item = Autorelease(Send(Send(Class("NSColorPickerTouchBarItem"u8), Sel("alloc"u8)), Sel("initWithIdentifier:"u8), NSString(identifier)));
        Send(item, Sel("setTarget:"u8), target);
        Send(item, Sel("setAction:"u8), Sel("riftwayColorPickerAction:"u8));
        return item;
    }

    public static void UpdateColorPicker(nint item, IReadOnlyList<(uint Argb, string Key)>? colors, uint? selectedArgb)
    {
        if (colors is not null)
        {
            var list = Autorelease(Send(Send(Class("NSColorList"u8), Sel("alloc"u8)), Sel("initWithName:"u8), NSString("")));
            for (var i = 0; i < colors.Count; i++)
                ((delegate* unmanaged<nint, nint, nint, nint, nuint, void>)s_msgSend)(list, Sel("insertColor:key:atIndex:"u8), DeviceColor(colors[i].Argb), NSString(colors[i].Key), (nuint)i);
            Send(item, Sel("setColorList:"u8), list);
        }
        if (selectedArgb is { } selected) Send(item, Sel("setColor:"u8), DeviceColor(selected));
    }

    public static nint MakeSlider(nint target, string identifier)
    {
        var item = Autorelease(Send(Send(Class("NSSliderTouchBarItem"u8), Sel("alloc"u8)), Sel("initWithIdentifier:"u8), NSString(identifier)));
        Send(item, Sel("setTarget:"u8), target);
        Send(item, Sel("setAction:"u8), Sel("riftwaySliderAction:"u8));
        return item;
    }

    public static void UpdateSlider(nint item, string label, double minValue, double maxValue, double value)
    {
        Send(item, Sel("setLabel:"u8), NSString(label));
        var slider = Send(item, Sel("slider"u8));
        SendDouble(slider, Sel("setMinValue:"u8), minValue);
        SendDouble(slider, Sel("setMaxValue:"u8), maxValue);
        SendDouble(slider, Sel("setDoubleValue:"u8), value);
    }

    public static nint MakePopover(string identifier)
        => Autorelease(Send(Send(Class("NSPopoverTouchBarItem"u8), Sel("alloc"u8)), Sel("initWithIdentifier:"u8), NSString(identifier)));

    public static void UpdatePopover(nint item, string label, nint image, bool showsCloseButton, nint popoverTouchBar)
    {
        Send(item, Sel("setCollapsedRepresentationLabel:"u8), NSString(label));
        if (image != 0) Send(item, Sel("setCollapsedRepresentationImage:"u8), image);
        SendBool(item, Sel("setShowsCloseButton:"u8), showsCloseButton);
        Send(item, Sel("setPopoverTouchBar:"u8), popoverTouchBar);
    }

    public static nint MakeGroup(string identifier, IReadOnlyList<nint> items)
        => ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_msgSend)(Class("NSGroupTouchBarItem"u8), Sel("groupItemWithIdentifier:items:"u8),
            NSString(identifier), ObjectArray(items));

    public static void UpdateGroup(nint item, nint groupTouchBar) => Send(item, Sel("setGroupTouchBar:"u8), groupTouchBar);

    public static nint MakeSegmentedControl(nint target, string identifier, long tag)
    {
        var control = ((delegate* unmanaged<nint, nint, nint, nuint, nint, nint, nint>)s_msgSend)(Class("NSSegmentedControl"u8),
            Sel("segmentedControlWithLabels:trackingMode:target:action:"u8), Send(Class("NSMutableArray"u8), Sel("array"u8)), 0, target, Sel("riftwaySegmentedControlAction:"u8));
        SendLong(control, Sel("setTag:"u8), tag);
        return CustomItem(identifier, control);
    }

    /// <summary><c>updateSegmentedControl:</c>; <paramref name="images"/> are NSImages (+0) or 0, one per segment.</summary>
    public static void UpdateSegmentedControl(nint item, long segmentStyle, long trackingMode, IReadOnlyList<(string Label, bool Enabled)> segments,
        IReadOnlyList<nint> images, int? selectedIndex)
    {
        var control = Send(item, Sel("view"u8));
        SendLong(control, Sel("setSegmentStyle:"u8), segmentStyle);
        SendLong(control, Sel("setTrackingMode:"u8), trackingMode);
        SendLong(control, Sel("setSegmentCount:"u8), segments.Count);
        for (var i = 0; i < segments.Count; i++)
        {
            ((delegate* unmanaged<nint, nint, nint, nint, void>)s_msgSend)(control, Sel("setLabel:forSegment:"u8), NSString(segments[i].Label), i);
            ((delegate* unmanaged<nint, nint, nint, nint, void>)s_msgSend)(control, Sel("setImage:forSegment:"u8), images[i], i);
            if (images[i] != 0)
                ((delegate* unmanaged<nint, nint, nuint, nint, void>)s_msgSend)(control, Sel("setImageScaling:forSegment:"u8), ImageScaleProportionallyUpOrDown, i);
            ((delegate* unmanaged<nint, nint, byte, nint, void>)s_msgSend)(control, Sel("setEnabled:forSegment:"u8), segments[i].Enabled ? (byte)1 : (byte)0, i);
        }
        if (selectedIndex is { } index) SendLong(control, Sel("setSelectedSegment:"u8), index);
    }

    /// <summary><c>makeScrubberForID:</c>: an NSScrubber whose identifier is the item's id, fed by the target.</summary>
    public static nint MakeScrubber(nint target, string identifier, string scrubberId)
    {
        var scrubber = Autorelease(((delegate* unmanaged<nint, nint, NSRect, nint>)s_msgSend)(Send(Class("NSScrubber"u8), Sel("alloc"u8)), Sel("initWithFrame:"u8), default));
        ((delegate* unmanaged<nint, nint, nint, nint, void>)s_msgSend)(scrubber, Sel("registerClass:forItemIdentifier:"u8), Class("NSScrubberTextItemView"u8), NSString(TextScrubberItem));
        ((delegate* unmanaged<nint, nint, nint, nint, void>)s_msgSend)(scrubber, Sel("registerClass:forItemIdentifier:"u8), Class("NSScrubberImageItemView"u8), NSString(ImageScrubberItem));
        Send(scrubber, Sel("setDelegate:"u8), target);
        Send(scrubber, Sel("setDataSource:"u8), target);
        Send(scrubber, Sel("setIdentifier:"u8), NSString(scrubberId));
        return CustomItem(identifier, scrubber);
    }

    /// <summary><c>updateScrubber:</c>; a style is <c>outline</c>, <c>background</c> or null for none.</summary>
    public static void UpdateScrubber(nint item, bool? showsArrowButtons, string? selectionBackgroundStyle, string? selectionOverlayStyle, long mode, bool continuous)
    {
        var scrubber = Send(item, Sel("view"u8));
        if (showsArrowButtons is { } arrows) SendBool(scrubber, Sel("setShowsArrowButtons:"u8), arrows);
        Send(scrubber, Sel("setSelectionBackgroundStyle:"u8), SelectionStyle(selectionBackgroundStyle));
        Send(scrubber, Sel("setSelectionOverlayStyle:"u8), SelectionStyle(selectionOverlayStyle));
        SendLong(scrubber, Sel("setMode:"u8), mode);
        SendBool(scrubber, Sel("setContinuous:"u8), continuous);
        Send(scrubber, Sel("reloadData"u8));
    }

    /// <summary>A pool for calls made outside AppKit's own callbacks; pass the result to <see cref="PopPool"/>.</summary>
    public static nint PushPool() => objc_autoreleasePoolPush();

    public static void PopPool(nint pool) => objc_autoreleasePoolPop(pool);

    /// <summary>Takes ownership of a +1 object into the current pool.</summary>
    public static nint Autorelease(nint obj) => obj == 0 ? 0 : Send(obj, Sel("autorelease"u8));

    private static nint SelectionStyle(string? style) => style switch
    {
        "outline" => Send(Class("NSScrubberSelectionStyle"u8), Sel("outlineOverlayStyle"u8)),
        "background" => Send(Class("NSScrubberSelectionStyle"u8), Sel("roundedBackgroundStyle"u8)),
        _ => 0,
    };

    private static nint CustomItem(string identifier, nint view)
    {
        var item = Autorelease(Send(Send(Class("NSCustomTouchBarItem"u8), Sel("alloc"u8)), Sel("initWithIdentifier:"u8), NSString(identifier)));
        Send(item, Sel("setView:"u8), view);
        return item;
    }

    /// <summary>skia's <c>SkColorToDeviceNSColor</c>.</summary>
    public static nint DeviceColor(uint argb)
        => DeviceColor(((argb >> 16) & 0xFF) / 255.0, ((argb >> 8) & 0xFF) / 255.0, (argb & 0xFF) / 255.0, (argb >> 24) / 255.0);

    /// <summary><c>colorWithDeviceRed:green:blue:alpha:</c>.</summary>
    public static nint DeviceColor(double red, double green, double blue, double alpha)
        => ((delegate* unmanaged<nint, nint, double, double, double, double, nint>)s_msgSend)(Class("NSColor"u8), Sel("colorWithDeviceRed:green:blue:alpha:"u8),
            red, green, blue, alpha);

    /// <summary>skia's <c>NSDeviceColorToSkColor</c>: the colour in device RGB, each component rounded half up to a byte,
    /// as <c>SkScalarRoundToInt</c>.</summary>
    public static uint ArgbOf(nint color)
    {
        var device = Send(color, Sel("colorUsingColorSpace:"u8), Send(Class("NSColorSpace"u8), Sel("deviceRGBColorSpace"u8)));
        if (device == 0) return 0xFF000000;
        uint Component(ReadOnlySpan<byte> selector) => (uint)Math.Clamp(Math.Floor(((delegate* unmanaged<nint, nint, double>)s_msgSend)(device, Sel(selector)) * 255 + 0.5), 0, 255);
        return (Component("alphaComponent"u8) << 24) | (Component("redComponent"u8) << 16) | (Component("greenComponent"u8) << 8) | Component("blueComponent"u8);
    }

    private static nint DelegateClass()
    {
        if (s_class != 0) return s_class;
        fixed (byte* name = "RiftwayTouchBarDelegate"u8)
        {
            var cls = objc_getClass(name);
            if (cls == 0)
            {
                cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
                Add(cls, "touchBar:makeItemForIdentifier:"u8, (delegate* unmanaged<nint, nint, nint, nint, nint>)&OnMakeItem, "@@:@@"u8);
                Add(cls, "riftwayButtonAction:"u8, (delegate* unmanaged<nint, nint, nint, void>)&OnButton, "v@:@"u8);
                Add(cls, "riftwayColorPickerAction:"u8, (delegate* unmanaged<nint, nint, nint, void>)&OnColorPicker, "v@:@"u8);
                Add(cls, "riftwaySliderAction:"u8, (delegate* unmanaged<nint, nint, nint, void>)&OnSlider, "v@:@"u8);
                Add(cls, "riftwaySegmentedControlAction:"u8, (delegate* unmanaged<nint, nint, nint, void>)&OnSegmentedControl, "v@:@"u8);
                Add(cls, "scrubber:didSelectItemAtIndex:"u8, (delegate* unmanaged<nint, nint, nint, nint, void>)&OnScrubberSelect, "v@:@q"u8);
                Add(cls, "scrubber:didHighlightItemAtIndex:"u8, (delegate* unmanaged<nint, nint, nint, nint, void>)&OnScrubberHighlight, "v@:@q"u8);
                Add(cls, "numberOfItemsForScrubber:"u8, (delegate* unmanaged<nint, nint, nint, nint>)&OnScrubberCount, "q@:@"u8);
                Add(cls, "scrubber:viewForItemAtIndex:"u8, (delegate* unmanaged<nint, nint, nint, nint, nint>)&OnScrubberView, "@@:@q"u8);
                Add(cls, "scrubber:layout:sizeForItemAtIndex:"u8, (delegate* unmanaged<nint, nint, nint, nint, nint, NSSize>)&OnScrubberSize, "{CGSize=dd}@:@@q"u8);
                foreach (var protocol in new[] { "NSTouchBarDelegate", "NSScrubberDelegate", "NSScrubberDataSource", "NSScrubberFlowLayoutDelegate" })
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(protocol + "\0");
                    fixed (byte* p = bytes)
                        if (objc_getProtocol(p) is var proto and not 0) class_addProtocol(cls, proto);
                }
                objc_registerClassPair(cls);
            }
            return s_class = cls;
        }
    }

    private static void Add(nint cls, ReadOnlySpan<byte> selector, void* imp, ReadOnlySpan<byte> types)
    {
        fixed (byte* t = types) class_addMethod(cls, Sel(selector), imp, t);
    }

    private static T Guard<T>(nint self, Func<IMacTouchBarHandler, T> call, T fallback)
    {
        if (!s_handlers.TryGetValue(self, out var handler)) return fallback;
        try { return call(handler); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Riftway] Unhandled exception in a Touch Bar callback: {e}");
            return fallback;
        }
    }

    [UnmanagedCallersOnly]
    private static nint OnMakeItem(nint self, nint cmd, nint touchBar, nint identifier) => Guard(self, h => h.MakeItem(ReadString(identifier)), 0);

    [UnmanagedCallersOnly]
    private static void OnButton(nint self, nint cmd, nint sender) => Guard(self, h => { h.ButtonAction(Send(sender, Sel("tag"u8))); return 0; }, 0);

    [UnmanagedCallersOnly]
    private static void OnColorPicker(nint self, nint cmd, nint sender)
        => Guard(self, h => { h.ColorPickerAction(ReadString(Send(sender, Sel("identifier"u8))), ArgbOf(Send(sender, Sel("color"u8)))); return 0; }, 0);

    [UnmanagedCallersOnly]
    private static void OnSlider(nint self, nint cmd, nint sender)
        => Guard(self, h => { h.SliderAction(ReadString(Send(sender, Sel("identifier"u8))), ((delegate* unmanaged<nint, nint, int>)s_msgSend)(Send(sender, Sel("slider"u8)), Sel("intValue"u8))); return 0; }, 0);

    [UnmanagedCallersOnly]
    private static void OnSegmentedControl(nint self, nint cmd, nint sender) => Guard(self, h =>
    {
        var selected = Send(sender, Sel("selectedSegment"u8));
        var isSelected = ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(sender, Sel("isSelectedForSegment:"u8), selected) != 0;
        h.SegmentedControlAction(Send(sender, Sel("tag"u8)), (int)selected, isSelected);
        return 0;
    }, 0);

    [UnmanagedCallersOnly]
    private static void OnScrubberSelect(nint self, nint cmd, nint scrubber, nint index)
        => Guard(self, h => { h.ScrubberSelected(ReadString(Send(scrubber, Sel("identifier"u8))), (int)index); return 0; }, 0);

    [UnmanagedCallersOnly]
    private static void OnScrubberHighlight(nint self, nint cmd, nint scrubber, nint index)
        => Guard(self, h => { h.ScrubberHighlighted(ReadString(Send(scrubber, Sel("identifier"u8))), (int)index); return 0; }, 0);

    [UnmanagedCallersOnly]
    private static nint OnScrubberCount(nint self, nint cmd, nint scrubber) => Guard(self, h => (nint)h.ScrubberCount(ReadString(Send(scrubber, Sel("identifier"u8)))), 0);

    // viewForItemAtIndex: a text view for a labelled item, an image view otherwise, an empty view for none.
    [UnmanagedCallersOnly]
    private static nint OnScrubberView(nint self, nint cmd, nint scrubber, nint index)
        // The method's result is non-null, as Electron's always is; an unknown scrubber or a failure gets an empty view.
        => ScrubberView(self, scrubber, index) is var view and not 0 ? view : EmptyItemView();

    private static nint ScrubberView(nint self, nint scrubber, nint index) => Guard(self, h =>
    {
        if (!h.ScrubberItem(ReadString(Send(scrubber, Sel("identifier"u8))), (int)index, out var label, out var image)) return EmptyItemView();
        var view = Send(scrubber, Sel("makeItemWithIdentifier:owner:"u8), NSString(label is null ? ImageScrubberItem : TextScrubberItem), self);
        if (view == 0) return EmptyItemView();
        if (label is not null) Send(view, Sel("setTitle:"u8), NSString(label));
        else if (image != 0) Send(view, Sel("setImage:"u8), image);
        return view;
    }, 0);

    private static nint EmptyItemView()
        => Autorelease(((delegate* unmanaged<nint, nint, NSRect, nint>)s_msgSend)(Send(Class("NSScrubberItemView"u8), Sel("alloc"u8)), Sel("initWithFrame:"u8), default));

    // sizeForItemAtIndex: 30 high; a label's width plus a 15 margin, an image's own width, else 50.
    [UnmanagedCallersOnly]
    private static NSSize OnScrubberSize(nint self, nint cmd, nint scrubber, nint layout, nint index) => Guard(self, h =>
    {
        var size = new NSSize { Width = 50, Height = 30 };
        if (!h.ScrubberItem(ReadString(Send(scrubber, Sel("identifier"u8))), (int)index, out var label, out var image)) return size;
        if (label is not null)
        {
            var font = ((delegate* unmanaged<nint, nint, double, nint>)s_msgSend)(Class("NSFont"u8), Sel("systemFontOfSize:"u8), 0);
            var attributes = Send(Class("NSDictionary"u8), Sel("dictionaryWithObject:forKey:"u8), font, *(nint*)NativeLibrary.GetExport(s_appKit, "NSFontAttributeName"));
            var bounds = new NSSize { Width = double.MaxValue, Height = double.MaxValue };
            NSRect rect;
            // NSRect is four doubles: arm64 returns it in registers, x64 through a hidden pointer (_stret).
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                rect = ((delegate* unmanaged<nint, nint, NSSize, nuint, nint, NSRect>)s_msgSend)(NSString(label), Sel("boundingRectWithSize:options:attributes:"u8), bounds, DrawingOptions, attributes);
            else ((delegate* unmanaged<NSRect*, nint, nint, NSSize, nuint, nint, void>)s_msgSendStret)(&rect, NSString(label), Sel("boundingRectWithSize:options:attributes:"u8), bounds, DrawingOptions, attributes);
            size.Width = (long)(rect.Width + 15);
        }
        else if (image != 0) size.Width = (long)((delegate* unmanaged<nint, nint, NSSize>)s_msgSend)(image, Sel("size"u8)).Width;
        return size;
    }, new NSSize { Width = 50, Height = 30 });

    private static nint StringArray(IReadOnlyList<string> values)
    {
        var array = Send(Class("NSMutableArray"u8), Sel("array"u8));
        foreach (var value in values) Send(array, Sel("addObject:"u8), NSString(value));
        return array;
    }

    private static nint ObjectArray(IReadOnlyList<nint> values)
    {
        var array = Send(Class("NSMutableArray"u8), Sel("array"u8));
        foreach (var value in values) Send(array, Sel("addObject:"u8), value);
        return array;
    }

    private static string ReadString(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"u8))) ?? "";

    private static nint NSString(string s)
    {
        fixed (char* p = s)
            return ((delegate* unmanaged<nint, nint, char*, nuint, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithCharacters:length:"u8), p, (nuint)s.Length);
    }

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

    private static void SendLong(nint receiver, nint selector, long value) => ((delegate* unmanaged<nint, nint, nint, void>)s_msgSend)(receiver, selector, (nint)value);

    private static void SendBool(nint receiver, nint selector, bool value) => ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(receiver, selector, value ? (byte)1 : (byte)0);

    private static void SendDouble(nint receiver, nint selector, double value) => ((delegate* unmanaged<nint, nint, double, void>)s_msgSend)(receiver, selector, value);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_getProtocol(byte* name);
    [DllImport(ObjC)] private static extern byte class_addProtocol(nint cls, nint protocol);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
}
