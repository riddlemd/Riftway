using System.Collections;

namespace Oriel;

/// <summary>What a <see cref="MenuItem"/> is; Electron's <c>type</c>.</summary>
public enum MenuItemType
{
    /// <summary>A plain command. Electron's <c>normal</c>.</summary>
    Normal,

    /// <summary>A dividing line. Electron's <c>separator</c>.</summary>
    Separator,

    /// <summary>Opens <see cref="MenuItem.Submenu"/>. Electron's <c>submenu</c>.</summary>
    Submenu,

    /// <summary>Toggles <see cref="MenuItem.Checked"/> when clicked. Electron's <c>checkbox</c>.</summary>
    Checkbox,

    /// <summary>One of a group: radio items with no separator between them form a group, and clicking one unchecks
    /// the others. The group is fixed when the item is inserted. Electron's <c>radio</c>.</summary>
    Radio,

    /// <summary>A section title (macOS 14 and later); a normal item elsewhere and on older macOS. Electron's <c>header</c>.</summary>
    Header,

    /// <summary>Opens <see cref="MenuItem.Submenu"/> laid out as a palette (macOS 14 and later); a submenu elsewhere and on
    /// older macOS. Electron's <c>palette</c>.</summary>
    Palette,
}

/// <summary>What a <see cref="MenuItemBadge"/> counts. Electron's badge <c>type</c>.</summary>
public enum MenuItemBadgeType
{
    /// <summary>Free text in <see cref="MenuItemBadge.Content"/>. Electron's <c>none</c>, the default.</summary>
    None,

    /// <summary>A count of alerts. Electron's <c>alerts</c>.</summary>
    Alerts,

    /// <summary>A count of updates. Electron's <c>updates</c>.</summary>
    Updates,

    /// <summary>A count of new items. Electron's <c>new-items</c>.</summary>
    NewItems,
}

/// <summary>A badge at a menu item's trailing edge (macOS 14 and later). Electron's <c>MenuItemBadge</c>.</summary>
public sealed record MenuItemBadge
{
    /// <summary>What the badge shows. Electron's <c>type</c>.</summary>
    public MenuItemBadgeType Type { get; init; }

    /// <summary>The count, for every type but <see cref="MenuItemBadgeType.None"/>. Electron's <c>count</c>.</summary>
    public int? Count { get; init; }

    /// <summary>The text, for <see cref="MenuItemBadgeType.None"/> only. Electron's <c>content</c>.</summary>
    public string? Content { get; init; }

    /// <summary>Electron's <c>validateBadge</c>, with its messages.</summary>
    /// <exception cref="ArgumentException">A field the type does not take, or a count that is not a non-negative integer.</exception>
    internal MenuItemBadge Validated()
    {
        if (Type == MenuItemBadgeType.None)
        {
            if (Content is null) throw new ArgumentException("badge.content must be a string when badge.type is 'none'");
            if (Count is not null) throw new ArgumentException("badge.count cannot be used when badge.type is 'none'");
        }
        else
        {
            if (Count is not >= 0) throw new ArgumentException($"badge.count must be a non-negative integer when badge.type is '{Name}'");
            if (Content is not null) throw new ArgumentException("badge.content can only be used when badge.type is 'none'");
        }
        return this;
    }

    /// <summary>Electron's name of the type.</summary>
    internal string Name => Type switch
    {
        MenuItemBadgeType.Alerts => "alerts",
        MenuItemBadgeType.Updates => "updates",
        MenuItemBadgeType.NewItems => "new-items",
        _ => "none",
    };
}

/// <summary>A predefined behaviour for a <see cref="MenuItem"/>, with its own label and accelerator unless
/// those are set; Electron's <c>role</c>.</summary>
/// <remarks>On macOS the editing roles, <see cref="About"/>, <see cref="Hide"/>, <see cref="HideOthers"/>,
/// <see cref="Unhide"/> and <see cref="Front"/> are AppKit's own actions, so they also work in DevTools and
/// native text fields; there a role item's <see cref="MenuItem.Click"/> never runs.</remarks>
public enum MenuItemRole
{
    /// <summary>No role: the item runs <see cref="MenuItem.Click"/>.</summary>
    None,

    /// <summary><see cref="WebContents.Undo"/>.</summary>
    Undo,

    /// <summary><see cref="WebContents.Redo"/>.</summary>
    Redo,

