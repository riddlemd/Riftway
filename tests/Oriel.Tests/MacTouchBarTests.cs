namespace Oriel.Tests;

public sealed class MacTouchBarControllerTests
{
    private readonly FakeUi _ui = new();
    private readonly List<(string Id, TouchBarInteraction Details)> _interactions = [];

    private MacTouchBarController Controller(params TouchBarItem[] items)
    {
        var c = new MacTouchBarController(items, (id, d) => _interactions.Add((id, d)));
        c.Attach(_ui);
        return c;
    }

    [Fact]
    public void IdentifiersNameEachItemWithAppKitsSpacersAndAProxyAtTheEnd()
    {
        var button = new TouchBarButton();
        var label = new TouchBarLabel();
        var c = Controller();
        var ids = c.IdentifiersFrom([button, new TouchBarSpacer { Size = TouchBarSpacerSize.Large }, new TouchBarSpacer(),
            new TouchBarSpacer { Size = TouchBarSpacerSize.Flexible }, label]);
        Assert.Equal([MacTouchBarController.ButtonPrefix + button.Id, "L", "S", "F", MacTouchBarController.LabelPrefix + label.Id, "P"], ids);
        Assert.Equal(["P", MacTouchBarController.LabelPrefix + label.Id], c.IdentifiersFrom([new TouchBarOtherItemsProxy(), label]));
    }

    [Theory]
    [InlineData("button", "com.electron.touchbar.button.7")]
    [InlineData("label", "com.electron.touchbar.label.7")]
    [InlineData("colorpicker", "com.electron.touchbar.colorpicker.7")]
    [InlineData("slider", "com.electron.touchbar.slider.7")]
    [InlineData("popover", "com.electron.touchbar.popover.7")]
    [InlineData("group", "com.electron.touchbar.group.7")]
    [InlineData("segmented_control", "com.electron.touchbar.segmentedcontrol.7")]
    [InlineData("scrubber", "com.electron.touchbar.scrubber.7")]
    [InlineData("spacer", null)]
    public void IdentifierIsTheTypesPrefixAndTheId(string type, string? identifier) => Assert.Equal(identifier, MacTouchBarController.Identifier("7", type));

    [Fact]
    public void TheBarIsTheItemsIdentifiers()
    {
        var label = new TouchBarLabel();
        Controller(label).MakeTouchBar();
        Assert.Equal([[MacTouchBarController.LabelPrefix + label.Id, "P"]], _ui.Bars);
    }

    [Fact]
    public void OnlyNamedItemsAreMade()
    {
        var label = new TouchBarLabel();
        var c = Controller(label);
        Assert.Equal(0, c.MakeItem(MacTouchBarController.LabelPrefix + label.Id));
        c.MakeTouchBar();
        Assert.NotEqual(0, c.MakeItem(MacTouchBarController.LabelPrefix + label.Id));
        Assert.Equal(0, c.MakeItem("com.example.other"));
        Assert.Equal(0, c.MakeItem(MacTouchBarController.ButtonPrefix + label.Id));
    }

