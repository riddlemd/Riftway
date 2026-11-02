namespace Riftway;

/// <summary>Where a button's icon sits. Electron's <c>iconPosition</c>.</summary>
public enum TouchBarIconPosition
{
    /// <summary>Electron's <c>left</c>.</summary>
    Left,

    /// <summary>Electron's <c>right</c>.</summary>
    Right,

    /// <summary>Electron's <c>overlay</c>.</summary>
    Overlay,
}

/// <summary>A spacer's width. Electron's <c>size</c>.</summary>
public enum TouchBarSpacerSize
{
    /// <summary>Electron's <c>small</c>.</summary>
    Small,

    /// <summary>Electron's <c>large</c>.</summary>
    Large,

    /// <summary>Electron's <c>flexible</c>.</summary>
    Flexible,
}

/// <summary>A segmented control's look. Electron's <c>segmentStyle</c>.</summary>
public enum TouchBarSegmentStyle
{
    /// <summary>Electron's <c>automatic</c>.</summary>
    Automatic,

    /// <summary>Electron's <c>rounded</c>.</summary>
    Rounded,

    /// <summary>Electron's <c>textured-rounded</c>.</summary>
    TexturedRounded,

    /// <summary>Electron's <c>round-rect</c>.</summary>
    RoundRect,

    /// <summary>Electron's <c>textured-square</c>.</summary>
    TexturedSquare,

    /// <summary>Electron's <c>capsule</c>.</summary>
    Capsule,

    /// <summary>Electron's <c>small-square</c>.</summary>
    SmallSquare,

    /// <summary>Electron's <c>separated</c>.</summary>
    Separated,
}

/// <summary>How a segmented control selects. Electron's <c>mode</c>.</summary>
public enum TouchBarSegmentedControlMode
{
    /// <summary>One segment at a time. Electron's <c>single</c>.</summary>
    Single,

    /// <summary>Any number. Electron's <c>multiple</c>.</summary>
    Multiple,

    /// <summary>Momentary buttons. Electron's <c>buttons</c>.</summary>
    Buttons,
}

/// <summary>How a scrubber marks an item. Electron's <c>selectedStyle</c> and <c>overlayStyle</c>.</summary>
public enum TouchBarScrubberStyle
{
    /// <summary>Electron's <c>background</c>.</summary>
    Background,

    /// <summary>Electron's <c>outline</c>.</summary>
    Outline,

    /// <summary>Electron's <c>none</c>.</summary>
    None,
}

/// <summary>How a scrubber moves. Electron's <c>mode</c>.</summary>
public enum TouchBarScrubberMode
{
    /// <summary>Electron's <c>fixed</c>.</summary>
    Fixed,

    /// <summary>Electron's <c>free</c>.</summary>
    Free,
}

/// <summary>A segment of a <see cref="TouchBarSegmentedControl"/>. Electron's <c>SegmentedControlSegment</c>.</summary>
public sealed record SegmentedControlSegment
{
    /// <summary>The text.</summary>
    public string? Label { get; init; }

    /// <summary>The image, used when there is no label.</summary>
    public NativeImage? Icon { get; init; }

    /// <summary>Whether it can be selected; true by default.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>An item of a <see cref="TouchBarScrubber"/>. Electron's <c>ScrubberItem</c>.</summary>
public sealed record ScrubberItem
{
    /// <summary>The text.</summary>
    public string? Label { get; init; }

    /// <summary>The image, used when there is no label.</summary>
    public NativeImage? Icon { get; init; }
}

/// <summary>What the native Touch Bar reports for an item: Electron's interaction <c>details</c>.</summary>
internal sealed record TouchBarInteraction
{
    public string? Color { get; init; }
    public int? Value { get; init; }
    public int? SelectedIndex { get; init; }
    public bool? IsSelected { get; init; }
    public int? HighlightedIndex { get; init; }
    public string? Type { get; init; }
}

/// <summary>An item of a <see cref="TouchBar"/>. Electron's <c>TouchBarItem</c>.</summary>
/// <remarks>Changing a property updates the Touch Bar it shows in; the callbacks are fixed once the item is made.</remarks>
public abstract class TouchBarItem
{
    private static int s_nextId;
    private readonly List<(string Id, string Type)> _parents = [];