    /// <summary><see cref="WebContents.Cut"/>.</summary>
    Cut,

    /// <summary><see cref="WebContents.Copy"/>.</summary>
    Copy,

    /// <summary><see cref="WebContents.Paste"/>.</summary>
    Paste,

    /// <summary><see cref="WebContents.PasteAndMatchStyle"/>.</summary>
    PasteAndMatchStyle,

    /// <summary><see cref="WebContents.Delete"/>.</summary>
    Delete,

    /// <summary><see cref="WebContents.SelectAll"/>.</summary>
    SelectAll,

    /// <summary><see cref="WebContents.Reload"/> on the focused window.</summary>
    Reload,

    /// <summary><see cref="WebContents.ReloadIgnoringCache"/> on the focused window.</summary>
    ForceReload,

    /// <summary><see cref="WebContents.ToggleDevTools"/> on the focused window.</summary>
    ToggleDevTools,

    /// <summary>Zoom level 0 on the focused window. Electron's <c>resetZoom</c>.</summary>
    ResetZoom,

    /// <summary>Zoom level +0.5 on the focused window.</summary>
    ZoomIn,

    /// <summary>Zoom level −0.5 on the focused window.</summary>
    ZoomOut,

    /// <summary>Toggles the focused window's full screen, honouring <see cref="BaseWindowOptions.SimpleFullScreen"/>.
    /// Electron's <c>togglefullscreen</c>.</summary>
    ToggleFullScreen,

    /// <summary>Minimizes the focused window.</summary>
    Minimize,

    /// <summary>Closes the focused window, as <see cref="BaseWindow.Close"/>.</summary>
    Close,

    /// <summary>Maximizes the focused window, or restores it if maximized. Electron's <c>zoom</c> (macOS).</summary>
    Zoom,

    /// <summary>Quits the application.</summary>
    Quit,

    /// <summary>macOS: the standard About panel.</summary>
    About,

    /// <summary>macOS: hides the application.</summary>
    Hide,

    /// <summary>macOS: hides other applications.</summary>
    HideOthers,

    /// <summary>macOS: shows all applications. Electron's <c>unhide</c>.</summary>
    Unhide,

    /// <summary>macOS: brings all the application's windows to the front.</summary>
    Front,

    /// <summary>macOS: a submenu the system fills with Services.</summary>
    Services,

    /// <summary>A submenu: on macOS the system adds the open windows to it. Electron's <c>window</c>.</summary>
    Window,

    /// <summary>A submenu: on macOS the system adds a search field to it. Electron's <c>help</c>.</summary>
    Help,

    /// <summary>The default application menu (About, Services, Hide, Quit), named after the app. Electron's <c>appMenu</c>.</summary>
    AppMenu,

    /// <summary>The default File menu: Close on macOS, Quit elsewhere. Electron's <c>fileMenu</c>.</summary>
    FileMenu,

    /// <summary>The default Edit menu. Electron's <c>editMenu</c>.</summary>
    EditMenu,

    /// <summary>The default View menu: reload, DevTools, zoom, full screen. Electron's <c>viewMenu</c>.</summary>
    ViewMenu,

    /// <summary>The default Window menu. Electron's <c>windowMenu</c>.</summary>
    WindowMenu,

    /// <summary>Turns the spell checker of the focused page's session on or off. Electron's <c>toggleSpellChecker</c>.</summary>
    ToggleSpellChecker,

    /// <summary>macOS: reads the selection aloud. Electron's <c>startSpeaking</c>.</summary>
    StartSpeaking,

    /// <summary>macOS: stops reading aloud. Electron's <c>stopSpeaking</c>.</summary>
    StopSpeaking,

    /// <summary>macOS: the Substitutions panel. Electron's <c>showSubstitutions</c>.</summary>
    ShowSubstitutions,

    /// <summary>macOS: smart quotes on or off. Electron's <c>toggleSmartQuotes</c>.</summary>
    ToggleSmartQuotes,

    /// <summary>macOS: smart dashes on or off. Electron's <c>toggleSmartDashes</c>.</summary>
    ToggleSmartDashes,

    /// <summary>macOS: text replacement on or off. Electron's <c>toggleTextReplacement</c>.</summary>
    ToggleTextReplacement,

    /// <summary>macOS: shows or hides the window tab bar. Electron's <c>toggleTabBar</c>.</summary>
    ToggleTabBar,

