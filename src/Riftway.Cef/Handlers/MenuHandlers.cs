using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Riftway.Cef.Interop;

namespace Riftway.Cef.Handlers;

/// <summary>A menu button was pressed: show its menu at <paramref name="screenPoint"/> while holding
/// <paramref name="pressedLock"/> (AddRef it to keep the button pressed until the menu closes).</summary>
public unsafe delegate void MenuButtonPressedCallback(cef_menu_button_t* button, cef_point_t screenPoint, cef_menu_button_pressed_lock_t* pressedLock);

/// <summary>A Views menu model event: the model (the top menu or a submenu), valid only during the call.</summary>
public unsafe delegate void MenuModelCallback(cef_menu_model_t* model);

/// <summary><c>cef_menu_model_delegate_t</c>: commands chosen from a Views menu, and its menus opening and closing.</summary>
public sealed unsafe class MenuModelDelegate
{
    public MenuModelDelegate()
    {
        Native = NativeObject.Create<cef_menu_model_delegate_t>(this);
        Native->execute_command = &ExecuteCommand;
        Native->menu_will_show = &MenuWillShow;
        Native->menu_closed = &MenuClosed;
    }

    public cef_menu_model_delegate_t* Native { get; }

    /// <summary>An item was chosen: its command id.</summary>
    public Action<int, cef_event_flags_t>? CommandExecuted { get; init; }

    /// <summary>A menu or submenu is about to show: CEF passes each submenu its own model.</summary>
    public MenuModelCallback? WillShow { get; init; }

