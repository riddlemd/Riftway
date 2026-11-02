namespace Riftway.Tests;

public sealed class TouchBarModelTests
{
    [Fact]
    public void ItemsGetSequentialIdentifiers()
    {
        var a = new TouchBarLabel();
        var b = new TouchBarButton();
        // The counter is process-wide and other test classes make items in parallel, so only the order is fixed here.
        Assert.True(int.Parse(b.Id) > int.Parse(a.Id));
    }

    [Fact]
    public void DefaultsAreElectrons()
    {
        Assert.True(new TouchBarButton().Enabled);
        var scrubber = new TouchBarScrubber();
        Assert.Equal((TouchBarScrubberMode.Free, true, false, null, null), (scrubber.Mode, scrubber.Continuous, scrubber.ShowArrowButtons, scrubber.SelectedStyle, scrubber.OverlayStyle));
        Assert.Empty(new TouchBarSegmentedControl().Segments);
        Assert.Empty(new TouchBarSegmentedControl { Segments = null! }.Segments);
        Assert.Empty(new TouchBarGroup().Items.OrderedItems);
    }

    [Fact]
    public void EveryLivePropertySetIsAChangeEvenToTheSameValue()
    {
        var label = new TouchBarLabel { Label = "a" };
        var bar = new TouchBar(label);
        var changes = new List<string>();
        bar.Changed += (id, type) => changes.Add($"{id}:{type}");
        label.Label = "a";
        label.TextColor = "#fff";
        Assert.Equal([$"{label.Id}:label", $"{label.Id}:label"], changes);
    }

    [Fact]
    public void NestedItemsAreFoundAndReportTheirChanges()
    {
        var inner = new TouchBarButton();
        var group = new TouchBarGroup { Items = new TouchBar(inner) };
        var bar = new TouchBar(group);
        Assert.Same(inner, bar.Find(inner.Id));
        var changes = new List<string>();
        bar.Changed += (id, type) => changes.Add(type);
        inner.Label = "x";
        Assert.Equal(["button"], changes);
    }

    [Fact]
    public void OnlyOneOtherItemsProxyAndNoItemTwice()
    {
        Assert.Equal("Must only have one OtherItemsProxy per TouchBar",
            Assert.Throws<InvalidOperationException>(() => new TouchBar(new TouchBarOtherItemsProxy(), new TouchBarOtherItemsProxy())).Message);
        var item = new TouchBarLabel();
        Assert.Equal("Cannot add a single instance of TouchBarItem multiple times in a TouchBar",
            Assert.Throws<InvalidOperationException>(() => new TouchBar(item, item)).Message);
    }

    [Fact]
    public void ARefusedBarRegistersNothing()
    {
        var label = new TouchBarLabel();
        Assert.Throws<InvalidOperationException>(() => new TouchBar(label, new TouchBarOtherItemsProxy(), new TouchBarOtherItemsProxy()));
        var bar = new TouchBar(label);
        var changes = 0;
        bar.Changed += (_, _) => changes++;
        label.Label = "x";
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AGroupsItemsKnowTheirParentAcrossReplacement()
    {
        var a = new TouchBarLabel();
        var b = new TouchBarLabel();
        var group = new TouchBarGroup { Items = new TouchBar(a) };
        Assert.Equal([(group.Id, "group")], a.Parents);
        group.Items = new TouchBar(b);
        Assert.Empty(a.Parents);
        Assert.Equal([(group.Id, "group")], b.Parents);
        var popover = new TouchBarPopover { Items = new TouchBar(b) };
        Assert.Equal([(group.Id, "group"), (popover.Id, "popover")], b.Parents);
        popover.Items = new TouchBar(b);
        Assert.Equal(2, b.Parents.Count);
        b.AddParent(group);
        Assert.Equal(2, b.Parents.Count);
    }

    [Fact]
    public void TheEscapeItemReportsItselfAndItsChanges()
    {
        var bar = new TouchBar();
        var first = new TouchBarButton();
        var second = new TouchBarButton();
        var escapes = new List<TouchBarItem?>();
        var changes = new List<string>();
        bar.EscapeItemChanged += escapes.Add;
        bar.Changed += (id, _) => changes.Add(id);
        bar.EscapeItem = first;
        first.Label = "1";
        bar.EscapeItem = second;
        first.Label = "2";
        second.Label = "3";
        bar.EscapeItem = null;
        Assert.Equal([first, second, null], escapes);
        Assert.Equal([first.Id, second.Id], changes);
    }
}

public sealed class TouchBarWindowTests
{
    private readonly FakeHost _window = new();

