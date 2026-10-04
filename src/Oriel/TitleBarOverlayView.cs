using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Minimize, maximize/restore and close drawn as CEF Views buttons in an overlay at the window's top right, over
/// the page of a window whose title bar is hidden. UI thread only.</summary>
internal sealed unsafe class TitleBarOverlayView
{
    private readonly BrowserWindow _owner;
    private readonly cef_label_button_t*[] _buttons = new cef_label_button_t*[3];
    private readonly ButtonDelegate[] _delegates = new ButtonDelegate[3];
    private readonly cef_button_state_t[] _states = new cef_button_state_t[3];
    private readonly bool[] _enabled = [true, true, true];
    private PanelDelegate? _panelDelegate;
    private cef_panel_t* _panel;
    private cef_overlay_controller_t* _controller;
    private uint _color;
    private uint _symbol;
    private int _height;
    private bool _maximized;

    internal TitleBarOverlayView(BrowserWindow owner) => _owner = owner;

    /// <summary>The overlay's height in DIPs.</summary>
    internal int Height => _height;

    /// <summary>The overlay's width in DIPs.</summary>
    internal int Width => TitleBar.ButtonWidth * _buttons.Length;

    internal void Attach(cef_window_t* window, uint color, uint symbol, int height)
    {
        _panelDelegate = new PanelDelegate(() => { if (_panel is not null) _panel->@base.set_background_color(&_panel->@base, _color); });
        NativeObject.AddRef(_panelDelegate.Native);
        _panel = CefNative.cef_panel_create(_panelDelegate.Native);
        var horizontal = new cef_box_layout_settings_t { size = (nuint)sizeof(cef_box_layout_settings_t), horizontal = 1 };
        NativeObject.Release(_panel->set_to_box_layout(_panel, &horizontal));
        for (var i = 0; i < _buttons.Length; i++)
        {
            var index = i;
            _delegates[i] = new ButtonDelegate(() => Press(index), state => { _states[index] = state; Paint(index); }, () => Paint(index));
            cef_string_utf16_t empty = default;
            NativeObject.AddRef(_delegates[i].Native);
            var button = CefNative.cef_label_button_create(_delegates[i].Native, &empty);
            button->set_horizontal_alignment(button, cef_horizontal_alignment_t.CEF_HORIZONTAL_ALIGNMENT_CENTER);
            button->@base.set_ink_drop_enabled(&button->@base, 0);
            var view = (cef_view_t*)button;
            view->set_focusable(view, 0);
            // One reference for this class, one the panel takes.
            NativeObject.AddRef(button);
            _buttons[i] = button;
            _panel->add_child_view(_panel, view);
        }
        NativeObject.AddRef(_panel);
        _controller = window->add_overlay_view(window, (cef_view_t*)_panel, cef_docking_mode_t.CEF_DOCKING_MODE_TOP_RIGHT, 0);
        // Colours set before the panel is in a widget do not take.
        Update(color, symbol, height);
        _controller->set_visible(_controller, 1);
    }

    /// <summary>New colours (ARGB) and height (DIPs).</summary>
    internal void Update(uint color, uint symbol, int height)
    {
        (_color, _symbol, _height) = (color, symbol, height);
        if (_controller is null) return;
        _panel->@base.set_background_color(&_panel->@base, color);
        for (var i = 0; i < _buttons.Length; i++)
        {
            var size = new cef_size_t { width = TitleBar.ButtonWidth, height = height };
            _buttons[i]->set_minimum_size(_buttons[i], &size);
            _buttons[i]->set_maximum_size(_buttons[i], &size);
            Paint(i);
        }
        // Docked overlays take their preferred size: the buttons' fixed size.
        _controller->size_to_preferred_size(_controller);
    }

    /// <summary>Enables the buttons the window allows; a disabled button greys its symbol and takes no clicks.</summary>
    internal void SetEnabled(bool minimizable, bool maximizable, bool closable)
    {
        bool[] enabled = [minimizable, maximizable, closable];
        for (var i = 0; i < _buttons.Length; i++)
        {
            if (_buttons[i] is null) continue;
            var view = (cef_view_t*)_buttons[i];
            view->set_enabled(view, enabled[i] ? 1 : 0);
            _enabled[i] = enabled[i];
            Paint(i);
        }
    }

    /// <summary>Whether a screen point in pixels is on the maximize button while it is enabled.</summary>
    internal bool IsOverMaximize(int x, int y)
    {
        if (_buttons[1] is null || !_enabled[1] || _controller is null || _controller->is_visible(_controller) == 0) return false;
        var view = (cef_view_t*)_buttons[1];
        var b = view->get_bounds_in_screen(view);
        var r = Screen.DipToScreenRect(_owner, new Rectangle(b.x, b.y, b.width, b.height));
        return x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height;
    }

