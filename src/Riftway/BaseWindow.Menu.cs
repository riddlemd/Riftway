using Riftway.Cef;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;
using Riftway.Cef.Platform;

namespace Riftway;

public unsafe partial class BaseWindow
{
    /// <summary>Sets the window's menu bar, replacing the application menu for this window; null removes it
    /// (Windows, Linux). Electron's <c>win.setMenu()</c>.</summary>
    /// <remarks>On macOS the menu bar belongs to the application: use <see cref="Menu.SetApplicationMenu"/>.</remarks>
    public void SetMenu(Menu? menu) => CefThread.RunOnUi(() =>
    {
        _hasOwnMenu = true;
        _ownMenu = menu;
        if (_window is not null) _menuBar?.SetMenu(menu);
    });

    /// <summary>Removes the window's menu bar (Windows, Linux). Electron's <c>win.removeMenu()</c>.</summary>
    public void RemoveMenu() => SetMenu(null);

    /// <summary>Whether the menu bar stays hidden until Alt is pressed (Windows, Linux). Setting it shows or
    /// hides the bar to match. Electron's <c>win.autoHideMenuBar</c>.</summary>
    public bool AutoHideMenuBar
    {
        get => Query(_ => _menuBar?.AutoHide ?? false, false);
        set => Command(_ =>
        {
            if (_menuBar is null) return;
            _menuBar.AutoHide = value;
            _menuBar.SetShown(!value);
        });
    }

    /// <summary>Shows or hides the menu bar; with <see cref="AutoHideMenuBar"/>, Alt still toggles it (Windows,
    /// Linux). Electron's <c>win.setMenuBarVisibility()</c>.</summary>
    public void SetMenuBarVisibility(bool visible) => Command(_ => _menuBar?.SetShown(visible));

    /// <summary>Whether a menu bar is showing; always false on macOS. Electron's <c>win.isMenuBarVisible()</c>.</summary>
    public bool IsMenuBarVisible => Query(_ => _menuBar?.IsVisible ?? false, false);

    // The popup menu showing in this window, if any; UI thread.
    private MenuRun? _popupRun;
    private readonly PopupCancelGate _popupCancels = new();

    /// <summary>Shows <paramref name="menu"/> at a point in the page, by default at the pointer.</summary>
    internal void ShowPopup(Menu menu, int? x, int? y, Action? closed)
    {
        var request = _popupCancels.Request();
        Command(w => ShowPopupOn(w, menu, x, y, closed, request));
    }

    private void ShowPopupOn(cef_window_t* w, Menu menu, int? x, int? y, Action? closed, long request)
    {
        // Closed before it opened: Electron's popup is open by the time closePopup can run, so it reports closing.
        if (_popupCancels.IsCancelled(request))
        {
            closed?.Invoke();
            return;
        }
        var content = ContentBoundsOn(w);
        var at = x is { } ax && y is { } ay ? new Point(content.X + ax, content.Y + ay) : Screen.GetCursorScreenPoint();
        var screen = new cef_point_t { x = at.X, y = at.Y };
        // MenuViews::PopupAt replaces the window's MenuRunner, which releases a popup still showing.
        _popupRun?.Release();
        var run = _popupRun = new MenuRun(menu);
        var popupDelegate = new MenuModelDelegate
        {
            CommandExecuted = (id, flags) => menu.FindByCommandId(id)?.Activate(this, KeyboardEvent.FromCefFlags(flags)),
            WillShow = run.WillShow,
            // CEF reports each submenu closing too; the popup callback is for the whole menu.
            Closed = model =>
            {
                if (!run.Closed(model)) return;
                if (_popupRun == run) _popupRun = null;
                closed?.Invoke();
            },
        };
        NativeObject.AddRef(popupDelegate.Native);
        var model = CefNative.cef_menu_model_create(popupDelegate.Native);
        MenuModels.Fill(model, menu);
        // The model reference passes to CEF, which keeps it, and through it the delegate, until the menu closes.
        w->show_menu(w, model, &screen, cef_menu_anchor_position_t.CEF_MENU_ANCHOR_TOPLEFT);
    }

    /// <summary>Closes a menu shown by <see cref="ShowPopup"/>.</summary>
    internal void ClosePopup()
    {
        _popupCancels.CancelAll();
        // An open popup holds macOS in AppKit's menu loop, where a posted CEF task would wait for the menu to close.
        if (OperatingSystem.IsMacOS()) MacMenu.PerformOnMainThread(() => { if (_window is not null) _window->cancel_menu(_window); });
        else Command(w => w->cancel_menu(w));
    }

    /// <summary>Opens the menu bar's menu at <paramref name="index"/> as a click would; false if there is none.</summary>
    internal bool TriggerMenuBarItem(int index) => Query(_ => _menuBar?.TriggerMenu(index) ?? false, false);

    /// <summary>Whether a menu bar menu is open.</summary>
    internal bool IsMenuBarMenuOpen => Query(_ => _menuBar?.IsMenuOpen ?? false, false);

    /// <summary>The menu bar's buttons (screen DIPs, border insets) and its theme background; for tests.</summary>
    internal (List<cef_rect_t> Buttons, List<cef_insets_t> Insets, uint Background) MenuBarSnapshot()
        => Query(_ => _menuBar?.Snapshot() ?? ([], [], 0u), ([], [], 0u));

    /// <summary>Closes an open menu bar menu.</summary>
    internal void CancelMenuBarMenu() => Command(_ => _menuBar?.CancelMenu());
}