    private protected TouchBarItem() => Id = Interlocked.Increment(ref s_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The item's identifier, unique in the process. Electron's <c>id</c>.</summary>
    public string Id { get; }

    /// <summary>Electron's item type name (<c>button</c>, <c>colorpicker</c>, ...).</summary>
    internal abstract string Type { get; }

    /// <summary>The group or popover items this one is in, as <c>_parents</c>.</summary>
    internal IReadOnlyList<(string Id, string Type)> Parents => _parents;

    /// <summary>The group's or popover's bar; null for other items. Electron's <c>child</c>.</summary>
    internal virtual TouchBar? Child => null;

    /// <summary>A live property changed. Electron's <c>change</c>.</summary>
    internal event Action<TouchBarItem>? Changed;

    /// <summary>Electron's <c>onInteraction</c>: false when the item has none, as for an item made without a callback.</summary>
    internal virtual bool Interact(TouchBarInteraction details) => false;

    // LiveProperty's setter: stores, then emits change even for the same value.
    private protected void Set<T>(ref T field, T value)
    {
        field = value;
        Changed?.Invoke(this);
    }

    internal void AddParent(TouchBarItem item)
    {
        if (!_parents.Exists(p => p.Id == item.Id)) _parents.Add((item.Id, item.Type));
    }

    internal void RemoveParent(TouchBarItem item) => _parents.RemoveAll(p => p.Id == item.Id);

    // The LiveProperty onMutate of group and popover: the old bar's items lose this parent, the new bar's gain it.
    private protected void Reparent(TouchBar? oldChild, TouchBar newChild)
    {
        if (oldChild is not null)
            foreach (var item in oldChild.OrderedItems) item.RemoveParent(this);
        foreach (var item in newChild.OrderedItems) item.AddParent(this);
    }
}

/// <summary>A button. Electron's <c>TouchBarButton</c>.</summary>
public sealed class TouchBarButton : TouchBarItem
{
    private string? _label, _accessibilityLabel, _backgroundColor;
    private NativeImage? _icon;
    private TouchBarIconPosition? _iconPosition;
    private bool _enabled = true;

    internal override string Type => "button";

    /// <summary>The text.</summary>
    public string? Label { get => _label; set => Set(ref _label, value); }

    /// <summary>Text for screen readers. Electron's <c>accessibilityLabel</c>.</summary>
    public string? AccessibilityLabel { get => _accessibilityLabel; set => Set(ref _accessibilityLabel, value); }

    /// <summary>A hex colour such as <c>#ABCDEF</c>. Electron's <c>backgroundColor</c>.</summary>
    public string? BackgroundColor { get => _backgroundColor; set => Set(ref _backgroundColor, value); }

    /// <summary>The image.</summary>
    public NativeImage? Icon { get => _icon; set => Set(ref _icon, value); }

    /// <summary>Where the image sits.</summary>
    public TouchBarIconPosition? IconPosition { get => _iconPosition; set => Set(ref _iconPosition, value); }

    /// <summary>Whether it can be pressed; true by default.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Runs when the button is pressed. Electron's <c>click</c>.</summary>
    public Action? Click { get; init; }

    internal override bool Interact(TouchBarInteraction details)
    {
        if (Click is null) return false;
        Click();
        return true;
    }
}

/// <summary>A colour picker. Electron's <c>TouchBarColorPicker</c>.</summary>
public sealed class TouchBarColorPicker : TouchBarItem
{
    private IReadOnlyList<string>? _availableColors;
    private string? _selectedColor;

    internal override string Type => "colorpicker";

    /// <summary>The hex colours offered. Electron's <c>availableColors</c>.</summary>
    public IReadOnlyList<string>? AvailableColors { get => _availableColors; set => Set(ref _availableColors, value); }

    /// <summary>The selected hex colour. Electron's <c>selectedColor</c>.</summary>
    public string? SelectedColor { get => _selectedColor; set => Set(ref _selectedColor, value); }

    /// <summary>Runs with the colour the user picked. Electron's <c>change</c>.</summary>
    public Action<string>? Change { get; init; }

    // Only with a callback does a pick update SelectedColor, and then without a change event.
    internal override bool Interact(TouchBarInteraction details)
    {
        if (Change is null) return false;
        _selectedColor = details.Color;
        Change(details.Color!);
        return true;
    }
}

/// <summary>A group of items. Electron's <c>TouchBarGroup</c>.</summary>
public sealed class TouchBarGroup : TouchBarItem
{
    private TouchBar _items;

    /// <summary>An empty group.</summary>
    public TouchBarGroup()
    {
        _items = new TouchBar();
        Reparent(null, _items);
    }

    internal override string Type => "group";

