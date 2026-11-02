using Riftway.Cef.Interop;
using Riftway.Cef.Platform;

namespace Riftway;

// Windows: minimize, restore, maximize and unmaximize come from WM_SIZE, as Electron raises them, instead of the poll.
public unsafe partial class BaseWindow
{
    // Set once the window is subclassed; null elsewhere and on failure, where the 250 ms poll raises these events.
    private WindowSizeState? _sizeState;

    private void RouteSizeEvents(cef_window_t* window)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (WindowsSizeEvents.Route(Hwnd(window), OnSize)) _sizeState = new WindowSizeState();
    }

    private void OnSize(int sizeType)
    {
        if (_sizeState is null || _window is null) return;
        foreach (var raised in _sizeState.OnSize(sizeType, _window->is_fullscreen(_window) != 0))
        {
            var handler = raised switch
            {
                WindowSizeEvent.Minimize => Minimized,
                WindowSizeEvent.Restore => Restored,
                WindowSizeEvent.Maximize => Maximized,
                WindowSizeEvent.Unmaximize => Unmaximized,
                _ => EnteredFullScreen,
            };
            handler?.Invoke(this, EventArgs.Empty);
        }
    }
}