    [Fact]
    public void AButtonGetsItsTagTextColourImageAndState()
    {
        var icon = NativeImage.CreateFromBitmap([1, 2, 3, 255], new NativeImageBitmapOptions { Width = 1, Height = 1 });
        var plain = new TouchBarButton();
        var full = new TouchBarButton { Label = "Go", AccessibilityLabel = "Go!", BackgroundColor = "#ff0000", Icon = icon, IconPosition = TouchBarIconPosition.Left, Enabled = false };
        var badColour = new TouchBarButton { BackgroundColor = "nope", IconPosition = TouchBarIconPosition.Right };
        var emptyColour = new TouchBarButton { BackgroundColor = "" };
        var c = Controller(plain, full, badColour, emptyColour);
        c.MakeTouchBar();
        c.MakeItem(MacTouchBarController.ButtonPrefix + plain.Id);
        c.MakeItem(MacTouchBarController.ButtonPrefix + full.Id);
        c.MakeItem(MacTouchBarController.ButtonPrefix + badColour.Id);
        c.MakeItem(MacTouchBarController.ButtonPrefix + emptyColour.Id);
        Assert.Equal([$"button {plain.Id} tag {plain.Id}", $"button {full.Id} tag {full.Id}", $"button {badColour.Id} tag {badColour.Id}", $"button {emptyColour.Id} tag {emptyColour.Id}"], _ui.Made);
        Assert.Null(_ui.Buttons[3].BezelArgb);
        Assert.Equal(new MacTouchBarButton("", "", null, null, 6, true), _ui.Buttons[0]);
        Assert.Equal(new MacTouchBarButton("Go", "Go!", 0xFFFF0000, icon, 2, false), _ui.Buttons[1]);
        Assert.Equal(new MacTouchBarButton("", "", 0xFFFFFFFF, null, 3, true), _ui.Buttons[2]);
    }

    [Fact]
    public void LabelsPickersAndSlidersGetElectronsDefaults()
    {
        var label = new TouchBarLabel { Label = "L", TextColor = "#00ff00" };
        var emptyColour = new TouchBarLabel { TextColor = "" };
        var picker = new TouchBarColorPicker { AvailableColors = ["#000000", "#ffffff"], SelectedColor = "#ffffff" };
        var bare = new TouchBarColorPicker { AvailableColors = [] };
        var slider = new TouchBarSlider();
        var c = Controller(label, emptyColour, picker, bare, slider);
        c.MakeTouchBar();
        foreach (var (prefix, item) in new (string, TouchBarItem)[] { (MacTouchBarController.LabelPrefix, label), (MacTouchBarController.LabelPrefix, emptyColour),
            (MacTouchBarController.ColorPickerPrefix, picker), (MacTouchBarController.ColorPickerPrefix, bare), (MacTouchBarController.SliderPrefix, slider) })
            c.MakeItem(prefix + item.Id);
        Assert.Equal([new MacTouchBarLabel("L", "", 0xFF00FF00), new MacTouchBarLabel("", "", null)], _ui.Labels);
        Assert.Equal([(0xFF000000u, "#000000"), (0xFFFFFFFFu, "#ffffff")], _ui.Pickers[0].Colors);
        Assert.Equal(0xFFFFFFFFu, _ui.Pickers[0].SelectedArgb);
        Assert.Equal(new MacTouchBarColorPicker(null, null), _ui.Pickers[1]);
        Assert.Equal(new MacTouchBarSlider("", 0, 100, 50), _ui.Sliders[0]);
    }

    [Fact]
    public void APopoverHasItsOwnBarAndShowsACloseButtonUnlessTold()
    {
        var inner = new TouchBarButton();
        var popover = new TouchBarPopover { Label = "More", Items = new TouchBar(inner) };
        var noClose = new TouchBarPopover { ShowCloseButton = false };
        var c = Controller(popover, noClose);
        c.MakeTouchBar();
        c.MakeItem(MacTouchBarController.PopoverPrefix + popover.Id);
        c.MakeItem(MacTouchBarController.PopoverPrefix + noClose.Id);
        Assert.Equal([MacTouchBarController.ButtonPrefix + inner.Id, "P"], _ui.Bars[1]);
        Assert.Equal(("More", true, _ui.BarHandles[1]), (_ui.Popovers[0].Label, _ui.Popovers[0].ShowsCloseButton, _ui.Popovers[0].PopoverTouchBar));
        Assert.False(_ui.Popovers[1].ShowsCloseButton);
        Assert.NotEqual(0, c.MakeItem(MacTouchBarController.ButtonPrefix + inner.Id));
    }