    /// <summary>The pointer on the maximize button as Windows reports it once it answers <c>HTMAXBUTTON</c>; the
    /// button no longer sees the mouse itself, so its look and click follow from here.</summary>
    internal void OnMaximizeEvent(Oriel.Cef.Platform.MaximizeButtonEvent e)
    {
        var button = _buttons[1];
        if (button is null || !_enabled[1]) return;
        var state = e switch
        {
            Oriel.Cef.Platform.MaximizeButtonEvent.Hover => _states[1] == cef_button_state_t.CEF_BUTTON_STATE_PRESSED ? cef_button_state_t.CEF_BUTTON_STATE_PRESSED : cef_button_state_t.CEF_BUTTON_STATE_HOVERED,
            Oriel.Cef.Platform.MaximizeButtonEvent.Press => cef_button_state_t.CEF_BUTTON_STATE_PRESSED,
            _ => cef_button_state_t.CEF_BUTTON_STATE_NORMAL,
        };
        if (state != _states[1]) button->@base.set_state(&button->@base, state);
        if (e == Oriel.Cef.Platform.MaximizeButtonEvent.Click) Press(1);
    }

    /// <summary>Swaps maximize for restore while the window is maximized.</summary>
    internal void SetMaximized(bool maximized)
    {
        if (maximized == _maximized) return;
        _maximized = maximized;
        if (_buttons[1] is null) return;
        Paint(1);
        cef_string_utf16_t name = default;
        CefString.Set(&name, maximized ? "Restore" : "Maximize");
        _buttons[1]->@base.set_accessible_name(&_buttons[1]->@base, &name);
        CefString.Clear(&name);
    }

    /// <summary>Shows or hides the overlay; Electron hides it in full screen.</summary>
    internal void SetVisible(bool visible)
    {
        if (_controller is not null) _controller->set_visible(_controller, visible ? 1 : 0);
    }

    internal void Detach()
    {
        if (_controller is not null)
        {
            _controller->destroy(_controller);
            NativeObject.Release(_controller);
        }
        foreach (var button in _buttons)
            if (button is not null) NativeObject.Release(button);
        if (_panel is not null) NativeObject.Release(_panel);
        _controller = null;
        _panel = null;
        Array.Clear(_buttons);
    }

    private CaptionGlyph GlyphOf(int index) => index switch
    {
        0 => CaptionGlyph.Minimize,
        1 => _maximized ? CaptionGlyph.Restore : CaptionGlyph.Maximize,
        _ => CaptionGlyph.Close,
    };

    private void Paint(int index)
    {
        var button = _buttons[index];
        if (button is null) return;
        var close = index == 2;
        var (normal, hover, pressed) = TitleBar.ButtonBackgrounds(_color, _symbol, close);
        var view = (cef_view_t*)button;
        view->set_background_color(view, _states[index] switch
        {
            _ when !_enabled[index] => normal,
            cef_button_state_t.CEF_BUTTON_STATE_HOVERED => hover,
            cef_button_state_t.CEF_BUTTON_STATE_PRESSED => pressed,
            _ => normal,
        });
        // The close button's symbol turns white on red.
        var glyph = GlyphOf(index);
        var active = close ? 0xFFFFFFFF : _symbol;
        SetImage(button, cef_button_state_t.CEF_BUTTON_STATE_NORMAL, glyph, _symbol);
        SetImage(button, cef_button_state_t.CEF_BUTTON_STATE_HOVERED, glyph, active);
        SetImage(button, cef_button_state_t.CEF_BUTTON_STATE_PRESSED, glyph, active);
        SetImage(button, cef_button_state_t.CEF_BUTTON_STATE_DISABLED, glyph, TitleBar.Blend(_color, _symbol, TitleBar.DisabledSymbol));
    }

    // set_image takes the image's one reference.
    private static void SetImage(cef_label_button_t* button, cef_button_state_t state, CaptionGlyph glyph, uint argb)
        => button->set_image(button, state, ImageCodec.ToCefImage(CaptionGlyphs.Scales.Select(s => (s, CaptionGlyphs.Render(glyph, s, argb)))));

    private void Press(int index)
    {
        switch (index)
        {
            case 0: _owner.Minimize(); break;
            case 1: if (_maximized) _owner.Unmaximize(); else _owner.Maximize(); break;
            default: _owner.Close(); break;
        }
    }
}
