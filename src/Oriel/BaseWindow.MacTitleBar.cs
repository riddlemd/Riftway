using System.Globalization;
using Oriel.Cef.Interop;
using Oriel.Cef.Platform;

// Each AppKit call runs behind an OperatingSystem.IsMacOS() check the analyzer cannot follow.
#pragma warning disable CA1416

namespace Oriel;

/// <summary>Arguments for <see cref="BaseWindow.Swiped"/>.</summary>
public sealed class SwipeEventArgs(string direction) : EventArgs
{
    /// <summary><c>up</c>, <c>right</c>, <c>down</c> or <c>left</c>. Electron's <c>direction</c>.</summary>
    public string Direction { get; } = direction;
}

/// <summary>Arguments for <see cref="BaseWindow.RotateGesture"/>.</summary>
public sealed class RotateGestureEventArgs(float rotation) : EventArgs
{
    /// <summary>Degrees rotated since the last event, counter-clockwise positive. Electron's <c>rotation</c>.</summary>
    public float Rotation { get; } = rotation;
}

/// <summary>Electron v44's NativeWindowMac <c>roundedCorners</c> rule.</summary>
internal static class MacRoundedCorners
{
    /// <summary>A frameless window without rounded corners is made borderless, which squares them.</summary>
    internal static bool Borderless(bool roundedCorners, bool frameless) => !roundedCorners && frameless;
}

/// <summary>Electron v44's BaseWindow::PreviewFile argument rule.</summary>
internal static class MacQuickLook
{
    /// <summary>The panel's title: the display name, else the path.</summary>
    internal static string Title(string path, string? displayName) => displayName ?? path;
}

/// <summary>Electron v44's NativeWindowMac tabbing rule.</summary>
internal static class MacTabbing
{
    /// <summary>The identifier the window tabs by; null disallows tabbing, as for no identifier or a window without a native
    /// title bar.</summary>
    internal static string? Identifier(string? identifier, bool transparent, bool frameless)
        => string.IsNullOrEmpty(identifier) || transparent || frameless ? null : identifier;
}

/// <summary>Electron v44's NativeWindowMac rules for a transparent or frameless window's title bar.</summary>
internal static class MacTitleBar
{
    /// <summary>Electron's own margin for <c>hiddenInset</c>, from before Electron 12.</summary>
    internal static readonly Point HiddenInsetMargin = new(12, 11);

    /// <summary>Only a hidden title bar style keeps the traffic lights, through <c>WindowButtonsProxy</c>.</summary>
    internal static bool UsesButtonsProxy(TitleBarStyle style) => style != TitleBarStyle.Default;

    /// <summary>The proxy's first margin: <c>trafficLightPosition</c>, else <c>hiddenInset</c>'s, else AppKit's (null).</summary>
    internal static Point? Margin(TitleBarStyle style, Point? trafficLightPosition)
        => trafficLightPosition ?? (style == TitleBarStyle.HiddenInset ? HiddenInsetMargin : null);

    /// <summary>Whether the buttons are shown when the window is made: <c>customButtonsOnHover</c> waits for the pointer.</summary>
    internal static bool ButtonsShownAtCreation(TitleBarStyle style) => style is not (TitleBarStyle.Default or TitleBarStyle.CustomButtonsOnHover);

    /// <summary>Chromium's <c>base::i18n::IsRTL()</c>: the app locale's direction.</summary>
    internal static bool IsRightToLeft(string locale)
    {
        try { return CultureInfo.GetCultureInfo(locale).TextInfo.IsRightToLeft; }
        catch (CultureNotFoundException) { return false; }
    }
}

public unsafe partial class BaseWindow
{
    private nint _largerThanScreenNSWindow;
    // ElectronNSWindowDelegate's is_borderless_, kept across full screen.
    private bool _borderlessBeforeFullScreen;
    private double _sheetOffsetX;
    private double _sheetOffsetY;
    private MacWindowButtons? _macButtons;
    private bool? _windowButtonVisibility;
    private Point? _trafficLightPosition;