    [Fact]
    public void AGroupMakesItsItemsWithoutTheProxy()
    {
        var a = new TouchBarLabel();
        var b = new TouchBarButton();
        var group = new TouchBarGroup { Items = new TouchBar(a, new TouchBarOtherItemsProxy(), b) };
        var c = Controller(group);
        c.MakeTouchBar();
        var item = c.MakeItem(MacTouchBarController.GroupPrefix + group.Id);
        Assert.Equal($"group {group.Id} [{_ui.Handle($"label {a.Id}")},{_ui.Handle($"button {b.Id} tag {b.Id}")}]", _ui.Made[^1]);
        Assert.NotEqual(0, item);
    }

    [Fact]
    public void SegmentsAndScrubbersGetAppKitsValues()
    {
        var segments = new TouchBarSegmentedControl
        {
            SegmentStyle = TouchBarSegmentStyle.Separated, Mode = TouchBarSegmentedControlMode.Buttons, SelectedIndex = 1,
            Segments = [new SegmentedControlSegment { Label = "a" }, new SegmentedControlSegment { Enabled = false }],
        };
        var outOfRange = new TouchBarSegmentedControl { Segments = [new SegmentedControlSegment()], SelectedIndex = 1 };
        var none = new TouchBarSegmentedControl();
        var unset = new TouchBarSegmentedControl { Segments = [new SegmentedControlSegment(), new SegmentedControlSegment()] };
        var scrubber = new TouchBarScrubber { Items = [new ScrubberItem { Label = "x" }], ShowArrowButtons = true, SelectedStyle = TouchBarScrubberStyle.Outline,
            OverlayStyle = TouchBarScrubberStyle.Background, Mode = TouchBarScrubberMode.Fixed, Continuous = false };
        var empty = new TouchBarScrubber { ShowArrowButtons = true };
        var c = Controller(segments, outOfRange, none, scrubber, empty, unset);
        c.MakeTouchBar();
        c.MakeItem(MacTouchBarController.SegmentedControlPrefix + segments.Id);
        c.MakeItem(MacTouchBarController.SegmentedControlPrefix + outOfRange.Id);
        c.MakeItem(MacTouchBarController.SegmentedControlPrefix + none.Id);
        c.MakeItem(MacTouchBarController.ScrubberPrefix + scrubber.Id);
        c.MakeItem(MacTouchBarController.ScrubberPrefix + empty.Id);
        c.MakeItem(MacTouchBarController.SegmentedControlPrefix + unset.Id);
        Assert.Equal(0, _ui.Segments[3].SelectedIndex);
        var s = _ui.Segments[0];
        Assert.Equal((8L, 2L, 1), (s.SegmentStyle, s.TrackingMode, s.SelectedIndex));
        Assert.Equal([new MacTouchBarSegment("a", null, true), new MacTouchBarSegment("", null, false)], s.Segments);
        Assert.Null(_ui.Segments[1].SelectedIndex);
        Assert.Null(_ui.Segments[2].SelectedIndex);
        Assert.Equal((0L, 0L), (_ui.Segments[2].SegmentStyle, _ui.Segments[2].TrackingMode));
        Assert.Contains($"segments {segments.Id} tag {segments.Id}", _ui.Made);
        Assert.Contains($"scrubber {scrubber.Id}", _ui.Made);
        Assert.Equal(new MacTouchBarScrubber(true, "outline", "background", 0, false), _ui.Scrubbers[0]);
        Assert.Equal(new MacTouchBarScrubber(null, null, null, 1, true), _ui.Scrubbers[1]);
    }

    [Theory]
    [InlineData(TouchBarSegmentStyle.Automatic, 0)]
    [InlineData(TouchBarSegmentStyle.Rounded, 1)]
    [InlineData(TouchBarSegmentStyle.TexturedRounded, 2)]
    [InlineData(TouchBarSegmentStyle.RoundRect, 3)]
    [InlineData(TouchBarSegmentStyle.TexturedSquare, 4)]
    [InlineData(TouchBarSegmentStyle.Capsule, 5)]
    [InlineData(TouchBarSegmentStyle.SmallSquare, 6)]
    [InlineData(TouchBarSegmentStyle.Separated, 8)]
    public void SegmentStylesAreNSSegmentStyles(TouchBarSegmentStyle style, long value) => Assert.Equal(value, MacTouchBarController.SegmentStyle(style));

