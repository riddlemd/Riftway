using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>A window's menu bar on Windows and Linux: a row of CEF Views menu buttons above the page, with
/// the menu's accelerators registered on the window. UI thread only.</summary>
/// <remarks>Electron draws its own bar on Linux too; CEF Views windows have no native menu bar to use.</remarks>
internal sealed unsafe class WindowMenuBar
{
    private const int AltKeyCode = 0x12;

    private readonly BrowserWindow _owner;
    private readonly MenuModelDelegate _modelDelegate;
    private readonly List<int> _accelerators = [];
    // Buttons by mnemonic letter, for Alt+letter.
    private readonly Dictionary<char, nint> _mnemonics = [];
    private readonly List<MenuButtonDelegate> _buttonDelegates = [];
    private cef_window_t* _window;
    private cef_panel_t* _panel;
    private Menu? _menu;
    private bool _rebuildPosted;
    private bool _altAlone;

    internal WindowMenuBar(BrowserWindow owner)
    {
        _owner = owner;
        _modelDelegate = new MenuModelDelegate { CommandExecuted = Execute, Closed = ReleasePressedLock };
    }

    private cef_menu_button_pressed_lock_t* _pressedLock;

    /// <summary>Whether the bar is shown when there is a menu.</summary>
    internal bool Shown { get; private set; } = true;

    /// <summary>Whether the bar is on screen: a menu with at least one item, and <see cref="Shown"/>.</summary>
    internal bool IsVisible => _panel is not null && _menu is { Items.Count: > 0 } && Shown;

    /// <summary>Hides the bar until Alt is pressed.</summary>
    internal bool AutoHide { get; set; }

    /// <summary>Lays the window out as the bar above <paramref name="browserView"/>, which takes the rest.</summary>
    internal void Attach(cef_window_t* window, cef_browser_view_t* browserView)
    {
        _window = window;
        var vertical = new cef_box_layout_settings_t { size = (nuint)sizeof(cef_box_layout_settings_t) };
        var layout = window->@base.set_to_box_layout(&window->@base, &vertical);

        var horizontal = new cef_box_layout_settings_t { size = (nuint)sizeof(cef_box_layout_settings_t), horizontal = 1 };
        _panel = CefNative.cef_panel_create(null);
        NativeObject.Release(_panel->set_to_box_layout(_panel, &horizontal));
        NativeObject.AddRef(_panel);
        window->@base.add_child_view(&window->@base, (cef_view_t*)_panel);

        NativeObject.AddRef(browserView);
        window->@base.add_child_view(&window->@base, (cef_view_t*)browserView);
        NativeObject.AddRef(browserView);
        layout->set_flex_for_view(layout, (cef_view_t*)browserView, 1);
        NativeObject.Release(layout);
        UpdateVisibility();
    }

    internal void Detach()
    {
        if (_panel is not null) NativeObject.Release(_panel);
        _panel = null;
        _window = null;
        SetMenu(null);
    }

    /// <summary>Shows <paramref name="menu"/> in the bar, or none.</summary>
    internal void SetMenu(Menu? menu)
    {
        if (ReferenceEquals(menu, _menu))
        {
            Rebuild();
            return;
        }
        if (_menu is not null) _menu.Changed -= PostRebuild;
        _menu = menu;
        if (menu is not null) menu.Changed += PostRebuild;
        Rebuild();
    }

    internal void SetShown(bool shown)
    {
        Shown = shown;
        UpdateVisibility();
    }

    /// <summary>The bar's height when visible, else 0.</summary>
    internal int Height => IsVisible ? _panel->@base.get_preferred_size(&_panel->@base).height : 0;

    /// <summary>A window accelerator fired; true if it was one of the menu's.</summary>
    internal bool OnAccelerator(int commandId)
    {
        // A disabled item leaves the key to whatever else wants it.
        if (_menu?.FindByCommandId(commandId) is not { Enabled: true } item) return false;
        item.Activate(_owner);
        return true;
    }

    /// <summary>A key the page did not handle: Alt alone toggles an auto-hidden bar, Alt+letter opens a menu.</summary>
    internal bool OnKeyEvent(cef_key_event_t* key)
    {
        if (_menu is null) return false;
        var isDown = key->type is cef_key_event_type_t.KEYEVENT_RAWKEYDOWN or cef_key_event_type_t.KEYEVENT_KEYDOWN;
        if (key->windows_key_code == AltKeyCode)
        {
            if (isDown && (key->modifiers & (uint)cef_event_flags_t.EVENTFLAG_IS_REPEAT) == 0) _altAlone = true;
            else if (key->type == cef_key_event_type_t.KEYEVENT_KEYUP && _altAlone && AutoHide)
            {
                _altAlone = false;
                SetShown(!Shown);
                return true;
            }
            return false;
        }
        if (!isDown) return false;
        _altAlone = false;
        var alt = (key->modifiers & (uint)cef_event_flags_t.EVENTFLAG_ALT_DOWN) != 0;
        if (!alt || key->windows_key_code is < 'A' or > 'Z') return false;
        if (!_mnemonics.TryGetValue((char)key->windows_key_code, out var button)) return false;
        if (AutoHide && !Shown) SetShown(true);
        var menuButton = (cef_menu_button_t*)button;
        menuButton->trigger_menu(menuButton);
        return true;
    }

