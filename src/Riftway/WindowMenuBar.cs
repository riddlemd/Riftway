using Riftway.Cef;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway;

/// <summary>A window's menu bar on Windows and Linux: a row of CEF Views menu buttons above the page, with
/// the menu's accelerators registered on the window. UI thread only.</summary>
/// <remarks>Electron draws its own bar on Linux too; CEF Views windows have no native menu bar to use.</remarks>
internal sealed unsafe class WindowMenuBar
{
    private const int AltKeyCode = 0x12;

    private readonly BaseWindow _owner;
    private readonly List<int> _accelerators = [];
    // Button positions by mnemonic letter, for Alt+letter.
    private readonly Dictionary<char, int> _mnemonics = [];
    private readonly List<MenuButtonDelegate> _buttonDelegates = [];
    // The buttons' labels with their '&' markers, by position.
    private readonly List<string> _labels = [];
    // CEF's set_accelerator has no Windows-key modifier, so those accelerators are matched here.
    private Dictionary<(int KeyCode, AcceleratorModifiers Modifiers), int> _commandAccelerators = [];
    private cef_window_t* _window;
    private cef_panel_t* _panel;
    private Menu? _menu;
    private bool _rebuildPosted;
    private bool _altAlone;

    internal WindowMenuBar(BaseWindow owner)
    {
        _owner = owner;
    }

    private cef_menu_button_pressed_lock_t* _pressedLock;
    // The button whose menu is open, painted as Electron's activated ink drop; -1 for none.
    private int _openButton = -1;
    private bool _underlined;

    /// <summary>Whether the bar is shown when there is a menu.</summary>
    internal bool Shown { get; private set; } = true;

    /// <summary>Whether the bar is on screen: a menu with at least one item, and <see cref="Shown"/>.</summary>
    internal bool IsVisible => _panel is not null && _menu is { Items.Count: > 0 } && Shown;

    /// <summary>Hides the bar until Alt is pressed.</summary>
    internal bool AutoHide { get; set; }

    /// <summary>Lays the window out as the bar above <paramref name="content"/>, which takes the rest.</summary>
    internal void Attach(cef_window_t* window, cef_view_t* content)
    {
        // The window delegate releases its argument after on_window_created, which frees this wrapper.
        NativeObject.AddRef(window);
        _window = window;
        // Stretched across: the default (start) sizes the content to its preferred width, a plain panel's to its children's.
        var vertical = new cef_box_layout_settings_t
        {
            size = (nuint)sizeof(cef_box_layout_settings_t),
            cross_axis_alignment = cef_axis_alignment_t.CEF_AXIS_ALIGNMENT_STRETCH,
        };
        var layout = window->@base.set_to_box_layout(&window->@base, &vertical);

        var horizontal = new cef_box_layout_settings_t { size = (nuint)sizeof(cef_box_layout_settings_t), horizontal = 1 };
        _panel = CefNative.cef_panel_create(null);
        NativeObject.Release(_panel->set_to_box_layout(_panel, &horizontal));
        NativeObject.AddRef(_panel);
        window->@base.add_child_view(&window->@base, (cef_view_t*)_panel);

        NativeObject.AddRef(content);
        window->@base.add_child_view(&window->@base, content);
        NativeObject.AddRef(content);
        layout->set_flex_for_view(layout, content, 1);
        NativeObject.Release(layout);
        UpdateVisibility();
    }

    internal void Detach()
    {
        if (_panel is not null) NativeObject.Release(_panel);
        if (_window is not null) NativeObject.Release(_window);
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
        _shownByAlt = false;
        UpdateVisibility();
    }

    /// <summary>The bar's height when visible, else 0.</summary>
    internal int Height => IsVisible ? _panel->@base.get_preferred_size(&_panel->@base).height : 0;

    /// <summary>A window accelerator fired; true if it was one of the menu's.</summary>
    internal bool OnAccelerator(int commandId)
    {
        // A disabled item leaves the key to whatever else wants it.
        if (_menu?.FindByCommandId(commandId) is not { Enabled: true } item) return false;
        item.ActivateByAccelerator(_owner);
        return true;
    }