    internal override TouchBar? Child => _items;

    /// <summary>The grouped items. Electron's <c>items</c> option and <c>child</c> property.</summary>
    public TouchBar Items
    {
        get => _items;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Reparent(_items, value);
            Set(ref _items, value);
        }
    }
}

/// <summary>A text label. Electron's <c>TouchBarLabel</c>.</summary>
public sealed class TouchBarLabel : TouchBarItem
{
    private string? _label, _accessibilityLabel, _textColor;

    internal override string Type => "label";

    /// <summary>The text.</summary>
    public string? Label { get => _label; set => Set(ref _label, value); }

    /// <summary>Text for screen readers.</summary>
    public string? AccessibilityLabel { get => _accessibilityLabel; set => Set(ref _accessibilityLabel, value); }

    /// <summary>A hex colour such as <c>#ABCDEF</c>.</summary>
    public string? TextColor { get => _textColor; set => Set(ref _textColor, value); }
}

/// <summary>A button that opens a further bar. Electron's <c>TouchBarPopover</c>.</summary>
public sealed class TouchBarPopover : TouchBarItem
{
    private string? _label;
    private NativeImage? _icon;
    private bool? _showCloseButton;
    private TouchBar _items;

    /// <summary>A popover with no items.</summary>
    public TouchBarPopover()
    {
        _items = new TouchBar();
        Reparent(null, _items);
    }

    internal override string Type => "popover";

    internal override TouchBar? Child => _items;

    /// <summary>The button's text.</summary>
    public string? Label { get => _label; set => Set(ref _label, value); }

    /// <summary>The button's image.</summary>
    public NativeImage? Icon { get => _icon; set => Set(ref _icon, value); }

    /// <summary>Whether the popover has a close button; unset shows one. Electron's <c>showCloseButton</c>.</summary>
    public bool? ShowCloseButton { get => _showCloseButton; set => Set(ref _showCloseButton, value); }

    /// <summary>The popover's items. Electron's <c>items</c> option and <c>child</c> property.</summary>
    public TouchBar Items
    {
        get => _items;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Reparent(_items, value);
            Set(ref _items, value);
        }
    }
}

/// <summary>A slider. Electron's <c>TouchBarSlider</c>.</summary>
public sealed class TouchBarSlider : TouchBarItem
{
    private string? _label;
    private int? _minValue, _maxValue, _value;

    internal override string Type => "slider";

    /// <summary>The text beside it.</summary>
    public string? Label { get => _label; set => Set(ref _label, value); }

    /// <summary>The lowest value. Electron's <c>minValue</c>.</summary>
    public int? MinValue { get => _minValue; set => Set(ref _minValue, value); }

    /// <summary>The highest value. Electron's <c>maxValue</c>.</summary>
    public int? MaxValue { get => _maxValue; set => Set(ref _maxValue, value); }

    /// <summary>The value.</summary>
    public int? Value { get => _value; set => Set(ref _value, value); }

    /// <summary>Runs with the value the user set. Electron's <c>change</c>.</summary>
    public Action<int>? Change { get; init; }

    internal override bool Interact(TouchBarInteraction details)
    {
        if (Change is null) return false;
        _value = details.Value;
        Change(details.Value!.Value);
        return true;
    }
}

/// <summary>Space between items. Electron's <c>TouchBarSpacer</c>.</summary>
public sealed class TouchBarSpacer : TouchBarItem
{
    internal override string Type => "spacer";

    /// <summary>The width; fixed once made.</summary>
    public TouchBarSpacerSize? Size { get; init; }
}

/// <summary>Segments to choose from. Electron's <c>TouchBarSegmentedControl</c>.</summary>
public sealed class TouchBarSegmentedControl : TouchBarItem
{
    private TouchBarSegmentStyle? _segmentStyle;
    private IReadOnlyList<SegmentedControlSegment> _segments = [];
    private int? _selectedIndex;
    private TouchBarSegmentedControlMode? _mode;

    internal override string Type => "segmented_control";

    /// <summary>The look. Electron's <c>segmentStyle</c>.</summary>
    public TouchBarSegmentStyle? SegmentStyle { get => _segmentStyle; set => Set(ref _segmentStyle, value); }

    /// <summary>The segments.</summary>
    public IReadOnlyList<SegmentedControlSegment> Segments
    {
        get => _segments;
        set => Set(ref _segments, value ?? []);
    }