    /// <summary>macOS: the next window tab. Electron's <c>selectNextTab</c>.</summary>
    SelectNextTab,

    /// <summary>macOS: the previous window tab. Electron's <c>selectPreviousTab</c>.</summary>
    SelectPreviousTab,

    /// <summary>macOS: the tab overview. Electron's <c>showAllTabs</c>.</summary>
    ShowAllTabs,

    /// <summary>macOS: merges every window into tabs of one. Electron's <c>mergeAllWindows</c>.</summary>
    MergeAllWindows,

    /// <summary>macOS: moves the tab to a window of its own. Electron's <c>moveTabToNewWindow</c>.</summary>
    MoveTabToNewWindow,

    /// <summary>macOS: empties the recent documents list. Electron's <c>clearRecentDocuments</c>.</summary>
    ClearRecentDocuments,

    /// <summary>macOS: a submenu of the sharing services for <see cref="MenuItem.SharingItem"/>; empty elsewhere.
    /// Electron's <c>shareMenu</c>.</summary>
    ShareMenu,

    /// <summary>macOS: its submenu becomes AppKit's Open Recent menu, which lists the recent documents above the
    /// submenu's own items (such as <see cref="ClearRecentDocuments"/>). Electron's <c>recentDocuments</c>.</summary>
    RecentDocuments,
}

/// <summary>An entry in a <see cref="Menu"/>; Electron's <c>MenuItem</c>.</summary>
/// <remarks>Changes to <see cref="Label"/>, <see cref="Enabled"/>, <see cref="Visible"/>, <see cref="Checked"/>
/// <see cref="Accelerator"/> and <see cref="RegisterAccelerator"/> show in menus already in use.</remarks>
public sealed class MenuItem
{
    private static int s_nextCommandId;

    private string? _label;
    private string? _accelerator;
    private bool? _registerAccelerator;
    private bool _enabled = true;
    private bool _visible = true;
    private bool _checked;
    private Menu? _submenu;
    private NativeImage? _icon;
    private string? _sublabel;
    private string? _toolTip;
    private MenuItemBadge? _badge;
    private MenuItemType? _type;

    /// <summary>Creates an item; set its properties with an object initializer.</summary>
    public MenuItem() => CommandId = NextCommandId();

    /// <summary>A command id no other item or menu has.</summary>
    internal static int NextCommandId() => Interlocked.Increment(ref s_nextCommandId);

    /// <summary>Identifies the item to native menus; unique in the process.</summary>
    internal int CommandId { get; }

    /// <summary>The menu this item is in, if any.</summary>
    internal Menu? Parent { get; set; }

    /// <summary>A radio item's group, fixed when it is inserted; 0 for other items. Electron's <c>groupId</c>.</summary>
    internal int GroupId { get; set; }

    /// <summary>Sets <see cref="Checked"/> without telling native menus, as Electron's <c>_menuWillShow</c> writes its
    /// checked map: menus read the change only when next rebuilt or refreshed.</summary>
    internal void SetCheckedQuietly(bool value) => _checked = value;

    /// <summary>A name for <see cref="Menu.GetMenuItemById"/>. Electron's <c>id</c>.</summary>
    public string? Id { get; init; }

    /// <summary>The item's kind; <see cref="MenuItemType.Submenu"/> when <see cref="Submenu"/> is set, otherwise
    /// <see cref="MenuItemType.Normal"/>. Electron's <c>type</c>.</summary>
    public MenuItemType Type
    {
        get => _type ?? (Submenu is not null ? MenuItemType.Submenu : MenuRoles.HasOwnCheck(Role) ? MenuItemType.Checkbox : MenuItemType.Normal);
        init => _type = value;
    }

    /// <summary>The predefined behaviour, if any. Electron's <c>role</c>.</summary>
    public MenuItemRole Role { get; init; }

    /// <summary>The text shown; <c>&amp;</c> marks the mnemonic letter on Windows and Linux (<c>&amp;&amp;</c> for
    /// an ampersand). Defaults to the role's label. Electron's <c>label</c>.</summary>
    public string Label
    {
        get => _label ?? MenuRoles.Label(Role) ?? string.Empty;
        set => Set(ref _label, value);
    }