    /// <summary>A key the page left unhandled: runs the item whose Windows-key accelerator it is, as Electron's focus
    /// manager does for <c>EF_COMMAND_DOWN</c> accelerators.</summary>
    internal bool OnUnhandledKey(cef_key_event_t* key)
        => key->type == cef_key_event_type_t.KEYEVENT_RAWKEYDOWN && CommandAcceleratorFor(key->windows_key_code, key->modifiers) is { } id && OnAccelerator(id);

    private int? CommandAcceleratorFor(int keyCode, uint cefModifiers)
        => _commandAccelerators.TryGetValue((keyCode, MenuModels.AcceleratorModifiersOf(cefModifiers)), out var id) ? id : null;

    /// <summary>A key on its way to the page, or one the bar's own buttons got: Alt alone toggles an auto-hidden bar
    /// (the key still reaches the page), Alt+letter opens the menu with that mnemonic (consumed).</summary>
    internal bool OnKeyEvent(cef_key_event_t* key)
    {
        if (_menu is null) return false;
        var isDown = key->type is cef_key_event_type_t.KEYEVENT_RAWKEYDOWN or cef_key_event_type_t.KEYEVENT_KEYDOWN;
        if (key->windows_key_code == AltKeyCode)
        {
            // Electron's RootView underlines the mnemonics while Alt is down.
            if (IsVisible) SetUnderlines(isDown);
            if (isDown && (key->modifiers & (uint)cef_event_flags_t.EVENTFLAG_IS_REPEAT) == 0) _altAlone = true;
            else if (key->type == cef_key_event_type_t.KEYEVENT_KEYUP && _altAlone && AutoHide)
            {
                _altAlone = false;
                SetShown(!Shown);
                _shownByAlt = Shown;
            }
            return false;
        }
        if (!isDown) return false;
        _altAlone = false;
        var alt = (key->modifiers & (uint)cef_event_flags_t.EVENTFLAG_ALT_DOWN) != 0;
        // Typing into the page puts an Alt-shown bar away again.
        if (!alt && AutoHide && _shownByAlt && !IsMenuOpen) SetShown(false);
        if (!alt || key->windows_key_code is < 'A' or > 'Z') return false;
        if (!_mnemonics.TryGetValue((char)key->windows_key_code, out var index)) return false;
        if (AutoHide && !Shown)
        {
            SetShown(true);
            _shownByAlt = true;
        }
        // Opening a menu runs a menu loop, which must not start inside CEF's key callback.
        CefThread.PostUi(() => TriggerMenu(index));
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
        _labels.Clear();
        _openButton = -1;
        _underlined = false;

        if (_menu is { } menu)
        {
            foreach (var item in menu.Items)
            {
                if (!item.Visible || item.Type == MenuItemType.Separator) continue;
                AddButton(item);
            }
            foreach (var item in menu.Descendants()) RegisterAccelerator(item);
            _commandAccelerators = MenuModels.CommandAccelerators(menu);
        }
        else _commandAccelerators = [];
        ApplyColors();
        UpdateVisibility();
    }

    private void AddButton(MenuItem item)
    {
        var commandId = item.CommandId;
        var index = (int)_panel->get_child_view_count(_panel);
        var buttonDelegate = new MenuButtonDelegate((button, point, pressedLock) => ShowMenu(commandId, index, button, pressedLock),
            // Views' theme pass resets every view's background, the bar's first; each button's repaints the bar's and theirs.
            ApplyColors);
        _buttonDelegates.Add(buttonDelegate);
        cef_string_utf16_t text = default;
        // The label keeps its '&': CEF hides it, and underlines the letter only while the button is focusable.
        CefString.Set(&text, item.Label);
        _labels.Add(item.Label);
        NativeObject.AddRef(buttonDelegate.Native);
        var button = CefNative.cef_menu_button_create(buttonDelegate.Native, &text);
        CefString.Set(&text, MenuLabels.WithoutMnemonics(item.Label));
        var asButton = (cef_button_t*)button;
        asButton->set_accessible_name(asButton, &text);
        CefString.Clear(&text);
        var view = (cef_view_t*)button;
        view->set_enabled(view, item.CommandEnabled ? 1 : 0);
        if (MnemonicOf(item.Label) is { } letter) _mnemonics.TryAdd(letter, index);
        // The panel takes the creation reference.
        _panel->add_child_view(_panel, view);
    }