    // The transparent-or-frameless block of the NativeWindowMac constructor.
    private void ApplyMacHiddenTitleBar(nint view)
    {
        MacWindow.HideTitleBar(view);
        _trafficLightPosition = _options.TrafficLightPosition;
        var style = _options.TitleBarStyle;
        if (!MacTitleBar.UsesButtonsProxy(style))
        {
            MacWindow.SetWindowButtonsHidden(view, true);
            return;
        }
        _macButtons = MacWindowButtons.Create(view, MacTitleBar.IsRightToLeft(OrielApp.CurrentOrNull?.GetLocale() ?? ""));
        if (_macButtons is null) return;
        _macButtons.SetHeight(_options.TitleBarOverlay?.Height ?? 0);
        if (MacTitleBar.Margin(style, _trafficLightPosition) is { } margin) _macButtons.SetMargin((margin.X, margin.Y));
        if (style == TitleBarStyle.CustomButtonsOnHover) _macButtons.SetShowOnHover(true);
        else MacWindow.SetWindowButtonsHidden(view, !MacTitleBar.ButtonsShownAtCreation(style));
    }

    /// <summary>Shows or hides the close, minimise and zoom buttons (macOS). Electron's <c>win.setWindowButtonVisibility()</c>.</summary>
    public void SetWindowButtonVisibility(bool visible)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Command(w =>
        {
            _windowButtonVisibility = visible;
            if (_macButtons is not null)
            {
                if (visible) _macButtons.Redraw();
                _macButtons.SetVisible(visible);
            }
            // customButtonsOnHover leaves the buttons to the pointer.
            if (_options.TitleBarStyle != TitleBarStyle.CustomButtonsOnHover) MacWindow.SetWindowButtonsHidden((nint)w->get_window_handle(w), !visible);
        });
    }

    /// <summary>Whether any of the close, minimise and zoom buttons shows (macOS); false elsewhere. Electron's private
    /// <c>win._getWindowButtonVisibility()</c>, which its own tests use.</summary>
    public bool GetWindowButtonVisibility()
        => OperatingSystem.IsMacOS() && Query(w => MacWindow.AnyWindowButtonShown((nint)w->get_window_handle(w)), false);

    /// <summary>Moves the traffic lights of a hidden title bar to <paramref name="position"/>, from the window's top left;
    /// null puts them back where AppKit had them (macOS). Electron's <c>win.setWindowButtonPosition()</c>.</summary>
    public void SetWindowButtonPosition(Point? position)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Command(_ =>
        {
            _trafficLightPosition = position;
            _macButtons?.SetMargin(position is { } p ? (p.X, p.Y) : null);
        });
    }

    /// <summary>The position set by <see cref="SetWindowButtonPosition"/> or <see cref="BaseWindowOptions.TrafficLightPosition"/>;
    /// null for none (macOS). Electron's <c>win.getWindowButtonPosition()</c>.</summary>
    public Point? GetWindowButtonPosition() => OperatingSystem.IsMacOS() ? Query(_ => _trafficLightPosition, null) : null;

    /// <summary>Where the window's sheets (message boxes, file dialogs) attach: <paramref name="offsetY"/> down from the
    /// content's top, <paramref name="offsetX"/> from its left (macOS); stored elsewhere. Electron's <c>win.setSheetOffset()</c>.</summary>
    public void SetSheetOffset(double offsetY, double offsetX = 0) => Command(_ =>
    {
        _sheetOffsetX = offsetX;
        _sheetOffsetY = offsetY;
    });

    /// <summary>Sets the vibrant material behind the page, as <see cref="BaseWindowOptions.Vibrancy"/>; null or empty removes
    /// it, and an unknown type does nothing (macOS). A window made without vibrancy or transparency stays opaque, as in
    /// Electron. Electron's <c>win.setVibrancy()</c>.</summary>
    public void SetVibrancy(string? type, VibrancyOptions? options = null)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var duration = options?.AnimationDuration ?? 0;
        Command(w => SetVibrancyOn(w, type ?? "", duration));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void SetVibrancyOn(cef_window_t* w, string type, int durationMs)
    {
        var view = (nint)w->get_window_handle(w);
        if (type.Length == 0) MacWindow.SetVibrancy(view, null, 0, durationMs);
        else if (MacVibrancy.Material(type) is { } material)
        {
            MacWindow.SetVibrancy(view, material, MacVibrancy.State(_options.VisualEffectState), durationMs);
            UpdateVibrancyRadii(w, w->is_fullscreen(w) != 0);
        }
    }

    // NativeWindowMac::UpdateVibrancyRadii.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void UpdateVibrancyRadii(cef_window_t* w, bool fullScreen)
    {
        var view = (nint)w->get_window_handle(w);
        if (!MacWindow.HasVibrancy(view)) return;
        if (MacVibrancy.CornerRadius(MacWindow.IsTitled(view), _options.Modal, !IsFrameless, fullScreen) is { } radius) MacWindow.SetVibrancyMask(view, radius);
    }

    /// <summary>Electron's <c>win.setAutoHideCursor()</c> (macOS): as <see cref="BaseWindowOptions.DisableAutoHideCursor"/>,
    /// nothing reads the setting, in Electron v44 as here.</summary>
    public void SetAutoHideCursor(bool autoHide) => _ = autoHide;

    /// <summary>Leaves the window out of the Window menu's list of windows (macOS). Electron's
    /// <c>win.excludedFromShownWindowsMenu</c>.</summary>
    public void SetExcludedFromShownWindowsMenu(bool excluded)
    {
        if (OperatingSystem.IsMacOS()) Command(w => MacWindow.SetExcludedFromWindowsMenu((nint)w->get_window_handle(w), excluded));
    }

    /// <summary>Electron's <c>win.excludedFromShownWindowsMenu</c>; false off macOS.</summary>
    public bool IsExcludedFromShownWindowsMenu()
        => OperatingSystem.IsMacOS() && Query(w => MacWindow.IsExcludedFromWindowsMenu((nint)w->get_window_handle(w)), false);

    private string _accessibleTitle = "";

    /// <summary>The title screen readers announce in place of the window's title; empty for the window's title. On macOS
    /// VoiceOver reads it; on Windows and Linux it is stored only (drift W58). Electron's <c>win.accessibleTitle</c>.</summary>
    public string AccessibleTitle
    {
        get => Query(_ => _accessibleTitle, "");
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Command(w =>
            {
                _accessibleTitle = value;
                if (OperatingSystem.IsMacOS()) MacWindow.SetAccessibleTitle((nint)w->get_window_handle(w), value);
            });
        }
    }

    /// <summary>macOS: a three-finger swipe on the window. Electron's <c>swipe</c>.</summary>
    public event EventHandler<SwipeEventArgs>? Swiped;

    /// <summary>macOS: a trackpad rotation on the window. Electron's <c>rotate-gesture</c>.</summary>
    public event EventHandler<RotateGestureEventArgs>? RotateGesture;

    /// <summary>macOS: a sheet is about to attach to the window. Electron's <c>sheet-begin</c>.</summary>
    public event EventHandler? SheetBegan;

    /// <summary>macOS: the window's sheet ended. Electron's <c>sheet-end</c>.</summary>
    public event EventHandler? SheetEnded;

    private nint _gesturesNSWindow;

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void ObserveMacGestures(nint view)
        => _gesturesNSWindow = MacWindow.ObserveGestures(view, rotation => RotateGesture?.Invoke(this, new RotateGestureEventArgs(rotation)),
            direction => Swiped?.Invoke(this, new SwipeEventArgs(direction)));

    /// <summary>macOS: the user clicked the + button of the window's tab bar. Electron's <c>new-window-for-tab</c>; the app's
    /// <see cref="OrielApp.NewWindowForTab"/> follows.</summary>
    public event EventHandler? NewWindowForTab;

    private void OnMacNewWindowForTab()
    {
        NewWindowForTab?.Invoke(this, EventArgs.Empty);
        OrielApp.CurrentOrNull?.RaiseNewWindowForTab();
    }

    /// <summary>The identifier the window tabs by; null when it never tabs, and off macOS. Electron's <c>win.tabbingIdentifier</c>.</summary>
    public string? GetTabbingIdentifier()
        => OperatingSystem.IsMacOS() ? Query(w => MacWindow.GetTabbingIdentifier((nint)w->get_window_handle(w)), null) : null;

    /// <summary>Selects the previous tab of the window's group (macOS). Electron's <c>win.selectPreviousTab()</c>.</summary>
    public void SelectPreviousTab() => MacTabAction("selectPreviousTab:");

    /// <summary>Selects the next tab (macOS). Electron's <c>win.selectNextTab()</c>.</summary>
    public void SelectNextTab() => MacTabAction("selectNextTab:");

    /// <summary>Shows or hides the tab overview (macOS). Electron's <c>win.showAllTabs()</c>.</summary>
    public void ShowAllTabs() => MacTabAction("toggleTabOverview:");

    /// <summary>Merges every window of the group into tabs of this one (macOS). Electron's <c>win.mergeAllWindows()</c>.</summary>
    public void MergeAllWindows() => MacTabAction("mergeAllWindows:");

    /// <summary>Moves this tab into a window of its own (macOS). Electron's <c>win.moveTabToNewWindow()</c>.</summary>
    public void MoveTabToNewWindow() => MacTabAction("moveTabToNewWindow:");

    /// <summary>Shows or hides the tab bar (macOS). Electron's <c>win.toggleTabBar()</c>.</summary>
    public void ToggleTabBar() => MacTabAction("toggleTabBar:");

    /// <summary>Adds <paramref name="window"/> as a tab after this window's (macOS). Electron's <c>win.addTabbedWindow()</c>.</summary>
    /// <exception cref="ArgumentException">The window is this one, with Electron's message.</exception>
    public void AddTabbedWindow(BaseWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsMacOS()) return;
        var other = window.GetNativeWindowHandle();
        var same = Query(w => MacWindow.SameWindow((nint)w->get_window_handle(w), other), false);
        if (same) throw new ArgumentException("AddTabbedWindow cannot be called by a window on itself.", nameof(window));
        Command(w => MacWindow.AddTabbedWindow((nint)w->get_window_handle(w), other));
    }

    private void MacTabAction(string selector)
    {
        if (OperatingSystem.IsMacOS()) Command(w => MacWindow.TabAction((nint)w->get_window_handle(w), selector));
    }

    /// <summary>Shows <paramref name="path"/> in QuickLook, titled <paramref name="displayName"/> (the path when null)
    /// (macOS). Electron's <c>win.previewFile()</c>.</summary>
    public void PreviewFile(string path, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsMacOS()) Command(w => MacWindow.PreviewFile((nint)w->get_window_handle(w), path, MacQuickLook.Title(path, displayName)));
    }

    /// <summary>Closes the QuickLook panel <see cref="PreviewFile"/> opened (macOS). Electron's <c>win.closeFilePreview()</c>.</summary>
    public void CloseFilePreview()
    {
        if (OperatingSystem.IsMacOS()) Command(w => MacWindow.CloseFilePreview((nint)w->get_window_handle(w)));
    }

    // Electron redraws the proxy wherever AppKit may have laid the title bar out again.
    private void RedrawMacButtons()
    {
        if (OperatingSystem.IsMacOS()) _macButtons?.Redraw();
    }

    // ElectronNSWindowDelegate's and NativeWindowMac's traffic light handling.
    private void OnMacTitleBarEvent(MacWindowEvent e, cef_window_t* w, nint view)
    {
        var hidesTitleBar = _macTransparent || IsFrameless;
        switch (e)
        {
            case MacWindowEvent.DidResize or MacWindowEvent.DidBecomeMain or MacWindowEvent.DidResignMain
                or MacWindowEvent.DidBecomeKey or MacWindowEvent.DidResignKey:
                if (w->is_fullscreen(w) == 0) _macButtons?.Redraw();
                break;
            case MacWindowEvent.WillMiniaturize:
                // AppKit puts the buttons back at its own place while the window comes back.
                _macButtons?.SetVisible(false);
                break;
            case MacWindowEvent.DidDeminiaturize:
                RestoreMacButtons();
                break;
            case MacWindowEvent.WillBeginSheet:
                SheetBegan?.Invoke(this, EventArgs.Empty);
                break;
            case MacWindowEvent.DidEndSheet:
                SheetEnded?.Invoke(this, EventArgs.Empty);
                break;
            case MacWindowEvent.WillEnterFullScreen:
                // Full screen needs a titled window.
                _borderlessBeforeFullScreen = !MacWindow.IsTitled(view);
                MacWindow.SetBorderless(view, false);
                UpdateVibrancyRadii(w, true);
                break;
            case MacWindowEvent.DidEnterFullScreen:
                if (_macButtons is not null) MacWindow.SetTitleVisible(view, true);
                if (hidesTitleBar) MacWindow.SetTitlebarAppearsTransparent(view, false);
                break;
            case MacWindowEvent.WillExitFullScreen:
                UpdateVibrancyRadii(w, false);
                if (_macButtons is not null)
                {
                    MacWindow.SetTitleVisible(view, false);
                    // Hidden, or the buttons jump.
                    _macButtons.SetVisible(false);
                }
                break;
            case MacWindowEvent.DidExitFullScreen:
                MacWindow.SetBorderless(view, _borderlessBeforeFullScreen);
                RestoreMacButtons();
                if (hidesTitleBar) MacWindow.SetTitlebarAppearsTransparent(view, true);
                break;
        }
    }

    private void RestoreMacButtons()
    {
        if (_macButtons is null || !(_windowButtonVisibility ?? true)) return;
        _macButtons.Redraw();
        _macButtons.SetVisible(true);
    }

    private void DisposeMacButtons()
    {
        _macButtons?.Dispose();
        _macButtons = null;
    }
}