    /// <summary>Opens the top-level menu at <paramref name="index"/> as a click on its button would.</summary>
    internal bool TriggerMenu(int index)
    {
        if (_panel is null || index < 0 || index >= (int)_panel->get_child_view_count(_panel)) return false;
        var view = _panel->get_child_view_at(_panel, index);
        var button = view->as_button(view);
        var labelButton = button->as_label_button(button);
        var menuButton = labelButton->as_menu_button(labelButton);
        menuButton->trigger_menu(menuButton);
        NativeObject.Release(menuButton);
        NativeObject.Release(labelButton);
        NativeObject.Release(button);
        NativeObject.Release(view);
        return true;
    }

    private void PostRebuild() => CefThread.RunOnUi(() =>
    {
        if (_rebuildPosted) return;
        _rebuildPosted = true;
        CefThread.PostUi(Rebuild);
    });

    private void Rebuild()
    {
        _rebuildPosted = false;
        if (_window is null) return;
        foreach (var id in _accelerators) _window->remove_accelerator(_window, id);
        _accelerators.Clear();
        _mnemonics.Clear();
        _panel->remove_all_child_views(_panel);
        _buttonDelegates.Clear();

        if (_menu is { } menu)
        {
            foreach (var item in menu.Items)
            {
                if (!item.Visible || item.Type == MenuItemType.Separator) continue;
                AddButton(item);
            }
            foreach (var item in menu.Descendants()) RegisterAccelerator(item);
        }
        UpdateVisibility();
    }

    private void AddButton(MenuItem item)
    {
        var commandId = item.CommandId;
        var buttonDelegate = new MenuButtonDelegate((button, point, pressedLock) => ShowMenu(commandId, button, pressedLock));
        _buttonDelegates.Add(buttonDelegate);
        cef_string_utf16_t text = default;
        CefString.Set(&text, MenuLabels.WithoutMnemonics(item.Label));
        NativeObject.AddRef(buttonDelegate.Native);
        var button = CefNative.cef_menu_button_create(buttonDelegate.Native, &text);
        CefString.Clear(&text);
        var view = (cef_view_t*)button;
        view->set_enabled(view, item.Enabled ? 1 : 0);
        if (MnemonicOf(item.Label) is { } letter) _mnemonics.TryAdd(letter, (nint)button);
        // The panel takes the creation reference.
        _panel->add_child_view(_panel, view);
    }

    private void ShowMenu(int commandId, cef_menu_button_t* button, cef_menu_button_pressed_lock_t* pressedLock)
    {
        if (_menu?.FindByCommandId(commandId) is not { Submenu: { } submenu })
        {
            // A top-level item without a submenu acts as a button.
            _menu?.FindByCommandId(commandId)?.Activate(_owner);
            return;
        }

        NativeObject.AddRef(_modelDelegate.Native);
        var model = CefNative.cef_menu_model_create(_modelDelegate.Native);
        MenuModels.Fill(model, submenu);

        var view = (cef_view_t*)button;
        var bounds = view->get_bounds_in_screen(view);
        var origin = new cef_point_t { x = bounds.x, y = bounds.y + bounds.height };
        // The button stays pressed while the lock lives; the menu runs asynchronously, so keep it until it closes.
        ReleasePressedLock();
        NativeObject.AddRef(pressedLock);
        _pressedLock = pressedLock;
        button->show_menu(button, model, &origin, cef_menu_anchor_position_t.CEF_MENU_ANCHOR_TOPLEFT);
    }

    private void ReleasePressedLock()
    {
        if (_pressedLock is null) return;
        NativeObject.Release(_pressedLock);
        _pressedLock = null;
    }

    private void RegisterAccelerator(MenuItem item)
    {
        if (item.Type is MenuItemType.Separator or MenuItemType.Submenu || MenuModels.CefAccelerator(item) is not { } a) return;
        // Low priority: the page sees the key first, so editing keys keep working in text fields.
        _window->set_accelerator(_window, item.CommandId, a.KeyCode,
            MenuModels.Has(a, AcceleratorModifiers.Shift), MenuModels.Has(a, AcceleratorModifiers.Control), MenuModels.Has(a, AcceleratorModifiers.Alt), 0);
        _accelerators.Add(item.CommandId);
    }


    private void Execute(int commandId) => _menu?.FindByCommandId(commandId)?.Activate(_owner);

    private void UpdateVisibility()
    {
        if (_panel is null || _window is null) return;
        var view = (cef_view_t*)_panel;
        view->set_visible(view, IsVisible ? 1 : 0);
        _window->@base.layout(&_window->@base);
    }

    /// <summary>The letter after a single <c>&amp;</c>, upper-cased, or null.</summary>
    internal static char? MnemonicOf(string label)
    {
        for (var i = 0; i < label.Length - 1; i++)
        {
            if (label[i] != '&') continue;
            if (label[i + 1] == '&')
            {
                i++;
                continue;
            }
            return char.IsAsciiLetter(label[i + 1]) ? char.ToUpperInvariant(label[i + 1]) : null;
        }
        return null;
    }
}