    [Fact]
    public void AddingShowsTheItemsAndEscapeItem()
    {
        var escape = new TouchBarButton();
        var label = new TouchBarLabel();
        var bar = new TouchBar(label) { EscapeItem = escape };
        TouchBar.SetOnWindow(bar, _window);
        Assert.Same(bar, _window.AttachedTouchBar);
        Assert.Equal([$"items:{label.Id}", $"escape:{escape.Id}"], _window.Log);
        TouchBar.SetOnWindow(bar, _window);
        Assert.Equal(4, _window.Log.Count);
    }

    [Fact]
    public void ChangesRefreshTheirItemOnTheWindow()
    {
        var label = new TouchBarLabel();
        var bar = new TouchBar(label);
        TouchBar.SetOnWindow(bar, _window);
        _window.Log.Clear();
        label.Label = "x";
        var other = new TouchBarButton();
        bar.EscapeItem = other;
        other.Label = "y";
        Assert.Equal([$"refresh:{label.Id}", $"escape:{other.Id}", $"refresh:{other.Id}"], _window.Log);
    }

    [Fact]
    public void InteractionsReachTheirItemIncludingTheEscapeItem()
    {
        var clicks = 0;
        var button = new TouchBarButton { Click = () => clicks++ };
        var escape = new TouchBarButton { Click = () => clicks += 10 };
        var bar = new TouchBar(button) { EscapeItem = escape };
        TouchBar.SetOnWindow(bar, _window);
        _window.Interact(button.Id, new TouchBarInteraction());
        _window.Interact(escape.Id, new TouchBarInteraction());
        _window.Interact("nope", new TouchBarInteraction());
        Assert.Equal(11, clicks);
    }

    [Fact]
    public void APickUpdatesTheColorQuietlyOnlyWithACallback()
    {
        string? picked = null;
        var withCallback = new TouchBarColorPicker { SelectedColor = "#000", Change = c => picked = c };
        var without = new TouchBarColorPicker { SelectedColor = "#000" };
        var bar = new TouchBar(withCallback, without);
        TouchBar.SetOnWindow(bar, _window);
        _window.Log.Clear();
        _window.Interact(withCallback.Id, new TouchBarInteraction { Color = "#fff" });
        _window.Interact(without.Id, new TouchBarInteraction { Color = "#fff" });
        Assert.Equal("#fff", picked);
        Assert.Equal("#fff", withCallback.SelectedColor);
        Assert.Equal("#000", without.SelectedColor);
        Assert.Empty(_window.Log);
    }