    [Fact]
    public void TrackingModesAreNSSegmentSwitchTracking()
        => Assert.Equal([0L, 0L, 1L, 2L], new TouchBarSegmentedControlMode?[] { null, TouchBarSegmentedControlMode.Single, TouchBarSegmentedControlMode.Multiple, TouchBarSegmentedControlMode.Buttons }.Select(MacTouchBarController.TrackingMode));

    [Fact]
    public void ARefreshUpdatesTheItemAndItsCopiesInParentBars()
    {
        var inner = new TouchBarLabel { Label = "a" };
        var popover = new TouchBarPopover { Items = new TouchBar(inner) };
        var c = Controller(popover);
        c.MakeTouchBar();
        const nint main = 500, popoverItem = 501, popoverBar = 502, innerInPopover = 503, innerInMain = 504;
        _ui.Items[(main, MacTouchBarController.PopoverPrefix + popover.Id)] = popoverItem;
        _ui.PopoverBars[popoverItem] = popoverBar;
        c.Refresh(main, inner.Id);
        Assert.Empty(_ui.Labels);
        c.MakeItem(MacTouchBarController.PopoverPrefix + popover.Id);
        _ui.Labels.Clear();
        _ui.Items[(popoverBar, MacTouchBarController.LabelPrefix + inner.Id)] = innerInPopover;
        _ui.Items[(main, MacTouchBarController.LabelPrefix + inner.Id)] = innerInMain;
        inner.Label = "b";
        c.Refresh(main, inner.Id);
        Assert.Equal([innerInPopover, innerInMain], _ui.LabelTargets);
        Assert.All(_ui.Labels, l => Assert.Equal("b", l.Text));
        c.Refresh(main, "999999");
    }

    [Fact]
    public void AGroupParentRefreshesInTheGroupsBar()
    {
        var inner = new TouchBarLabel();
        var group = new TouchBarGroup { Items = new TouchBar(inner) };
        var c = Controller(group);
        c.MakeTouchBar();
        c.MakeItem(MacTouchBarController.GroupPrefix + group.Id);
        _ui.Items[(600, MacTouchBarController.GroupPrefix + group.Id)] = 601;
        _ui.GroupBars[601] = 602;
        _ui.Items[(602, MacTouchBarController.LabelPrefix + inner.Id)] = 603;
        _ui.LabelTargets.Clear();
        c.Refresh(600, inner.Id);
        Assert.Equal([(nint)603], _ui.LabelTargets);
    }

    [Fact]
    public void TheEscapeItemReplacesTheKeyWhereAppKitCan()
    {
        var escape = new TouchBarButton();
        var c = Controller();
        _ui.CanReplace = false;
        c.SetEscapeItem(1, escape);
        Assert.Empty(_ui.Escapes);
        _ui.CanReplace = true;
        c.SetEscapeItem(1, escape);
        c.SetEscapeItem(1, new TouchBarSpacer());
        c.SetEscapeItem(1, null);
        Assert.Equal([MacTouchBarController.ButtonPrefix + escape.Id, null, null], _ui.Escapes);
        Assert.NotEqual(0, c.MakeItem(MacTouchBarController.ButtonPrefix + escape.Id));
    }

    [Fact]
    public void ActionsReportElectronsDetails()
    {
        var c = Controller();
        c.ButtonAction(5);
        c.ColorPickerAction(MacTouchBarController.ColorPickerPrefix + "7", 0xFF12AB0C);
        c.SliderAction(MacTouchBarController.SliderPrefix + "8", 42);
        c.SegmentedControlAction(9, 2, true);
        c.ScrubberSelected("10", 3);
        c.ScrubberHighlighted("10", 4);
        Assert.Equal([
            ("5", new TouchBarInteraction()),
            ("7", new TouchBarInteraction { Color = "#12AB0C" }),
            ("8", new TouchBarInteraction { Value = 42 }),
            ("9", new TouchBarInteraction { SelectedIndex = 2, IsSelected = true }),
            ("10", new TouchBarInteraction { Type = "select", SelectedIndex = 3 }),
            ("10", new TouchBarInteraction { Type = "highlight", HighlightedIndex = 4 }),
        ], _interactions);
    }