    /// <summary>The selected segment's index. Electron's <c>selectedIndex</c>.</summary>
    public int? SelectedIndex { get => _selectedIndex; set => Set(ref _selectedIndex, value); }

    /// <summary>How it selects.</summary>
    public TouchBarSegmentedControlMode? Mode { get => _mode; set => Set(ref _mode, value); }

    /// <summary>Runs with the index the user chose and whether it is now selected. Electron's <c>change</c>.</summary>
    public Action<int, bool>? Change { get; init; }

    internal override bool Interact(TouchBarInteraction details)
    {
        if (Change is null) return false;
        _selectedIndex = details.SelectedIndex;
        Change(details.SelectedIndex!.Value, details.IsSelected ?? false);
        return true;
    }
}

/// <summary>A scrollable strip of items. Electron's <c>TouchBarScrubber</c>.</summary>
public sealed class TouchBarScrubber : TouchBarItem
{
    private IReadOnlyList<ScrubberItem>? _items;
    private TouchBarScrubberStyle? _selectedStyle, _overlayStyle;
    private bool _showArrowButtons;
    private TouchBarScrubberMode _mode = TouchBarScrubberMode.Free;
    private bool _continuous = true;

    internal override string Type => "scrubber";

    /// <summary>The items.</summary>
    public IReadOnlyList<ScrubberItem>? Items { get => _items; set => Set(ref _items, value); }

    /// <summary>How the selected item is marked; none by default. Electron's <c>selectedStyle</c>.</summary>
    public TouchBarScrubberStyle? SelectedStyle { get => _selectedStyle; set => Set(ref _selectedStyle, value); }

    /// <summary>How the selected item is overlaid; none by default. Electron's <c>overlayStyle</c>.</summary>
    public TouchBarScrubberStyle? OverlayStyle { get => _overlayStyle; set => Set(ref _overlayStyle, value); }

    /// <summary>Whether arrows scroll it. Electron's <c>showArrowButtons</c>.</summary>
    public bool ShowArrowButtons { get => _showArrowButtons; set => Set(ref _showArrowButtons, value); }

    /// <summary>How it moves; <see cref="TouchBarScrubberMode.Free"/> by default.</summary>
    public TouchBarScrubberMode Mode { get => _mode; set => Set(ref _mode, value); }

    /// <summary>Whether it scrolls continuously; true by default.</summary>
    public bool Continuous { get => _continuous; set => Set(ref _continuous, value); }

    /// <summary>Runs with the index the user selected. Electron's <c>select</c>.</summary>
    public Action<int>? Select { get; init; }

    /// <summary>Runs with the index the user moved over. Electron's <c>highlight</c>.</summary>
    public Action<int>? Highlight { get; init; }

    internal override bool Interact(TouchBarInteraction details)
    {
        if (Select is null && Highlight is null) return false;
        if (details.Type == "select") Select?.Invoke(details.SelectedIndex!.Value);
        else Highlight?.Invoke(details.HighlightedIndex!.Value);
        return true;
    }
}

/// <summary>Where the system's items go among the app's. Electron's <c>TouchBarOtherItemsProxy</c>.</summary>
public sealed class TouchBarOtherItemsProxy : TouchBarItem
{
    internal override string Type => "other_items_proxy";
}

/// <summary>What a <see cref="TouchBar"/> needs from a window: Electron's <c>_setTouchBarItems</c>,
/// <c>_refreshTouchBarItem</c>, <c>_setEscapeTouchBarItem</c> and <c>-touch-bar-interaction</c>.</summary>
internal interface ITouchBarHost
{
    int Id { get; }
    TouchBar? AttachedTouchBar { get; set; }
    void SetTouchBarItems(IReadOnlyList<TouchBarItem> items);
    void RefreshTouchBarItem(string id);
    void SetEscapeTouchBarItem(TouchBarItem? item);
    event Action<string, TouchBarInteraction>? TouchBarInteraction;
    event Action? HostClosed;
}

/// <summary>A bar of items for the MacBook Touch Bar, shown with <see cref="BaseWindow.SetTouchBar(TouchBar?)"/>.
/// Electron's <c>TouchBar</c>.</summary>
public sealed class TouchBar
{
    private readonly Dictionary<string, TouchBarItem> _items = [];
    private readonly List<TouchBarItem> _orderedItems = [];
    private readonly Dictionary<int, Action> _windowListeners = [];
    private TouchBarItem? _escapeItem;