    private void ShowMenu(int commandId, int index, cef_menu_button_t* button, cef_menu_button_pressed_lock_t* pressedLock)
    {
        // Electron's MenuBar hides the underlines once a button is pressed.
        SetUnderlines(false);
        if (_menu?.FindByCommandId(commandId) is not { Submenu: { } submenu })
        {
            // A top-level item without a submenu acts as a button.
            _menu?.FindByCommandId(commandId)?.Activate(_owner);
            return;
        }

        // Electron's MenuBar runs the dropdown on the item's submenu model, so the submenu raises the menu events.
        var run = new MenuRun(submenu);
        var modelDelegate = new MenuModelDelegate
        {
            CommandExecuted = Execute,
            WillShow = run.WillShow,
            Closed = closed => { if (run.Closed(closed)) OnMenuClosed(); },
        };
        NativeObject.AddRef(modelDelegate.Native);
        var model = CefNative.cef_menu_model_create(modelDelegate.Native);
        MenuModels.Fill(model, submenu);

        var view = (cef_view_t*)button;
        var bounds = view->get_bounds_in_screen(view);
        var origin = new cef_point_t { x = bounds.x, y = bounds.y + bounds.height };
        // The button stays pressed while the lock lives; the menu runs asynchronously, so keep it until it closes.
        ReleasePressedLock();
        NativeObject.AddRef(pressedLock);
        _pressedLock = pressedLock;
        IsMenuOpen = true;
        _openButton = index;
        ApplyColors();
        button->show_menu(button, model, &origin, cef_menu_anchor_position_t.CEF_MENU_ANCHOR_TOPLEFT);
    }

    /// <summary>Whether one of the bar's menus is open.</summary>
    internal bool IsMenuOpen { get; private set; }

    /// <summary>Closes an open bar menu.</summary>
    internal void CancelMenu()
    {
        if (_window is not null) _window->cancel_menu(_window);
    }

    // An auto-hidden bar that Alt brought up goes away again once its menu has been used, as in Electron.
    private bool _shownByAlt;

    /// <summary>The window lost focus: an auto-hidden bar that Alt brought up goes away, as in Electron.</summary>
    internal void OnWindowBlurred()
    {
        SetUnderlines(false);
        if (AutoHide && _shownByAlt && !IsMenuOpen) SetShown(false);
    }

    /// <summary>A click into the page: an Alt-shown bar goes away, as Electron's.</summary>
    internal void OnPageMouseDown() => OnWindowBlurred();

    private void OnMenuClosed()
    {
        ReleasePressedLock();
        SetUnderlines(false);
        if (!AutoHide || !_shownByAlt) return;
        _shownByAlt = false;
        SetShown(false);
    }

    private void ReleasePressedLock()
    {
        IsMenuOpen = false;
        if (_openButton >= 0)
        {
            _openButton = -1;
            ApplyColors();
        }
        if (_pressedLock is null) return;
        NativeObject.Release(_pressedLock);
        _pressedLock = null;
    }

    // Electron's Windows bar paints ui::kColorMenuBackground (menu_bar.cc); a CEF panel shows the window's colour through.
    private void ApplyColors()
    {
        if (_panel is null) return;
        var panel = (cef_view_t*)_panel;
        var background = panel->get_theme_color(panel, MenuBarColors.MenuBackgroundColorId);
        panel->set_background_color(panel, background);
        var count = (int)_panel->get_child_view_count(_panel);
        for (var i = 0; i < count; i++)
        {
            var child = _panel->get_child_view_at(_panel, i);
            child->set_background_color(child, i == _openButton ? MenuBarColors.Pressed(background) : background);
            NativeObject.Release(child);
        }
    }

    // CEF's menu button draws its mnemonic underline only while focusable (menu_button_impl.cc, SetFocusable).
    private void SetUnderlines(bool visible)
    {
        if (_panel is null || visible == _underlined) return;
        _underlined = visible;
        var count = (int)_panel->get_child_view_count(_panel);
        for (var i = 0; i < count; i++)
        {
            var child = _panel->get_child_view_at(_panel, i);
            child->set_focusable(child, visible ? 1 : 0);
            // CEF's label keeps its laid-out text until the text changes, so the new prefix flag shows only after a reset.
            var button = child->as_button(child);
            var label = button->as_label_button(button);
            cef_string_utf16_t text = default;
            label->set_text(label, &text);
            CefString.Set(&text, _labels[i]);
            label->set_text(label, &text);
            // Setting the text also renames the button, '&' and all.
            CefString.Set(&text, MenuLabels.WithoutMnemonics(_labels[i]));
            button->set_accessible_name(button, &text);
            CefString.Clear(&text);
            NativeObject.Release(label);
            NativeObject.Release(button);
            NativeObject.Release(child);
        }
    }