    [Fact]
    public void TheScrubbersItemsAreItsLiveItems()
    {
        var scrubber = new TouchBarScrubber { Items = [new ScrubberItem { Label = "a" }, new ScrubberItem { Label = "b" }] };
        var c = Controller(scrubber);
        Assert.Equal(0, c.ScrubberCount(scrubber.Id));
        c.MakeTouchBar();
        Assert.Equal(2, c.ScrubberCount(scrubber.Id));
        Assert.Equal("b", c.ScrubberItemAt(scrubber.Id, 1)?.Label);
        Assert.Null(c.ScrubberItemAt(scrubber.Id, 2));
        Assert.Null(c.ScrubberItemAt(scrubber.Id, -1));
        scrubber.Items = null;
        Assert.Equal(0, c.ScrubberCount(scrubber.Id));
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData("3.9", 3)]
    [InlineData("x", 0)]
    public void TagIsTheIdsFloatValue(string id, long tag) => Assert.Equal(tag, MacTouchBarController.Tag(id));

    [Fact]
    public void TheWindowMakesANewControllerPerBarAndAsksAppKitAgain()
    {
        var resets = 0;
        nint current = 0;
        var window = new MacTouchBarWindow(_ => _ui, () => resets++, () => current, (id, d) => _interactions.Add((id, d)));
        Assert.Equal(0, window.MakeTouchBar());
        window.RefreshItem("1");
        window.SetEscapeItem(new TouchBarButton());
        var label = new TouchBarLabel();
        window.SetItems([label]);
        var first = window.Controller;
        Assert.Equal(1, resets);
        Assert.NotEqual(0, window.MakeTouchBar());
        window.SetEscapeItem(new TouchBarButton());
        Assert.Empty(_ui.Escapes);
        current = 700;
        _ui.Items[(700, MacTouchBarController.LabelPrefix + label.Id)] = 701;
        window.RefreshItem(label.Id);
        Assert.Equal([(nint)701], _ui.LabelTargets);
        window.SetEscapeItem(null);
        Assert.Equal([null], _ui.Escapes);
        window.SetItems([]);
        Assert.NotSame(first, window.Controller);
        Assert.Equal(2, resets);
    }

    private sealed class FakeUi : IMacTouchBarUi
    {
        private nint _next = 1000;
        private readonly Dictionary<string, nint> _handles = [];
        public List<List<string>> Bars = [];
        public List<nint> BarHandles = [];
        public List<string> Made = [];
        public Dictionary<(nint, string), nint> Items = [];
        public Dictionary<nint, nint> PopoverBars = [], GroupBars = [];
        public bool CanReplace = true;
        public List<string?> Escapes = [];
        public List<MacTouchBarButton> Buttons = [];
        public List<MacTouchBarLabel> Labels = [];
        public List<nint> LabelTargets = [];
        public List<MacTouchBarColorPicker> Pickers = [];
        public List<MacTouchBarSlider> Sliders = [];
        public List<MacTouchBarPopover> Popovers = [];
        public List<MacTouchBarSegmentedControl> Segments = [];
        public List<MacTouchBarScrubber> Scrubbers = [];

        public string FixedSpaceSmall => "S";
        public string FixedSpaceLarge => "L";
        public string FlexibleSpace => "F";
        public string OtherItemsProxy => "P";
        public nint Handle(string made) => _handles[made];

        private nint New(string made)
        {
            Made.Add(made);
            return _handles[made] = _next++;
        }