    /// <summary>An image beside the label (macOS menus; Windows and Linux not yet, drift M3); load a file with
    /// <see cref="NativeImage.CreateFromPath(string)"/>. Electron's <c>icon</c>.</summary>
    public NativeImage? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    /// <summary>A second line under the label (macOS 14.4 and later; elsewhere not yet, drift M3). Electron's <c>sublabel</c>.</summary>
    public string? Sublabel
    {
        get => _sublabel;
        set => Set(ref _sublabel, value);
    }

    /// <summary>A badge at the item's trailing edge (macOS 14 and later); setting it updates an item already in a menu.
    /// Electron's <c>badge</c>.</summary>
    /// <exception cref="ArgumentException">On macOS, a badge Electron's <c>validateBadge</c> refuses.</exception>
    public MenuItemBadge? Badge
    {
        get => _badge;
        set => Set(ref _badge, OperatingSystem.IsMacOS() ? value?.Validated() : value);
    }

    /// <summary>The text shown when the pointer rests on the item (macOS). Electron's <c>toolTip</c>.</summary>
    public string? ToolTip
    {
        get => _toolTip;
        set => Set(ref _toolTip, value);
    }

    /// <summary>Whether <see cref="Accelerator"/> is registered with the window on Windows and Linux; when false the menu
    /// still shows it but the key goes to the page. False by default for the <see cref="MenuItemRole.Copy"/>,
    /// <see cref="MenuItemRole.Cut"/>, <see cref="MenuItemRole.Paste"/> and <see cref="MenuItemRole.PasteAndMatchStyle"/>
    /// roles, true otherwise. macOS menus keep their key equivalents either way, as Electron's. Electron's
    /// <c>registerAccelerator</c>.</summary>
    public bool RegisterAccelerator
    {
        get => _registerAccelerator ?? MenuRoles.RegistersAccelerator(Role);
        set => Set(ref _registerAccelerator, value);
    }

    /// <summary>The keyboard shortcut, e.g. <c>CmdOrCtrl+Shift+S</c>; defaults to the role's. Electron's <c>accelerator</c>.</summary>
    /// <remarks>On Windows and Linux, <c>Command</c>/<c>Super</c>/<c>Meta</c> is the Windows (Super) key: the shortcut works
    /// once the page leaves the key unhandled, but its menu item shows no shortcut text.</remarks>
    /// <exception cref="ArgumentException">Not a valid accelerator.</exception>
    public string? Accelerator
    {
        get => _accelerator ?? MenuRoles.Accelerator(Role);
        set
        {
            if (value is not null) Oriel.Accelerator.Parse(value, OperatingSystem.IsMacOS());
            Set(ref _accelerator, value);
        }
    }

    /// <summary>The accelerator as set, without the role's default.</summary>
    internal string? ExplicitAccelerator => _accelerator;

    /// <summary>Whether the item can be chosen. Electron's <c>enabled</c>.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    /// <summary>Whether the item is shown. Electron's <c>visible</c>.</summary>
    public bool Visible
    {
        get => _visible;
        set => Set(ref _visible, value);
    }

    /// <summary>Whether a <see cref="MenuItemType.Checkbox"/> or <see cref="MenuItemType.Radio"/> item is checked.
    /// Electron's <c>checked</c>.</summary>
    public bool Checked
    {
        get => _checked;
        set => Set(ref _checked, value);
    }

    /// <summary>The menu this item opens; role menus such as <see cref="MenuItemRole.EditMenu"/> supply their own.
    /// Electron's <c>submenu</c>.</summary>
    /// <exception cref="InvalidOperationException">The menu already belongs to another item.</exception>
    public Menu? Submenu
    {
        get => _submenu ??= MenuRoles.Submenu(Role) is { } menu ? Adopt(menu) : null;
        init => _submenu = value is null ? null : Adopt(value);
    }

    /// <summary>What a <see cref="MenuItemRole.ShareMenu"/> item shares. Electron's <c>sharingItem</c>.</summary>
    /// <exception cref="ArgumentException">A file path is empty or a URL is not valid.</exception>
    public SharingItem? SharingItem
    {
        get;
        init => field = value?.Validated();
    }

    /// <summary>Runs when the item is chosen, with the focused window (or null) and the keys held; not for role items,
    /// whose role runs instead. Electron's <c>click(menuItem, browserWindow, event)</c>.</summary>
    public Action<MenuItem, BrowserWindow?, KeyboardEvent>? Click { get; set; }

