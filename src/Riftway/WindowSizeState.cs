namespace Riftway;

/// <summary>A window event Electron raises from <c>WM_SIZE</c>.</summary>
internal enum WindowSizeEvent
{
    /// <summary><c>minimize</c>.</summary>
    Minimize,

    /// <summary><c>restore</c>.</summary>
    Restore,

    /// <summary><c>maximize</c>.</summary>
    Maximize,

    /// <summary><c>unmaximize</c>.</summary>
    Unmaximize,

    /// <summary><c>enter-full-screen</c>.</summary>
    EnterFullScreen,
}

/// <summary>Electron's <c>last_window_state_</c> and <c>NativeWindowViews::HandleSizeEvent</c>
/// (shell/browser/native_window_views_win.cc): which events a <c>WM_SIZE</c> raises, given the state before it.</summary>
internal sealed class WindowSizeState
{
    private const int SizeRestored = 0, SizeMinimized = 1, SizeMaximized = 2;

    private enum ShowState { Normal, Maximized, Minimized, FullScreen }

    // HandleSizeEvent treats normal and full screen alike; only maximized and minimized change what WM_SIZE raises, so
    // a window created full screen needs no state of its own.
    private ShowState _state;

    /// <summary>Electron's <c>SetFullScreen</c> on Windows: the state follows the request, before any resize. Also called
    /// when the page entered full screen, which does not go through <c>SetFullScreen</c>.</summary>
    internal void SetFullScreen(bool fullScreen) => _state = fullScreen ? ShowState.FullScreen : ShowState.Normal;

    /// <summary>Electron's <c>SetFullScreen</c> without a thick frame, where Chromium's full screen cannot be used:
    /// entering saves <paramref name="current"/> as the restore bounds and covers <paramref name="display"/>; leaving
    /// returns to the saved bounds. Null bounds leave the window where it is.</summary>
    internal static (Rectangle? Bounds, Rectangle? Restore) ThinFrameFullScreen(bool enter, Rectangle current, Rectangle display, Rectangle? restore)
        // Electron leaves to its saved rect even when it never entered, an empty one; nothing is the useful reading.
        => enter ? (display, current) : (restore, restore);

    /// <summary>The events, in order, for a <c>WM_SIZE</c> of type <paramref name="sizeType"/>.</summary>
    internal List<WindowSizeEvent> OnSize(int sizeType, bool isFullScreen)
    {
        var events = new List<WindowSizeEvent>(2);
        switch (sizeType)
        {
            // Windows may send these more than once for one change; only a new state counts.
            case SizeMaximized when _state != ShowState.Maximized:
                if (_state == ShowState.Minimized) events.Add(WindowSizeEvent.Restore);
                _state = ShowState.Maximized;
                events.Add(WindowSizeEvent.Maximize);
                break;
            case SizeMinimized when _state != ShowState.Minimized:
                _state = ShowState.Minimized;
                events.Add(WindowSizeEvent.Minimize);
                break;
            case SizeRestored when _state == ShowState.Maximized:
                _state = ShowState.Normal;
                events.Add(WindowSizeEvent.Unmaximize);
                break;
            case SizeRestored when _state == ShowState.Minimized:
                _state = isFullScreen ? ShowState.FullScreen : ShowState.Normal;
                events.Add(isFullScreen ? WindowSizeEvent.EnterFullScreen : WindowSizeEvent.Restore);
                break;
        }
        return events;
    }
}