    /// <summary>A menu or submenu closed, whether or not an item was chosen. The top menu reports it as the run ends;
    /// CEF posts a submenu's, so it arrives after the top menu's when both close together.</summary>
    public MenuModelCallback? Closed { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ExecuteCommand(cef_menu_model_delegate_t* self, cef_menu_model_t* menuModel, int commandId, cef_event_flags_t eventFlags)
    {
        var owner = NativeObject.Owner<MenuModelDelegate>(self);
        if (owner.CommandExecuted is { } callback) Guard.Run(() => callback(commandId, eventFlags));
        NativeObject.Release(menuModel);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MenuWillShow(cef_menu_model_delegate_t* self, cef_menu_model_t* menuModel)
    {
        var owner = NativeObject.Owner<MenuModelDelegate>(self);
        if (owner.WillShow is { } callback) Guard.Run(() => callback(menuModel));
        NativeObject.Release(menuModel);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MenuClosed(cef_menu_model_delegate_t* self, cef_menu_model_t* menuModel)
    {
        var owner = NativeObject.Owner<MenuModelDelegate>(self);
        if (owner.Closed is { } callback) Guard.Run(() => callback(menuModel));
        NativeObject.Release(menuModel);
    }
}

/// <summary><c>cef_button_delegate_t</c>: a plain Views button's presses and state changes (hover, press).</summary>
public sealed unsafe class ButtonDelegate
{
    public ButtonDelegate(Action pressed, Action<cef_button_state_t> stateChanged, Action? themeChanged = null)
    {
        Pressed = pressed;
        StateChanged = stateChanged;
        ThemeChanged = themeChanged;
        Native = NativeObject.Create<cef_button_delegate_t>(this);
        Native->on_button_pressed = &OnButtonPressed;
        Native->on_button_state_changed = &OnButtonStateChanged;
        if (themeChanged is not null) Native->@base.on_theme_changed = &OnThemeChanged;
    }

    /// <summary>The theme reset the view's colours, as it does when the view first joins a window.</summary>
    public Action? ThemeChanged { get; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnThemeChanged(cef_view_delegate_t* self, cef_view_t* view)
    {
        var owner = NativeObject.Owner<ButtonDelegate>(self);
        Guard.Run(() => owner.ThemeChanged!());
        NativeObject.Release(view);
    }

    public cef_button_delegate_t* Native { get; }

    public Action Pressed { get; }

    public Action<cef_button_state_t> StateChanged { get; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnButtonPressed(cef_button_delegate_t* self, cef_button_t* button)
    {
        var owner = NativeObject.Owner<ButtonDelegate>(self);
        Guard.Run(owner.Pressed);
        NativeObject.Release(button);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnButtonStateChanged(cef_button_delegate_t* self, cef_button_t* button)
    {
        var owner = NativeObject.Owner<ButtonDelegate>(self);
        var state = button->get_state(button);
        Guard.Run(() => owner.StateChanged(state));
        NativeObject.Release(button);
    }
}

/// <summary><c>cef_menu_button_delegate_t</c>: a Views button that opens a menu.</summary>
public sealed unsafe class MenuButtonDelegate
{
    public MenuButtonDelegate(MenuButtonPressedCallback pressed, Action? themeChanged = null)
    {
        Pressed = pressed;
        ThemeChanged = themeChanged;
        Native = NativeObject.Create<cef_menu_button_delegate_t>(this);
        Native->on_menu_button_pressed = &OnMenuButtonPressed;
        if (themeChanged is not null) Native->@base.@base.on_theme_changed = &OnThemeChanged;
    }

    /// <summary>The theme reset the button's colours, as it does when the button first joins a window.</summary>
    public Action? ThemeChanged { get; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnThemeChanged(cef_view_delegate_t* self, cef_view_t* view)
    {
        var owner = NativeObject.Owner<MenuButtonDelegate>(self);
        Guard.Run(() => owner.ThemeChanged!());
        NativeObject.Release(view);
    }

    public cef_menu_button_delegate_t* Native { get; }

    public MenuButtonPressedCallback Pressed { get; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMenuButtonPressed(cef_menu_button_delegate_t* self, cef_menu_button_t* button, cef_point_t* screenPoint, cef_menu_button_pressed_lock_t* buttonPressedLock)
    {
        var owner = NativeObject.Owner<MenuButtonDelegate>(self);
        var point = *screenPoint;
        Guard.Run(() => owner.Pressed(button, point, buttonPressedLock));
        NativeObject.Release(buttonPressedLock);
        NativeObject.Release(button);
    }
}

/// <summary>The page asked for a context menu: its frame and parameters, valid only during the call.</summary>
public unsafe delegate void ContextMenuCallback(cef_frame_t* frame, cef_context_menu_params_t* menuParams);

/// <summary><c>cef_context_menu_handler_t</c>: reports the page's context menu requests and suppresses CEF's own
/// menu, so the application decides what to show.</summary>
public sealed unsafe class ContextMenuHandler
{
    public ContextMenuHandler(ContextMenuCallback requested)
    {
        Requested = requested;
        Native = NativeObject.Create<cef_context_menu_handler_t>(this);
        Native->on_before_context_menu = &OnBeforeContextMenu;
        Native->run_context_menu = &RunContextMenu;
    }

    public cef_context_menu_handler_t* Native { get; }

    public ContextMenuCallback Requested { get; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBeforeContextMenu(cef_context_menu_handler_t* self, cef_browser_t* browser, cef_frame_t* frame, cef_context_menu_params_t* menuParams, cef_menu_model_t* model)
    {
        var owner = NativeObject.Owner<ContextMenuHandler>(self);
        Guard.Run(() => owner.Requested(frame, menuParams));
        NativeObject.Release(model);
        NativeObject.Release(menuParams);
        NativeObject.Release(frame);
        NativeObject.Release(browser);
    }

    // Claiming the menu and cancelling it shows nothing, which is Electron's default: no context menu unless the
    // app shows one. Emptying the model in on_before_context_menu instead leaves a dangling object on macOS.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int RunContextMenu(cef_context_menu_handler_t* self, cef_browser_t* browser, cef_frame_t* frame, cef_context_menu_params_t* menuParams,
        cef_menu_model_t* model, cef_run_context_menu_callback_t* callback)
    {
        callback->cancel(callback);
        NativeObject.Release(callback);
        NativeObject.Release(model);
        NativeObject.Release(menuParams);
        NativeObject.Release(frame);
        NativeObject.Release(browser);
        return 1;
    }
}

/// <summary><c>cef_keyboard_handler_t</c>: the page's keys before the renderer sees them.</summary>
public sealed unsafe class KeyboardHandler
{
    public KeyboardHandler(KeyEventPredicate preKeyEvent, KeyEventPredicate? unhandledKeyEvent = null)
    {
        PreKeyEvent = preKeyEvent;
        UnhandledKeyEvent = unhandledKeyEvent;
        Native = NativeObject.Create<cef_keyboard_handler_t>(this);
        Native->on_pre_key_event = &OnPreKeyEvent;
        Native->on_key_event = &OnKeyEvent;
    }

    public cef_keyboard_handler_t* Native { get; }

    /// <summary>A key about to reach the page; true consumes it.</summary>
    public KeyEventPredicate PreKeyEvent { get; }

    /// <summary>A key the page left unhandled, before Chrome passes it on (on macOS, to the app menu); true consumes it.</summary>
    public KeyEventPredicate? UnhandledKeyEvent { get; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnKeyEvent(cef_keyboard_handler_t* self, cef_browser_t* browser, cef_key_event_t* keyEvent, void* osEvent)
    {
        var owner = NativeObject.Owner<KeyboardHandler>(self);
        var consumed = owner.UnhandledKeyEvent is { } unhandled && Guard.Run(() => unhandled(keyEvent), false);
        NativeObject.Release(browser);
        return consumed ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPreKeyEvent(cef_keyboard_handler_t* self, cef_browser_t* browser, cef_key_event_t* keyEvent, void* osEvent, int* isKeyboardShortcut)
    {
        var owner = NativeObject.Owner<KeyboardHandler>(self);
        var consumed = Guard.Run(() => owner.PreKeyEvent(keyEvent), false);
        NativeObject.Release(browser);
        return consumed ? 1 : 0;
    }
}