    private Menu Adopt(Menu menu)
    {
        if (menu.OwnerItem is not null) throw new InvalidOperationException("That menu is already the submenu of another item.");
        menu.OwnerItem = this;
        return menu;
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Parent?.OnChanged();
    }

    /// <summary>Chooses the item as a click or accelerator would: toggles checkbox and radio state, then runs
    /// the role or <see cref="Click"/>.</summary>
    internal void Activate(BaseWindow? focusedWindow, KeyboardEvent? keys = null)
    {
        if (!CommandEnabled) return;
        Run(focusedWindow, keys);
    }

    /// <summary>Electron's <c>menuItem.click</c> as <c>Menu._executeCommand</c> calls it, which does not ask whether the
    /// item is enabled: what a DBusMenu <c>Event</c> runs.</summary>
    internal void Run(BaseWindow? focusedWindow, KeyboardEvent? keys = null)
    {
        if (Type == MenuItemType.Checkbox && !MenuRoles.HasOwnCheck(Role)) Checked = !Checked;
        else if (Type == MenuItemType.Radio && Parent is { } parent) parent.CheckRadio(this);
        // Click names only a BrowserWindow (DRIFT V15); a role acts on any window.
        if (!MenuRoles.Execute(Role, focusedWindow)) Click?.Invoke(this, focusedWindow as BrowserWindow, keys ?? new KeyboardEvent());
    }

    /// <summary>Electron's <c>getCheckStatus</c>: <see cref="Checked"/>, or for <see cref="MenuItemRole.ToggleSpellChecker"/>
    /// whether the focused page's session (else the default session) checks spelling. What menus show.</summary>
    internal bool CheckStatus => MenuRoles.HasOwnCheck(Role) ? SpellCheckerEnabled() : Checked;

    /// <summary>Electron's <c>_isCommandIdEnabled</c>: with a focused window, <see cref="MenuItemRole.Minimize"/>,
    /// <see cref="MenuItemRole.ToggleFullScreen"/> and <see cref="MenuItemRole.Close"/> follow what the window allows;
    /// otherwise <see cref="Enabled"/>. What menus show and what an activation checks.</summary>
    internal bool CommandEnabled => FocusedWindowAbilities() is { } window
        ? Role switch
        {
            MenuItemRole.Minimize => window.Minimizable,
            MenuItemRole.ToggleFullScreen => window.FullScreenable,
            MenuItemRole.Close => window.Closable,
            _ => Enabled,
        }
        : Enabled;

    internal static Func<(bool Minimizable, bool FullScreenable, bool Closable)?> FocusedWindowAbilities { get; set; }
        = () => BaseWindow.GetFocusedWindow() is { } w ? (w.IsMinimizable, w.IsFullScreenable, w.IsClosable) : null;

    internal static Func<bool> SpellCheckerEnabled { get; set; }
        = () => (WebContents.GetFocusedWebContents()?.Session ?? Session.DefaultSession).IsSpellCheckerEnabled();

    /// <summary>Chooses the item from its accelerator: the accelerator's modifiers were held. UI thread.</summary>
    internal void ActivateByAccelerator(BaseWindow? focusedWindow)
        => Activate(focusedWindow, KeyboardEvent.ForAccelerator(Accelerator));
}

/// <summary>The keys held when a menu item was chosen. Electron's <c>KeyboardEvent</c> (a menu item's <c>click</c> event).</summary>
public sealed record KeyboardEvent
{
    /// <summary>Shift was held.</summary>
    public bool ShiftKey { get; init; }

    /// <summary>Control was held.</summary>
    public bool CtrlKey { get; init; }

    /// <summary>Alt (Option on macOS) was held.</summary>
    public bool AltKey { get; init; }

    /// <summary>Command on macOS, the Windows or Super key elsewhere, was held.</summary>
    public bool MetaKey { get; init; }

    /// <summary>The item's accelerator chose it, not a click.</summary>
    public bool TriggeredByAccelerator { get; init; }