    [Fact]
    public void SlidersSegmentsAndScrubbersReportTheirDetails()
    {
        var events = new List<string>();
        var slider = new TouchBarSlider { Value = 1, Change = v => events.Add($"slider:{v}") };
        var segments = new TouchBarSegmentedControl { SelectedIndex = 0, Change = (i, on) => events.Add($"segment:{i}:{on}") };
        var scrubber = new TouchBarScrubber { Select = i => events.Add($"select:{i}"), Highlight = i => events.Add($"highlight:{i}") };
        var selectOnly = new TouchBarScrubber { Select = i => events.Add($"only:{i}") };
        var highlightOnly = new TouchBarScrubber { Highlight = i => events.Add($"hover:{i}") };
        TouchBar.SetOnWindow(new TouchBar(slider, segments, scrubber, selectOnly, highlightOnly), _window);
        _window.Interact(slider.Id, new TouchBarInteraction { Value = 7 });
        _window.Interact(segments.Id, new TouchBarInteraction { SelectedIndex = 2, IsSelected = true });
        _window.Interact(scrubber.Id, new TouchBarInteraction { Type = "select", SelectedIndex = 3 });
        _window.Interact(scrubber.Id, new TouchBarInteraction { Type = "highlight", HighlightedIndex = 4 });
        _window.Interact(selectOnly.Id, new TouchBarInteraction { Type = "highlight", HighlightedIndex = 5 });
        _window.Interact(highlightOnly.Id, new TouchBarInteraction { Type = "highlight", HighlightedIndex = 6 });
        Assert.Equal(["slider:7", "segment:2:True", "select:3", "highlight:4", "hover:6"], events);
        Assert.Equal((7, 2), (slider.Value, segments.SelectedIndex));
    }

    [Fact]
    public void ReplacingABarDetachesTheOldOne()
    {
        var oldLabel = new TouchBarLabel();
        var oldBar = new TouchBar(oldLabel);
        TouchBar.SetOnWindow(oldBar, _window);
        var newBar = new TouchBar(new TouchBarLabel());
        TouchBar.SetOnWindow(newBar, _window);
        _window.Log.Clear();
        oldLabel.Label = "x";
        var escape = new TouchBarButton();
        oldBar.EscapeItem = escape;
        escape.Label = "y";
        Assert.Empty(_window.Log);
        Assert.Same(newBar, _window.AttachedTouchBar);
    }

    [Fact]
    public void NullClearsTheWindowsBar()
    {
        var label = new TouchBarLabel();
        TouchBar.SetOnWindow(new TouchBar(label), _window);
        TouchBar.SetOnWindow(null, _window);
        Assert.Null(_window.AttachedTouchBar);
        Assert.Equal("items:", _window.Log[^1]);
        _window.Log.Clear();
        label.Label = "x";
        Assert.Empty(_window.Log);
    }

    [Fact]
    public void ClosingTheWindowDetachesAndAReaddedBarNoLongerFollowsItsItems()
    {
        var label = new TouchBarLabel();
        var bar = new TouchBar(label);
        TouchBar.SetOnWindow(bar, _window);
        _window.Close();
        Assert.Null(_window.AttachedTouchBar);
        Assert.Equal(0, _window.InteractionHandlers);
        var other = new FakeHost { Id = 2 };
        TouchBar.SetOnWindow(bar, other);
        other.Log.Clear();
        label.Label = "x";
        Assert.Empty(other.Log);
    }

    private sealed class FakeHost : ITouchBarHost
    {
        private Action<string, TouchBarInteraction>? _interaction;
        private Action? _closed;
        public List<string> Log = [];
        public int Id { get; init; } = 1;
        public TouchBar? AttachedTouchBar { get; set; }
        public int InteractionHandlers => _interaction?.GetInvocationList().Length ?? 0;

        public void SetTouchBarItems(IReadOnlyList<TouchBarItem> items) => Log.Add("items:" + string.Join(",", items.Select(i => i.Id)));
        public void RefreshTouchBarItem(string id) => Log.Add($"refresh:{id}");
        public void SetEscapeTouchBarItem(TouchBarItem? item) => Log.Add($"escape:{item?.Id}");
        public event Action<string, TouchBarInteraction>? TouchBarInteraction { add => _interaction += value; remove => _interaction -= value; }
        public event Action? HostClosed { add => _closed += value; remove => _closed -= value; }
        public void Interact(string id, TouchBarInteraction details) => _interaction?.Invoke(id, details);
        public void Close() => _closed?.Invoke();
    }
}
