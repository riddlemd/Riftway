using System.Runtime.Versioning;
using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>What <c>updateButton:</c> writes to a button: AppKit's values. A null image leaves the button's image.</summary>
internal sealed record MacTouchBarButton(string Title, string AccessibilityLabel, uint? BezelArgb, NativeImage? Image, long ImagePosition, bool Enabled);

/// <summary>What <c>updateLabel:</c> writes to a text field.</summary>
internal sealed record MacTouchBarLabel(string Text, string AccessibilityLabel, uint? TextArgb);

/// <summary>What <c>updateColorPicker:</c> writes; a null list or colour leaves the picker's.</summary>
internal sealed record MacTouchBarColorPicker(IReadOnlyList<(uint Argb, string Key)>? Colors, uint? SelectedArgb);

/// <summary>What <c>updateSlider:</c> writes.</summary>
internal sealed record MacTouchBarSlider(string Label, double MinValue, double MaxValue, double Value);

/// <summary>What <c>updatePopover:</c> writes; a null image leaves the popover's.</summary>
internal sealed record MacTouchBarPopover(string Label, NativeImage? Image, bool ShowsCloseButton, nint PopoverTouchBar);

/// <summary>A segment as <c>updateSegmentedControl:</c> writes it.</summary>
internal sealed record MacTouchBarSegment(string Label, NativeImage? Image, bool Enabled);

/// <summary>What <c>updateSegmentedControl:</c> writes; a null index leaves the selection.</summary>
internal sealed record MacTouchBarSegmentedControl(long SegmentStyle, long TrackingMode, IReadOnlyList<MacTouchBarSegment> Segments, int? SelectedIndex);

/// <summary>What <c>updateScrubber:</c> writes; null arrows are left as they are.</summary>
internal sealed record MacTouchBarScrubber(bool? ShowsArrowButtons, string? SelectionBackgroundStyle, string? SelectionOverlayStyle, long Mode, bool Continuous);

/// <summary>The AppKit calls <see cref="MacTouchBarController"/> makes, bound to one controller's delegate and target;
/// a seam for tests.</summary>
internal interface IMacTouchBarUi
{
    string FixedSpaceSmall { get; }
    string FixedSpaceLarge { get; }
    string FlexibleSpace { get; }
    string OtherItemsProxy { get; }
    nint CreateTouchBar(IReadOnlyList<string> identifiers);
    nint ItemForIdentifier(nint touchBar, string identifier);
    nint PopoverTouchBar(nint popoverItem);
    nint GroupTouchBar(nint groupItem);
    bool CanReplaceEscapeKey(nint touchBar);
    void SetEscapeKeyReplacement(nint touchBar, string? identifier);
    nint MakeButton(string identifier, long tag);
    nint MakeLabel(string identifier);
    nint MakeColorPicker(string identifier);
    nint MakeSlider(string identifier);
    nint MakePopover(string identifier);
    nint MakeGroup(string identifier, IReadOnlyList<nint> items);
    nint MakeSegmentedControl(string identifier, long tag);
    nint MakeScrubber(string identifier, string scrubberId);
    void UpdateButton(nint item, MacTouchBarButton state);
    void UpdateLabel(nint item, MacTouchBarLabel state);
    void UpdateColorPicker(nint item, MacTouchBarColorPicker state);
    void UpdateSlider(nint item, MacTouchBarSlider state);
    void UpdatePopover(nint item, MacTouchBarPopover state);
    void UpdateGroup(nint item, nint groupTouchBar);
    void UpdateSegmentedControl(nint item, MacTouchBarSegmentedControl state);
    void UpdateScrubber(nint item, MacTouchBarScrubber state);
}

/// <summary>Electron's <c>ElectronTouchBar</c> (electron_touch_bar.mm): NSTouchBar items made from the window's
/// <see cref="TouchBarItem"/>s, read live as Electron reads its persistent dictionaries, with uses reported back as
/// <c>-touch-bar-interaction</c>. Main thread.</summary>
internal sealed class MacTouchBarController
{
    internal const string ButtonPrefix = "com.electron.touchbar.button.";
    internal const string ColorPickerPrefix = "com.electron.touchbar.colorpicker.";
    internal const string GroupPrefix = "com.electron.touchbar.group.";
    internal const string LabelPrefix = "com.electron.touchbar.label.";
    internal const string PopoverPrefix = "com.electron.touchbar.popover.";
    internal const string SliderPrefix = "com.electron.touchbar.slider.";
    internal const string SegmentedControlPrefix = "com.electron.touchbar.segmentedcontrol.";
    internal const string ScrubberPrefix = "com.electron.touchbar.scrubber.";
    // AppKit's NSImageLeft, NSImageRight, NSImageOverlaps.
    private const long ImageLeft = 2, ImageRight = 3, ImageOverlaps = 6;
    private const uint White = 0xFFFFFFFF;