    internal static KeyboardEvent FromCefFlags(Oriel.Cef.Interop.cef_event_flags_t flags) => new()
    {
        ShiftKey = flags.HasFlag(Oriel.Cef.Interop.cef_event_flags_t.EVENTFLAG_SHIFT_DOWN),
        CtrlKey = flags.HasFlag(Oriel.Cef.Interop.cef_event_flags_t.EVENTFLAG_CONTROL_DOWN),
        AltKey = flags.HasFlag(Oriel.Cef.Interop.cef_event_flags_t.EVENTFLAG_ALT_DOWN),
        MetaKey = flags.HasFlag(Oriel.Cef.Interop.cef_event_flags_t.EVENTFLAG_COMMAND_DOWN),
    };

    // NSEventModifierFlags: Shift, Control, Option, Command.
    internal static KeyboardEvent FromMacModifiers(ulong flags, bool keyDown) => new()
    {
        ShiftKey = (flags & (1 << 17)) != 0,
        CtrlKey = (flags & (1 << 18)) != 0,
        AltKey = (flags & (1 << 19)) != 0,
        MetaKey = (flags & (1 << 20)) != 0,
        TriggeredByAccelerator = keyDown,
    };

    internal static KeyboardEvent ForAccelerator(string? accelerator)
    {
        if (accelerator is null) return new() { TriggeredByAccelerator = true };
        var modifiers = Oriel.Accelerator.Parse(accelerator, OperatingSystem.IsMacOS()).Modifiers.ForMenus();
        return new()
        {
            ShiftKey = modifiers.HasFlag(AcceleratorModifiers.Shift),
            CtrlKey = modifiers.HasFlag(AcceleratorModifiers.Control),
            AltKey = modifiers.HasFlag(AcceleratorModifiers.Alt),
            MetaKey = modifiers.HasFlag(AcceleratorModifiers.Command),
            TriggeredByAccelerator = true,
        };
    }
}

/// <summary>A list of <see cref="MenuItem"/>s: the application menu, a window's menu bar, or a submenu;
/// Electron's <c>Menu</c>.</summary>
/// <remarks>Build one with a collection initializer, as Electron's <c>Menu.buildFromTemplate()</c> does:
/// <code>new Menu { new MenuItem { Label = "&amp;File", Submenu = new Menu { new MenuItem { Role = MenuItemRole.Quit } } } }</code>
/// Items and menus may be changed from any thread; the native menus follow on the UI thread.</remarks>
public sealed class Menu : IEnumerable<MenuItem>
{
    private static readonly Lock s_gate = new();
    private static Menu? s_applicationMenu;
    private static bool s_applicationMenuSet;
    private static int s_groupIdIndex;

    private readonly List<MenuItem> _items = [];
    // Electron's groupsMap: each radio group's items in the order they were inserted.
    private readonly SortedDictionary<int, List<MenuItem>> _groups = [];
    private int _markerCommandId;

    /// <summary>The menu's items, in order. Electron's <c>menu.items</c>.</summary>
    public IReadOnlyList<MenuItem> Items
    {
        get
        {
            lock (s_gate) return [.. _items];
        }
    }

    /// <summary>The item whose submenu this is, if any.</summary>
    internal MenuItem? OwnerItem { get; set; }

    /// <summary>Raised on the changing thread when this menu or any submenu changes.</summary>
    internal event Action? Changed;

    /// <summary>Adds <paramref name="item"/> at the end; what a collection initializer calls.</summary>
    /// <exception cref="InvalidOperationException">The item is already in a menu.</exception>
    public void Add(MenuItem item) => Append(item);

    /// <summary>Adds <paramref name="item"/> at the end. Electron's <c>menu.append()</c>.</summary>
    /// <exception cref="InvalidOperationException">The item is already in a menu.</exception>
    public void Append(MenuItem item) => InsertCore(null, item);

    /// <summary>Inserts <paramref name="item"/> at <paramref name="position"/>. Electron's <c>menu.insert()</c>.</summary>
    /// <exception cref="InvalidOperationException">The item is already in a menu.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is outside the menu.</exception>
    public void Insert(int position, MenuItem item) => InsertCore(position, item);

    private void InsertCore(int? position, MenuItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (s_gate)
        {
            if (item.Parent is not null) throw new InvalidOperationException("That item is already in a menu.");
            var at = position ?? _items.Count;
            if (at < 0 || at > _items.Count) throw new ArgumentOutOfRangeException(nameof(position));
            if (item.Type == MenuItemType.Radio)
            {
                item.GroupId = GenerateGroupId(_items, at);
                if (!_groups.TryGetValue(item.GroupId, out var group)) _groups[item.GroupId] = group = [];
                group.Add(item);
            }
            _items.Insert(at, item);
            item.Parent = this;
        }
        OnChanged();
    }

