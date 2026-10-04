using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef.Handlers;

/// <summary>A menu button was pressed: show its menu at <paramref name="screenPoint"/> while holding
/// <paramref name="pressedLock"/> (AddRef it to keep the button pressed until the menu closes).</summary>
public unsafe delegate void MenuButtonPressedCallback(cef_menu_button_t* button, cef_point_t screenPoint, cef_menu_button_pressed_lock_t* pressedLock);

/// <summary><c>cef_menu_model_delegate_t</c>: commands chosen from a Views menu.</summary>
public sealed unsafe class MenuModelDelegate
{
    public MenuModelDelegate()
    {
        Native = NativeObject.Create<cef_menu_model_delegate_t>(this);
        Native->execute_command = &ExecuteCommand;
        Native->menu_closed = &MenuClosed;
    }

    public cef_menu_model_delegate_t* Native { get; }

    /// <summary>An item was chosen: its command id.</summary>
    public Action<int>? CommandExecuted { get; init; }

    /// <summary>The menu closed, whether or not an item was chosen.</summary>
    public Action? Closed { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ExecuteCommand(cef_menu_model_delegate_t* self, cef_menu_model_t* menuModel, int commandId, cef_event_flags_t eventFlags)
    {
        var owner = NativeObject.Owner<MenuModelDelegate>(self);
        if (owner.CommandExecuted is { } callback) Guard.Run(() => callback(commandId));
        NativeObject.Release(menuModel);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MenuClosed(cef_menu_model_delegate_t* self, cef_menu_model_t* menuModel)
    {
        var owner = NativeObject.Owner<MenuModelDelegate>(self);
        if (owner.Closed is { } callback) Guard.Run(callback);
        NativeObject.Release(menuModel);
    }
}

/// <summary><c>cef_menu_button_delegate_t</c>: a Views button that opens a menu.</summary>
public sealed unsafe class MenuButtonDelegate
{
    public MenuButtonDelegate(MenuButtonPressedCallback pressed)
    {
        Pressed = pressed;
        Native = NativeObject.Create<cef_menu_button_delegate_t>(this);
        Native->on_menu_button_pressed = &OnMenuButtonPressed;
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