    private readonly IReadOnlyList<TouchBarItem> _ordered;
    private readonly Action<string, TouchBarInteraction> _interaction;
    // settings_: every item an identifier list has named, kept for the controller's life.
    private readonly Dictionary<string, TouchBarItem> _settings = [];
    private IMacTouchBarUi _ui = null!;

    internal MacTouchBarController(IReadOnlyList<TouchBarItem> orderedItems, Action<string, TouchBarInteraction> interaction)
        => (_ordered, _interaction) = (orderedItems, interaction);

    /// <summary>Binds the AppKit side, which calls back into this controller.</summary>
    internal void Attach(IMacTouchBarUi ui) => _ui = ui;

    /// <summary><c>makeTouchBar</c>: the window's bar.</summary>
    internal nint MakeTouchBar() => _ui.CreateTouchBar(IdentifiersFrom(_ordered));

    /// <summary><c>identifiersFromSettings:</c>: each item's identifier (spacers and the proxy as AppKit's), registering
    /// it, with AppKit's other-items proxy appended when the items have none.</summary>
    internal List<string> IdentifiersFrom(IEnumerable<TouchBarItem> items)
    {
        var identifiers = new List<string>();
        var hasProxy = false;
        foreach (var item in items)
        {
            string? identifier;
            if (item is TouchBarSpacer spacer)
                identifier = spacer.Size switch
                {
                    TouchBarSpacerSize.Large => _ui.FixedSpaceLarge,
                    TouchBarSpacerSize.Flexible => _ui.FlexibleSpace,
                    _ => _ui.FixedSpaceSmall,
                };
            else if (item is TouchBarOtherItemsProxy)
            {
                identifier = _ui.OtherItemsProxy;
                hasProxy = true;
            }
            else identifier = Identifier(item.Id, item.Type);
            if (identifier is null) continue;
            _settings[item.Id] = item;
            identifiers.Add(identifier);
        }
        if (!hasProxy) identifiers.Add(_ui.OtherItemsProxy);
        return identifiers;
    }

    /// <summary><c>identifierFromID:type:</c>: the prefix for the item's type and its id; null for other types.</summary>
    internal static string? Identifier(string id, string type)
    {
        var prefix = type switch
        {
            "button" => ButtonPrefix,
            "label" => LabelPrefix,
            "colorpicker" => ColorPickerPrefix,
            "slider" => SliderPrefix,
            "popover" => PopoverPrefix,
            "group" => GroupPrefix,
            "segmented_control" => SegmentedControlPrefix,
            "scrubber" => ScrubberPrefix,
            _ => null,
        };
        return prefix is null ? null : prefix + id;
    }

    /// <summary><c>makeItemForIdentifier:</c>, the bar delegate's <c>touchBar:makeItemForIdentifier:</c>.</summary>
    internal nint MakeItem(string identifier)
    {
        if (Strip(identifier, ButtonPrefix) is { } button) return MakeButton(button, identifier);
        if (Strip(identifier, LabelPrefix) is { } label) return Known(label) is TouchBarLabel l ? Updated(_ui.MakeLabel(identifier), l) : 0;
        if (Strip(identifier, ColorPickerPrefix) is { } picker) return Known(picker) is TouchBarColorPicker p ? Updated(_ui.MakeColorPicker(identifier), p) : 0;
        if (Strip(identifier, SliderPrefix) is { } slider) return Known(slider) is TouchBarSlider s ? Updated(_ui.MakeSlider(identifier), s) : 0;
        if (Strip(identifier, PopoverPrefix) is { } popover) return Known(popover) is TouchBarPopover o ? Updated(_ui.MakePopover(identifier), o) : 0;
        if (Strip(identifier, GroupPrefix) is { } group) return MakeGroup(group, identifier);
        if (Strip(identifier, SegmentedControlPrefix) is { } segments)
            return Known(segments) is TouchBarSegmentedControl c ? Updated(_ui.MakeSegmentedControl(identifier, Tag(segments)), c) : 0;
        if (Strip(identifier, ScrubberPrefix) is { } scrubber) return Known(scrubber) is TouchBarScrubber r ? Updated(_ui.MakeScrubber(identifier, scrubber), r) : 0;
        return 0;
    }