    // Electron's generateGroupId: the group of the nearest radio item before the position (after it, at position 0)
    // with no separator between, else a new group. A normal item between two radio items does not split them.
    private static int GenerateGroupId(List<MenuItem> items, int position)
    {
        if (position > 0)
        {
            for (var i = position - 1; i >= 0; i--)
            {
                if (items[i].Type == MenuItemType.Radio) return items[i].GroupId;
                if (items[i].Type == MenuItemType.Separator) break;
            }
        }
        else if (position < items.Count)
        {
            for (var i = position; i < items.Count; i++)
            {
                if (items[i].Type == MenuItemType.Radio) return items[i].GroupId;
                if (items[i].Type == MenuItemType.Separator) break;
            }
        }
        return ++s_groupIdIndex;
    }

    /// <summary>Raised on the UI thread before the menu shows. Electron's <c>menu-will-show</c>.</summary>
    /// <remarks>As in Electron, which menus raise it depends on the OS: a popup or tray menu on Windows and Linux raises
    /// it on each menu and submenu as it opens; on macOS the popup's (or application menu's) top menu raises it for
    /// every menu that opens under it. A window menu bar's dropdown raises it on the dropdown's submenu, not on the
    /// bar's menu. The Linux tray raises it on a submenu the host is about to show, never on the top menu.</remarks>
    public event EventHandler? MenuWillShow;

    /// <summary>Raised on the UI thread when the menu closes. Electron's <c>menu-will-close</c>.</summary>
    /// <remarks>Raised by the menus that raise <see cref="MenuWillShow"/>, except the Linux tray's, which never
    /// reports a close.</remarks>
    public event EventHandler? MenuWillClose;

    /// <summary>Electron's <c>_menuWillShow</c> then <c>menu-will-show</c>: checks the first item of each radio group
    /// with none checked, then raises <see cref="MenuWillShow"/>. Returns the items it checked. UI thread.</summary>
    internal IReadOnlyList<MenuItem> OnMenuWillShow()
    {
        var fixedUp = EnsureRadioGroupsChecked();
        MenuWillShow?.Invoke(this, EventArgs.Empty);
        return fixedUp;
    }

    /// <summary>Raises <see cref="MenuWillClose"/>. UI thread.</summary>
    internal void OnMenuWillClose() => MenuWillClose?.Invoke(this, EventArgs.Empty);

    /// <summary>Electron's <c>_menuWillShow</c>: a radio group with no item checked gets its first-inserted item checked,
    /// quietly. Returns those items.</summary>
    internal IReadOnlyList<MenuItem> EnsureRadioGroupsChecked()
    {
        List<MenuItem> fixedUp = [];
        lock (s_gate)
        {
            foreach (var group in _groups.Values)
            {
                if (group.Exists(i => i.Checked)) continue;
                group[0].SetCheckedQuietly(true);
                fixedUp.Add(group[0]);
            }
        }
        return fixedUp;
    }

    /// <summary>A command id for a hidden native item that names this menu when it has no visible items of its own.</summary>
    internal int MarkerCommandId
    {
        get
        {
            lock (s_gate) return _markerCommandId != 0 ? _markerCommandId : _markerCommandId = MenuItem.NextCommandId();
        }
    }

    internal bool IsMarker(int commandId)
    {
        lock (s_gate) return _markerCommandId != 0 && _markerCommandId == commandId;
    }

    /// <summary>Shows the menu as a popup in a window, as a context menu. Electron's <c>menu.popup()</c>.</summary>
    /// <exception cref="InvalidOperationException">No window was given and none is focused.</exception>
    /// <exception cref="ArgumentException">Only one of <see cref="PopupOptions.X"/> and <see cref="PopupOptions.Y"/> is set.</exception>
    public void Popup(PopupOptions? options = null)
    {
        if (options is not null && options.X.HasValue != options.Y.HasValue)
            throw new ArgumentException("Set both X and Y, or neither.", nameof(options));
        var window = options?.Window ?? BaseWindow.GetFocusedWindow()
            ?? throw new InvalidOperationException("No window to show the menu in: set PopupOptions.Window or focus a window.");
        var token = _popups.Opened(window);
        var closed = options?.Closed;
        window.ShowPopup(this, options?.X, options?.Y, () =>
        {
            _popups.Closed(token);
            closed?.Invoke();
        });
    }