    /// <summary>The buttons' bounds in screen DIPs and border insets, and the theme's menu background; for tests.</summary>
    internal (List<cef_rect_t> Buttons, List<cef_insets_t> Insets, uint Background) Snapshot()
    {
        var buttons = new List<cef_rect_t>();
        var insets = new List<cef_insets_t>();
        if (_panel is null) return (buttons, insets, 0);
        var count = (int)_panel->get_child_view_count(_panel);
        for (var i = 0; i < count; i++)
        {
            var child = _panel->get_child_view_at(_panel, i);
            buttons.Add(child->get_bounds_in_screen(child));
            insets.Add(child->get_insets(child));
            NativeObject.Release(child);
        }
        var panel = (cef_view_t*)_panel;
        return (buttons, insets, panel->get_theme_color(panel, MenuBarColors.MenuBackgroundColorId));
    }

    private void RegisterAccelerator(MenuItem item)
    {
        if (item.Type is MenuItemType.Separator or MenuItemType.Submenu or MenuItemType.Palette || !item.RegisterAccelerator || MenuModels.CefAccelerator(item) is not { } a) return;
        // Low priority: the page sees the key first, so editing keys keep working in text fields.
        _window->set_accelerator(_window, item.CommandId, a.KeyCode,
            MenuModels.Has(a, AcceleratorModifiers.Shift), MenuModels.Has(a, AcceleratorModifiers.Control), MenuModels.Has(a, AcceleratorModifiers.Alt), 0);
        _accelerators.Add(item.CommandId);
    }


    private void Execute(int commandId, cef_event_flags_t flags) => _menu?.FindByCommandId(commandId)?.Activate(_owner, KeyboardEvent.FromCefFlags(flags));

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

/// <summary>The colours of Electron's Windows menu bar, computed as Chromium's <c>color_utils</c> does.</summary>
internal static class MenuBarColors
{
    /// <summary><c>CEF_ColorMenuBackground</c> in CEF 154's <c>cef_color_ids.h</c>: <c>ui::kColorMenuBackground</c>.</summary>
    internal const int MenuBackgroundColorId = 364;

    /// <summary>A pressed <c>SubmenuButton</c> over <paramref name="background"/> (ARGB): its flood-fill ink drop, based on
    /// <c>BlendTowardMaxContrast(background, 0x81)</c> (submenu_button.cc), at the ink drop's 0.175 visible opacity.</summary>
    internal static uint Pressed(uint background)
        => AlphaBlend(BlendTowardMaxContrast(background, 0x81 / 255f), background | 0xFF000000, 0.175f);

    internal static uint BlendTowardMaxContrast(uint color, float alpha)
        => (color & 0xFF000000) | (AlphaBlend(IsDark(color) ? 0xFFFFFFFF : 0xFF202124, color | 0xFF000000, alpha) & 0x00FFFFFF);

    // g_luminance_midpoint, where white and kGoogleGrey900 (0x202124) contrast equally.
    internal static bool IsDark(uint color) => Luminance(color) < 0.211692036f;

    private static float Luminance(uint color)
        => 0.2126f * Linearize((byte)(color >> 16)) + 0.7152f * Linearize((byte)(color >> 8)) + 0.0722f * Linearize((byte)color);

    private static float Linearize(byte channel)
    {
        var c = channel / 255f;
        return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    // Opaque colours only.
    private static uint AlphaBlend(uint foreground, uint background, float alpha)
    {
        uint Channel(int shift) => (uint)MathF.Round(((foreground >> shift) & 0xFF) * alpha + ((background >> shift) & 0xFF) * (1 - alpha)) << shift;
        return 0xFF000000 | Channel(16) | Channel(8) | Channel(0);
    }
}