        public nint CreateTouchBar(IReadOnlyList<string> identifiers)
        {
            Bars.Add([.. identifiers]);
            BarHandles.Add(_next);
            return _next++;
        }

        public nint ItemForIdentifier(nint touchBar, string identifier) => Items.GetValueOrDefault((touchBar, identifier));
        public nint PopoverTouchBar(nint popoverItem) => PopoverBars.GetValueOrDefault(popoverItem);
        public nint GroupTouchBar(nint groupItem) => GroupBars.GetValueOrDefault(groupItem);
        public bool CanReplaceEscapeKey(nint touchBar) => CanReplace;
        public void SetEscapeKeyReplacement(nint touchBar, string? identifier) => Escapes.Add(identifier);
        public nint MakeButton(string identifier, long tag) => New($"button {identifier[MacTouchBarController.ButtonPrefix.Length..]} tag {tag}");
        public nint MakeLabel(string identifier) => New($"label {identifier[MacTouchBarController.LabelPrefix.Length..]}");
        public nint MakeColorPicker(string identifier) => New($"picker {identifier}");
        public nint MakeSlider(string identifier) => New($"slider {identifier}");
        public nint MakePopover(string identifier) => New($"popover {identifier}");
        public nint MakeGroup(string identifier, IReadOnlyList<nint> items) => New($"group {identifier[MacTouchBarController.GroupPrefix.Length..]} [{string.Join(",", items)}]");
        public nint MakeSegmentedControl(string identifier, long tag) => New($"segments {identifier[MacTouchBarController.SegmentedControlPrefix.Length..]} tag {tag}");
        public nint MakeScrubber(string identifier, string scrubberId) => New($"scrubber {scrubberId}");
        public void UpdateButton(nint item, MacTouchBarButton state) => Buttons.Add(state);
        public void UpdateLabel(nint item, MacTouchBarLabel state)
        {
            LabelTargets.Add(item);
            Labels.Add(state);
        }
        public void UpdateColorPicker(nint item, MacTouchBarColorPicker state) => Pickers.Add(state);
        public void UpdateSlider(nint item, MacTouchBarSlider state) => Sliders.Add(state);
        public void UpdatePopover(nint item, MacTouchBarPopover state) => Popovers.Add(state);
        public void UpdateGroup(nint item, nint groupTouchBar) => GroupBars[item] = groupTouchBar;
        public void UpdateSegmentedControl(nint item, MacTouchBarSegmentedControl state) => Segments.Add(state);
        public void UpdateScrubber(nint item, MacTouchBarScrubber state) => Scrubbers.Add(state);
    }
}

public sealed class MacTouchBarNativeTests
{
    [Fact]
    public void AppKitsIdentifiersAreDistinctStrings()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var ids = new[] { "NSTouchBarItemIdentifierFixedSpaceSmall", "NSTouchBarItemIdentifierFixedSpaceLarge", "NSTouchBarItemIdentifierFlexibleSpace",
            "NSTouchBarItemIdentifierOtherItemsProxy" }.Select(Oriel.Cef.Platform.MacTouchBar.SystemIdentifier).ToList();
        Assert.All(ids, id => Assert.StartsWith("NSTouchBarItemIdentifier", id));
        Assert.Equal(4, ids.Distinct().Count());
    }

    [Theory]
    [InlineData(0xFF12AB0Cu)]
    [InlineData(0x80FF8000u)]
    [InlineData(0x00000000u)]
    public void ADeviceColourRoundTrips(uint argb)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(argb, Oriel.Cef.Platform.MacTouchBar.ArgbOf(Oriel.Cef.Platform.MacTouchBar.DeviceColor(argb)));
    }

    [Fact]
    public void HalvesRoundUpAsSkia()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(0xFF7F7F7Fu, Oriel.Cef.Platform.MacTouchBar.ArgbOf(Oriel.Cef.Platform.MacTouchBar.DeviceColor(126.5 / 255, 126.5 / 255, 126.5 / 255, 1)));
    }
}