    private readonly MenuPopups<BaseWindow> _popups = new();

    /// <summary>Closes this menu's popup in <paramref name="window"/>, or in every window it is popped up in.
    /// Electron's <c>menu.closePopup()</c>.</summary>
    public void ClosePopup(BaseWindow? window = null)
    {
        foreach (var target in _popups.Targets(window)) target.ClosePopup();
    }

    /// <summary>The first item, here or in a submenu, whose <see cref="MenuItem.Id"/> is <paramref name="id"/>.
    /// Electron's <c>menu.getMenuItemById()</c>.</summary>
    public MenuItem? GetMenuItemById(string id)
    {
        foreach (var item in Items)
        {
            if (item.Id == id) return item;
            if (item.Submenu?.GetMenuItemById(id) is { } found) return found;
        }
        return null;
    }

    /// <summary>The item, here or in a submenu, with <paramref name="commandId"/>.</summary>
    internal MenuItem? FindByCommandId(int commandId)
    {
        foreach (var item in Items)
        {
            if (item.CommandId == commandId) return item;
            if (item.Submenu?.FindByCommandId(commandId) is { } found) return found;
        }
        return null;
    }

    /// <summary>Every item here and in submenus, depth first.</summary>
    internal IEnumerable<MenuItem> Descendants()
    {
        foreach (var item in Items)
        {
            yield return item;
            if (item.Submenu is { } submenu)
                foreach (var nested in submenu.Descendants()) yield return nested;
        }
    }

    /// <summary>Checks <paramref name="item"/> and unchecks the rest of its radio group, as Electron's radio
    /// <c>checked</c> setter.</summary>
    internal void CheckRadio(MenuItem item)
    {
        List<MenuItem> group;
        lock (s_gate) group = _groups.TryGetValue(item.GroupId, out var members) ? [.. members] : [item];
        foreach (var other in group) if (other != item) other.Checked = false;
        item.Checked = true;
    }

    internal void OnChanged()
    {
        Changed?.Invoke();
        OwnerItem?.Parent?.OnChanged();
    }

    /// <inheritdoc/>
    public IEnumerator<MenuItem> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Sets the application menu: on macOS the menu bar at the top of the screen, elsewhere the menu
    /// bar of every window without its own (<see cref="BaseWindow.SetMenu"/>). Null removes it.
    /// Electron's <c>Menu.setApplicationMenu()</c>.</summary>
    /// <remarks>On macOS the first item becomes the application menu, titled with the app's name whatever
    /// its label. If none is set by the end of <see cref="OrielApp.Ready"/>, macOS gets a default menu
    /// (application, Edit, View, Window) so that copy, paste and quit shortcuts work; unlike Electron,
    /// Windows and Linux get none.</remarks>
    public static void SetApplicationMenu(Menu? menu)
    {
        lock (s_gate)
        {
            s_applicationMenu = menu;
            s_applicationMenuSet = true;
        }
        ApplicationMenuChanged?.Invoke(menu);
    }

    /// <summary>The application menu, or null. Electron's <c>Menu.getApplicationMenu()</c>.</summary>
    public static Menu? GetApplicationMenu()
    {
        lock (s_gate) return s_applicationMenu;
    }

    /// <summary>Raised on the setting thread when <see cref="SetApplicationMenu"/> is called.</summary>
    internal static event Action<Menu?>? ApplicationMenuChanged;

    /// <summary>Sets the default application menu on macOS unless the app has set one.</summary>
    internal static void SetDefaultApplicationMenuIfUnset()
    {
        bool appSetOne;
        lock (s_gate) appSetOne = s_applicationMenuSet;
        if (NeedsDefaultApplicationMenu(OperatingSystem.IsMacOS(), appSetOne))
            SetApplicationMenu(MenuRoles.DefaultApplicationMenu());
    }

    /// <summary>Whether the default application menu goes in: only on macOS, where copy, paste and quit shortcuts
    /// need one; on Windows and Linux it would be a File/Edit/View/Window bar on every window.</summary>
    internal static bool NeedsDefaultApplicationMenu(bool isMacOS, bool appSetOne) => isMacOS && !appSetOne;
}