    private static string? Strip(string identifier, string prefix) => identifier.StartsWith(prefix, StringComparison.Ordinal) ? identifier[prefix.Length..] : null;

    private TouchBarItem? Known(string id) => _settings.GetValueOrDefault(id);

    // [id floatValue] as an NSInteger tag.
    internal static long Tag(string id) => double.TryParse(id, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (long)v : 0;

    private nint MakeButton(string id, string identifier)
        => Known(id) is TouchBarButton b ? Updated(_ui.MakeButton(identifier, Tag(id)), b) : 0;

    // makeGroupForID: the group's own items, made now, without the proxy.
    private nint MakeGroup(string id, string identifier)
    {
        if (Known(id) is not TouchBarGroup group) return 0;
        var made = new List<nint>();
        foreach (var child in IdentifiersFrom(group.Items.OrderedItems))
            if (child != _ui.OtherItemsProxy && MakeItem(child) is var item and not 0) made.Add(item);
        return _ui.MakeGroup(identifier, made);
    }

    private nint Updated(nint item, TouchBarItem settings)
    {
        Update(item, settings);
        return item;
    }

    private void Update(nint item, TouchBarItem settings)
    {
        switch (settings)
        {
            case TouchBarButton b:
                _ui.UpdateButton(item, new MacTouchBarButton(b.Label ?? "", b.AccessibilityLabel ?? "",
                    b.BackgroundColor is { Length: > 0 } bg ? Color(bg) : null, b.Icon,
                    b.IconPosition switch { TouchBarIconPosition.Left => ImageLeft, TouchBarIconPosition.Right => ImageRight, _ => ImageOverlaps }, b.Enabled));
                break;
            case TouchBarLabel l:
                _ui.UpdateLabel(item, new MacTouchBarLabel(l.Label ?? "", l.AccessibilityLabel ?? "", l.TextColor is { Length: > 0 } tc ? Color(tc) : null));
                break;
            case TouchBarColorPicker p:
                _ui.UpdateColorPicker(item, new MacTouchBarColorPicker(
                    p.AvailableColors is { Count: > 0 } colors ? [.. colors.Select(c => (Color(c), c))] : null,
                    p.SelectedColor is { } selected ? Color(selected) : null));
                break;
            case TouchBarSlider s:
                _ui.UpdateSlider(item, new MacTouchBarSlider(s.Label ?? "", s.MinValue ?? 0, s.MaxValue ?? 100, s.Value ?? 50));
                break;
            case TouchBarPopover o:
                _ui.UpdatePopover(item, new MacTouchBarPopover(o.Label ?? "", o.Icon, o.ShowCloseButton ?? true,
                    _ui.CreateTouchBar(IdentifiersFrom(o.Items.OrderedItems))));
                break;
            case TouchBarGroup g:
                _ui.UpdateGroup(item, _ui.CreateTouchBar(IdentifiersFrom(g.Items.OrderedItems)));
                break;
            case TouchBarSegmentedControl c:
                var segmentIndex = c.SelectedIndex ?? 0;
                _ui.UpdateSegmentedControl(item, new MacTouchBarSegmentedControl(SegmentStyle(c.SegmentStyle), TrackingMode(c.Mode),
                    [.. c.Segments.Select(seg => new MacTouchBarSegment(seg.Label ?? "", seg.Icon, seg.Enabled))],
                    segmentIndex >= 0 && segmentIndex < c.Segments.Count ? segmentIndex : null));
                break;
            case TouchBarScrubber r:
                // AppKit crashes on a scroll with arrows and no items.
                _ui.UpdateScrubber(item, new MacTouchBarScrubber(r.Items is { Count: > 0 } ? r.ShowArrowButtons : null,
                    SelectionStyle(r.SelectedStyle), SelectionStyle(r.OverlayStyle), r.Mode == TouchBarScrubberMode.Fixed ? 0 : 1, r.Continuous));
                break;
        }
    }

    /// <summary>NSSegmentStyle for Electron's names; automatic otherwise.</summary>
    internal static long SegmentStyle(TouchBarSegmentStyle? style) => style switch
    {
        TouchBarSegmentStyle.Rounded => 1,
        TouchBarSegmentStyle.TexturedRounded => 2,
        TouchBarSegmentStyle.RoundRect => 3,
        TouchBarSegmentStyle.TexturedSquare => 4,
        TouchBarSegmentStyle.Capsule => 5,
        TouchBarSegmentStyle.SmallSquare => 6,
        TouchBarSegmentStyle.Separated => 8,
        _ => 0,
    };

    /// <summary>NSSegmentSwitchTracking: select one unless multiple or buttons.</summary>
    internal static long TrackingMode(TouchBarSegmentedControlMode? mode) => mode switch
    {
        TouchBarSegmentedControlMode.Multiple => 1,
        TouchBarSegmentedControlMode.Buttons => 2,
        _ => 0,
    };

    // NSScrubberSelectionStyle's outlineOverlayStyle or roundedBackgroundStyle; null for none.
    private static string? SelectionStyle(TouchBarScrubberStyle? style) => style switch
    {
        TouchBarScrubberStyle.Outline => "outline",
        TouchBarScrubberStyle.Background => "background",
        _ => null,
    };

    /// <summary><c>colorFromHexColorString:</c>: Electron's CSS colour, white when it does not parse.</summary>
    internal static uint Color(string color) => CssColor.TryParseElectron(color, out var argb) ? argb : White;

    /// <summary><c>refreshTouchBarItem:id:</c>: updates the item in <paramref name="touchBar"/> and in the bars of its
    /// group and popover parents.</summary>
    internal void Refresh(nint touchBar, string id)
    {
        if (Known(id) is not { } settings || Identifier(id, settings.Type) is not { } identifier) return;
        foreach (var (parentId, parentType) in settings.Parents)
        {
            if (Identifier(parentId, parentType) is not { } parentIdentifier) continue;
            var parent = _ui.ItemForIdentifier(touchBar, parentIdentifier);
            if (parentType == "popover") Refresh(_ui.PopoverTouchBar(parent), identifier, settings);
            else if (parentType == "group") Refresh(_ui.GroupTouchBar(parent), identifier, settings);
        }
        Refresh(touchBar, identifier, settings);
    }

    private void Refresh(nint touchBar, string identifier, TouchBarItem settings)
    {
        if (_ui.ItemForIdentifier(touchBar, identifier) is var item and not 0) Update(item, settings);
    }

    /// <summary><c>setEscapeTouchBarItem:forTouchBar:</c>.</summary>
    internal void SetEscapeItem(nint touchBar, TouchBarItem? item)
    {
        if (!_ui.CanReplaceEscapeKey(touchBar)) return;
        var identifier = item is null ? null : Identifier(item.Id, item.Type);
        if (identifier is not null) IdentifiersFrom([item!]);
        _ui.SetEscapeKeyReplacement(touchBar, identifier);
    }

    // The target's actions and the scrubber's delegate, reported as Electron's interaction details.
    internal void ButtonAction(long tag) => _interaction(tag.ToString(System.Globalization.CultureInfo.InvariantCulture), new TouchBarInteraction());

    internal void ColorPickerAction(string identifier, uint argb)
        => _interaction(identifier[ColorPickerPrefix.Length..], new TouchBarInteraction { Color = $"#{(argb >> 16) & 0xFF:X2}{(argb >> 8) & 0xFF:X2}{argb & 0xFF:X2}" });

    internal void SliderAction(string identifier, int value) => _interaction(identifier[SliderPrefix.Length..], new TouchBarInteraction { Value = value });

    internal void SegmentedControlAction(long tag, int selectedIndex, bool isSelected)
        => _interaction(tag.ToString(System.Globalization.CultureInfo.InvariantCulture), new TouchBarInteraction { SelectedIndex = selectedIndex, IsSelected = isSelected });

    internal void ScrubberSelected(string scrubberId, int index) => _interaction(scrubberId, new TouchBarInteraction { Type = "select", SelectedIndex = index });

    internal void ScrubberHighlighted(string scrubberId, int index) => _interaction(scrubberId, new TouchBarInteraction { Type = "highlight", HighlightedIndex = index });

    /// <summary><c>numberOfItemsForScrubber:</c>.</summary>
    internal int ScrubberCount(string scrubberId) => Known(scrubberId) is TouchBarScrubber s ? s.Items?.Count ?? 0 : 0;

    /// <summary>The scrubber item for <c>viewForItemAtIndex:</c> and <c>sizeForItemAtIndex:</c>; null for none.</summary>
    internal ScrubberItem? ScrubberItemAt(string scrubberId, int index)
        => Known(scrubberId) is TouchBarScrubber { Items: { } items } && index >= 0 && index < items.Count ? items[index] : null;
}

/// <summary>Electron's <c>NativeWindowMac</c> touch bar methods for one window: a new controller per bar, the window's
/// bar dropped so AppKit asks <c>makeTouchBar</c> again.</summary>
internal sealed class MacTouchBarWindow(Func<MacTouchBarController, IMacTouchBarUi> bind, Action resetTouchBar, Func<nint> currentTouchBar,
    Action<string, TouchBarInteraction> interaction, Action<Action>? withPool = null) : ITouchBarNative
{
    private MacTouchBarController? _controller;

    internal MacTouchBarController? Controller => _controller;

    public void SetItems(IReadOnlyList<TouchBarItem> items)
    {
        var controller = new MacTouchBarController(items, interaction);
        controller.Attach(bind(controller));
        _controller = controller;
        resetTouchBar();
    }

    /// <summary>The window's <c>makeTouchBar</c>: the controller's bar, or none.</summary>
    internal nint MakeTouchBar() => _controller?.MakeTouchBar() ?? 0;

    public void RefreshItem(string id) => InPool(() =>
    {
        if (_controller is not null && currentTouchBar() is var bar and not 0) _controller.Refresh(bar, id);
    });

    public void SetEscapeItem(TouchBarItem? item) => InPool(() =>
    {
        if (_controller is not null && currentTouchBar() is var bar and not 0) _controller.SetEscapeItem(bar, item);
    });

    // Outside AppKit's callbacks the autoreleased bars and items need a pool of their own.
    private void InPool(Action action)
    {
        if (withPool is null) action();
        else withPool(action);
    }
}

/// <summary>The AppKit side of one <see cref="MacTouchBarController"/>: its calls go to <see cref="MacTouchBar"/>, and
/// AppKit's calls on the bar's delegate and target come back to the controller.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacTouchBarUi : IMacTouchBarUi, IMacTouchBarHandler
{
    private static readonly Lazy<(string Small, string Large, string Flexible, string Proxy)> s_system = new(() => (
        MacTouchBar.SystemIdentifier("NSTouchBarItemIdentifierFixedSpaceSmall"), MacTouchBar.SystemIdentifier("NSTouchBarItemIdentifierFixedSpaceLarge"),
        MacTouchBar.SystemIdentifier("NSTouchBarItemIdentifierFlexibleSpace"), MacTouchBar.SystemIdentifier("NSTouchBarItemIdentifierOtherItemsProxy")));

    private readonly MacTouchBarController _controller;
    private readonly nint _target;

    internal MacTouchBarUi(MacTouchBarController controller)
    {
        _controller = controller;
        _target = MacTouchBar.CreateDelegate(this);
    }

    public string FixedSpaceSmall => s_system.Value.Small;
    public string FixedSpaceLarge => s_system.Value.Large;
    public string FlexibleSpace => s_system.Value.Flexible;
    public string OtherItemsProxy => s_system.Value.Proxy;

    public nint CreateTouchBar(IReadOnlyList<string> identifiers) => MacTouchBar.CreateTouchBar(_target, identifiers);
    public nint ItemForIdentifier(nint touchBar, string identifier) => MacTouchBar.ItemForIdentifier(touchBar, identifier);
    public nint PopoverTouchBar(nint popoverItem) => MacTouchBar.PopoverTouchBar(popoverItem);
    public nint GroupTouchBar(nint groupItem) => MacTouchBar.GroupTouchBar(groupItem);
    public bool CanReplaceEscapeKey(nint touchBar) => MacTouchBar.CanReplaceEscapeKey(touchBar);
    public void SetEscapeKeyReplacement(nint touchBar, string? identifier) => MacTouchBar.SetEscapeKeyReplacement(touchBar, identifier);
    public nint MakeButton(string identifier, long tag) => MacTouchBar.MakeButton(_target, identifier, tag);
    public nint MakeLabel(string identifier) => MacTouchBar.MakeLabel(identifier);
    public nint MakeColorPicker(string identifier) => MacTouchBar.MakeColorPicker(_target, identifier);
    public nint MakeSlider(string identifier) => MacTouchBar.MakeSlider(_target, identifier);
    public nint MakePopover(string identifier) => MacTouchBar.MakePopover(identifier);
    public nint MakeGroup(string identifier, IReadOnlyList<nint> items) => MacTouchBar.MakeGroup(identifier, items);
    public nint MakeSegmentedControl(string identifier, long tag) => MacTouchBar.MakeSegmentedControl(_target, identifier, tag);
    public nint MakeScrubber(string identifier, string scrubberId) => MacTouchBar.MakeScrubber(_target, identifier, scrubberId);

    public void UpdateButton(nint item, MacTouchBarButton s)
        => MacTouchBar.UpdateButton(item, s.Title, s.AccessibilityLabel, s.BezelArgb, Image(s.Image), s.ImagePosition, s.Enabled);

    public void UpdateLabel(nint item, MacTouchBarLabel s) => MacTouchBar.UpdateLabel(item, s.Text, s.AccessibilityLabel, s.TextArgb);
    public void UpdateColorPicker(nint item, MacTouchBarColorPicker s) => MacTouchBar.UpdateColorPicker(item, s.Colors, s.SelectedArgb);
    public void UpdateSlider(nint item, MacTouchBarSlider s) => MacTouchBar.UpdateSlider(item, s.Label, s.MinValue, s.MaxValue, s.Value);
    public void UpdatePopover(nint item, MacTouchBarPopover s) => MacTouchBar.UpdatePopover(item, s.Label, Image(s.Image), s.ShowsCloseButton, s.PopoverTouchBar);
    public void UpdateGroup(nint item, nint groupTouchBar) => MacTouchBar.UpdateGroup(item, groupTouchBar);

    public void UpdateSegmentedControl(nint item, MacTouchBarSegmentedControl s)
        => MacTouchBar.UpdateSegmentedControl(item, s.SegmentStyle, s.TrackingMode, [.. s.Segments.Select(seg => (seg.Label, seg.Enabled))],
            [.. s.Segments.Select(seg => Image(seg.Image))], s.SelectedIndex);

    public void UpdateScrubber(nint item, MacTouchBarScrubber s)
        => MacTouchBar.UpdateScrubber(item, s.ShowsArrowButtons, s.SelectionBackgroundStyle, s.SelectionOverlayStyle, s.Mode, s.Continuous);

    nint IMacTouchBarHandler.MakeItem(string identifier) => _controller.MakeItem(identifier);
    void IMacTouchBarHandler.ButtonAction(long tag) => _controller.ButtonAction(tag);
    void IMacTouchBarHandler.ColorPickerAction(string identifier, uint argb) => _controller.ColorPickerAction(identifier, argb);
    void IMacTouchBarHandler.SliderAction(string identifier, int value) => _controller.SliderAction(identifier, value);
    void IMacTouchBarHandler.SegmentedControlAction(long tag, int selectedIndex, bool isSelected) => _controller.SegmentedControlAction(tag, selectedIndex, isSelected);
    void IMacTouchBarHandler.ScrubberSelected(string scrubberId, int index) => _controller.ScrubberSelected(scrubberId, index);
    void IMacTouchBarHandler.ScrubberHighlighted(string scrubberId, int index) => _controller.ScrubberHighlighted(scrubberId, index);
    int IMacTouchBarHandler.ScrubberCount(string scrubberId) => _controller.ScrubberCount(scrubberId);

    bool IMacTouchBarHandler.ScrubberItem(string scrubberId, int index, out string? label, out nint image)
    {
        var item = _controller.ScrubberItemAt(scrubberId, index);
        label = item?.Label;
        image = item is { Label: null, Icon: { } icon } ? Image(icon) : 0;
        return item is not null;
    }

    // gfx::Image::AsNSImage of a NativeImage: every representation, the template flag kept; autoreleased.
    private static nint Image(NativeImage? image)
        => image is null || image.IsEmpty() ? 0
            : MacTouchBar.Autorelease(MacImages.FromRepresentations([.. image.GetScaleFactors().Select(scale => (image.ToPng(scale), scale))], image.IsTemplateImage()));
}
