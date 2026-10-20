using System.Runtime.Versioning;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>What draws a window's Touch Bar: Electron's <c>NativeWindow</c> touch bar methods, which do nothing off macOS.</summary>
internal interface ITouchBarNative
{
    void SetItems(IReadOnlyList<TouchBarItem> items);
    void RefreshItem(string id);
    void SetEscapeItem(TouchBarItem? item);
}

public unsafe partial class BaseWindow : ITouchBarHost
{
    private TouchBar? _touchBar;
    private Action<string, TouchBarInteraction>? _touchBarInteraction;
    private Action? _touchBarHostClosed;

    /// <summary>Shows <paramref name="touchBar"/> on the MacBook Touch Bar while this window is focused, replacing the
    /// window's bar; null removes it. Does nothing to the screen off macOS. Electron's <c>win.setTouchBar()</c>.</summary>
    public void SetTouchBar(TouchBar? touchBar) => TouchBar.SetOnWindow(touchBar, this);

    /// <summary>Shows a new bar of <paramref name="items"/>. Electron's <c>win.setTouchBar()</c> with an array.</summary>
    public void SetTouchBar(IEnumerable<TouchBarItem> items) => SetTouchBar(new TouchBar(items));

    /// <summary>The native Touch Bar, where there is one.</summary>
    internal ITouchBarNative? TouchBarNative { get; set; }

    // NativeWindowMac's touch_bar_ and ElectronNSWindow's makeTouchBar.
    [SupportedOSPlatform("macos")]
    private void AttachMacTouchBar(nint contentView)
    {
        MacTouchBarWindow? bar = null;
        _touchBarNSWindow = MacWindow.RouteMakeTouchBar(contentView, () => bar?.MakeTouchBar() ?? 0);
        if (_touchBarNSWindow == 0) return;
        var window = _touchBarNSWindow;
        bar = new MacTouchBarWindow(controller => new MacTouchBarUi(controller), () => MacWindow.ResetTouchBar(window), () => MacWindow.TouchBar(window),
            OnTouchBarInteraction, action =>
            {
                var pool = MacTouchBar.PushPool();
                try { action(); }
                finally { MacTouchBar.PopPool(pool); }
            });
        TouchBarNative = bar;
    }

    /// <summary>Reports a use of an item from the native bar: Electron's <c>-touch-bar-interaction</c>.</summary>
    internal void OnTouchBarInteraction(string itemId, TouchBarInteraction details) => _touchBarInteraction?.Invoke(itemId, details);

    TouchBar? ITouchBarHost.AttachedTouchBar
    {
        get => _touchBar;
        set => _touchBar = value;
    }

    // Items may change on any thread; AppKit is reached on the UI thread.
    void ITouchBarHost.SetTouchBarItems(IReadOnlyList<TouchBarItem> items) => OnTouchBarThread(native => native.SetItems(items));

    void ITouchBarHost.RefreshTouchBarItem(string id) => OnTouchBarThread(native => native.RefreshItem(id));

    void ITouchBarHost.SetEscapeTouchBarItem(TouchBarItem? item) => OnTouchBarThread(native => native.SetEscapeItem(item));

    private void OnTouchBarThread(Action<ITouchBarNative> call)
    {
        if (TouchBarNative is not { } native) return;
        Oriel.Cef.CefThread.RunOnUi(() => call(native));
    }

    event Action<string, TouchBarInteraction>? ITouchBarHost.TouchBarInteraction
    {
        add => _touchBarInteraction += value;
        remove => _touchBarInteraction -= value;
    }

    event Action? ITouchBarHost.HostClosed
    {
        add => _touchBarHostClosed += value;
        remove => _touchBarHostClosed -= value;
    }
}