    /// <summary>A bar of <paramref name="items"/>, in order. Electron's <c>new TouchBar({ items })</c>.</summary>
    /// <exception cref="InvalidOperationException">More than one <see cref="TouchBarOtherItemsProxy"/>, or an item twice.</exception>
    public TouchBar(params IEnumerable<TouchBarItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        var hasProxy = false;
        var ids = new HashSet<string>();
        foreach (var item in list)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
            if (item is TouchBarOtherItemsProxy)
            {
                if (hasProxy) throw new InvalidOperationException("Must only have one OtherItemsProxy per TouchBar");
                hasProxy = true;
            }
            if (!ids.Add(item.Id)) throw new InvalidOperationException("Cannot add a single instance of TouchBarItem multiple times in a TouchBar");
        }
        foreach (var item in list)
        {
            _orderedItems.Add(item);
            Register(item);
        }
    }

    /// <summary>The items, in order. Electron's <c>orderedItems</c>.</summary>
    public IReadOnlyList<TouchBarItem> OrderedItems => _orderedItems;

    /// <summary>An item shown in place of the Esc key; null for the key. Electron's <c>escapeItem</c>.</summary>
    public TouchBarItem? EscapeItem
    {
        get => _escapeItem;
        set
        {
            if (_escapeItem is not null) _escapeItem.Changed -= OnItemChanged;
            _escapeItem = value;
            if (_escapeItem is not null) _escapeItem.Changed += OnItemChanged;
            EscapeItemChanged?.Invoke(value);
        }
    }

    // Electron's change and escape-item-change events.
    internal event Action<string, string>? Changed;

    internal event Action<TouchBarItem?>? EscapeItemChanged;

    /// <summary>The item with <paramref name="id"/> here or in a group or popover, as Electron's <c>items</c> map.</summary>
    internal TouchBarItem? Find(string id) => _items.GetValueOrDefault(id);

    private void Register(TouchBarItem item)
    {
        _items[item.Id] = item;
        item.Changed += OnItemChanged;
        if (item.Child is { } child)
            foreach (var nested in child.OrderedItems) Register(nested);
    }

    private void OnItemChanged(TouchBarItem item) => Changed?.Invoke(item.Id, item.Type);

    /// <summary>Electron's <c>TouchBar._setOnWindow</c>: replaces the window's bar, or clears it for null.</summary>
    internal static void SetOnWindow(TouchBar? touchBar, ITouchBarHost window)
    {
        window.AttachedTouchBar?.RemoveFromWindow(window);
        if (touchBar is null)
        {
            window.SetTouchBarItems([]);
            return;
        }
        touchBar.AddToWindow(window);
    }

    private void AddToWindow(ITouchBarHost window)
    {
        var id = window.Id;
        if (_windowListeners.ContainsKey(id)) return;
        window.AttachedTouchBar = this;

        void OnChanged(string itemId, string type) => window.RefreshTouchBarItem(itemId);
        void OnEscapeItemChanged(TouchBarItem? item) => window.SetEscapeTouchBarItem(item);
        void OnInteraction(string itemId, TouchBarInteraction details)
        {
            var item = _items.GetValueOrDefault(itemId);
            if (item is null && _escapeItem?.Id == itemId) item = _escapeItem;
            item?.Interact(details);
        }
        void RemoveListeners()
        {
            Changed -= OnChanged;
            EscapeItemChanged -= OnEscapeItemChanged;
            window.TouchBarInteraction -= OnInteraction;
            window.HostClosed -= RemoveListeners;
            window.AttachedTouchBar = null;
            _windowListeners.Remove(id);
            // As Electron's, this drops the item listeners the constructor added; a bar added again no longer follows them.
            Unregister(_orderedItems);
            if (_escapeItem is not null) _escapeItem.Changed -= OnItemChanged;
        }

        Changed += OnChanged;
        EscapeItemChanged += OnEscapeItemChanged;
        window.TouchBarInteraction += OnInteraction;
        window.HostClosed += RemoveListeners;
        _windowListeners[id] = RemoveListeners;

        window.SetTouchBarItems(_orderedItems);
        OnEscapeItemChanged(_escapeItem);
    }

    private void Unregister(IEnumerable<TouchBarItem> items)
    {
        foreach (var item in items)
        {
            item.Changed -= OnItemChanged;
            if (item.Child is { } child) Unregister(child.OrderedItems);
        }
    }

    private void RemoveFromWindow(ITouchBarHost window)
    {
        if (_windowListeners.TryGetValue(window.Id, out var remove)) remove();
    }
}
