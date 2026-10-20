using System.Runtime.Versioning;
using Oriel.Cef;
using Oriel.Cef.Platform;

// Each AppKit call runs inside a lambda behind an OperatingSystem.IsMacOS() check the analyzer cannot follow.
#pragma warning disable CA1416

namespace Oriel;

/// <summary>A popup menu of the sharing services that can share a <see cref="SharingItem"/> (macOS). Electron's
/// <c>ShareMenu</c>.</summary>
public sealed class ShareMenu
{
    private readonly SharingItem _item;
    // Electron's popup_controllers_: this menu's open popup in each window, by window id. UI thread.
    private readonly Dictionary<int, MacSharePopup> _popups = [];

    /// <summary>A share menu for <paramref name="sharingItem"/>. Electron's <c>new ShareMenu(sharingItem)</c>.</summary>
    /// <exception cref="ArgumentException">A file path is empty or a URL is not valid.</exception>
    public ShareMenu(SharingItem sharingItem)
    {
        ArgumentNullException.ThrowIfNull(sharingItem);
        _item = sharingItem.Validated();
    }

    /// <summary>Shows the menu in a window: <see cref="PopupOptions.Window"/>, else the focused window, else the first
    /// open one; at <see cref="PopupOptions.X"/>, <see cref="PopupOptions.Y"/> or the pointer. Electron's
    /// <c>shareMenu.popup()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on macOS, where Electron has no <c>ShareMenu</c>.</exception>
    /// <exception cref="ArgumentException">Only one of <see cref="PopupOptions.X"/> and <see cref="PopupOptions.Y"/> is set.</exception>
    /// <exception cref="InvalidOperationException">No window is open.</exception>
    public void Popup(PopupOptions? options = null)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("ShareMenu is macOS only.");
        if (options is not null && options.X.HasValue != options.Y.HasValue)
            throw new ArgumentException("Set both X and Y, or neither.", nameof(options));
        var window = PopupWindow(options?.Window, BaseWindow.GetAllWindows(), BaseWindow.GetFocusedWindow())
            ?? throw new InvalidOperationException("Cannot open Menu without a BaseWindow present");
        var (x, y, closed) = (options?.X, options?.Y, options?.Closed);
        // Electron posts PopupOnUI, so popup() returns before the menu's loop starts.
        CefThread.PostUi(() => Show(window, x, y, closed));
    }

    /// <summary>Closes the menu in <paramref name="window"/>, or in every window when null. Electron's
    /// <c>shareMenu.closePopup()</c>.</summary>
    public void ClosePopup(BaseWindow? window = null)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var id = window?.Id;
        // An open popup holds the main thread in AppKit's menu loop, where a posted CEF task would wait for it to close.
        MacMenu.PerformOnMainThread(() => Close(id));
    }

    /// <summary>Electron's <c>Menu.prototype.popup</c> window choice: the given window if it is open, else the focused
    /// one, else the first.</summary>
    internal static T? PopupWindow<T>(T? requested, IReadOnlyList<T> all, T? focused) where T : class
        => requested is not null && all.Contains(requested) ? requested : focused ?? all.FirstOrDefault();

    [SupportedOSPlatform("macos")]
    private void Show(BaseWindow window, int? x, int? y, Action? closed)
    {
        var view = window.GetNativeWindowHandle();
        if (view == 0) return;
        var (texts, files, urls) = _item.ForMac();
        var menu = MacMenu.CreateShareMenu(texts, files, urls);
        MacMenu.SetDelegate(menu);
        MacSharePopup? popup = null;
        popup = new MacSharePopup(menu, () =>
        {
            if (_popups.TryGetValue(window.Id, out var current) && current == popup) _popups.Remove(window.Id);
            MacApplicationMenu.UntrackSharePopup(popup!);
            MacMenu.Release(menu);
            closed?.Invoke();
        }, CefThread.PostUi);
        // The controller a new popup replaces is released, and cancels as it goes.
        if (_popups.TryGetValue(window.Id, out var old)) old.Cancel(MacMenu.CancelTracking);
        _popups[window.Id] = popup;
        MacApplicationMenu.TrackSharePopup(popup);
        var sending = MacApplication.HandlingSendEvent;
        MacApplication.HandlingSendEvent = true;
        try { MacMenu.PopUp(menu, view, x, y); }
        finally { MacApplication.HandlingSendEvent = sending; }
    }

    [SupportedOSPlatform("macos")]
    private void Close(int? windowId)
    {
        if (windowId is { } id)
        {
            if (_popups.TryGetValue(id, out var popup)) popup.Cancel(MacMenu.CancelTracking);
            return;
        }
        foreach (var popup in _popups.Values.ToList()) popup.Cancel(MacMenu.CancelTracking);
    }
}

/// <summary>Electron's <c>ElectronMenuController</c> for a popup with a close callback: the callback is posted when the
/// top-level menu closes or <see cref="Cancel"/> ends it, once. Main thread.</summary>
internal sealed class MacSharePopup(nint nativeMenu, Action closed, Action<Action> post)
{
    internal nint NativeMenu { get; } = nativeMenu;

    internal bool IsMenuOpen { get; private set; }

    /// <summary><c>menuWillOpen:</c>.</summary>
    internal void WillOpen() => IsMenuOpen = true;

    /// <summary><c>menuDidClose:</c>: only the top-level menu ends a popup.</summary>
    internal void DidClose(nint menu)
    {
        if (!IsMenuOpen || menu != NativeMenu) return;
        IsMenuOpen = false;
        post(closed);
    }

    /// <summary>Electron's <c>cancel</c>.</summary>
    internal void Cancel(Action<nint> cancelTracking)
    {
        if (!IsMenuOpen) return;
        cancelTracking(NativeMenu);
        IsMenuOpen = false;
        post(closed);
    }
}
