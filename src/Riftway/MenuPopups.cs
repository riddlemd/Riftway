namespace Riftway;

/// <summary>The windows a menu is popped up in, as Electron's per-menu popup controllers: <c>closePopup</c> reads them
/// from any thread, since it cannot ask the UI thread while AppKit's menu loop holds it.</summary>
internal sealed class MenuPopups<TWindow> where TWindow : class
{
    private readonly Lock _gate = new();
    private readonly List<(long Token, TWindow Window)> _open = [];
    private long _next;

    /// <summary>Records a popup in <paramref name="window"/>; the token ends it.</summary>
    internal long Opened(TWindow window)
    {
        lock (_gate)
        {
            var token = ++_next;
            _open.Add((token, window));
            return token;
        }
    }

    internal void Closed(long token)
    {
        lock (_gate) _open.RemoveAll(p => p.Token == token);
    }

    /// <summary>Electron's <c>ClosePopupAt</c>: the windows to close this menu's popups in, every one without
    /// <paramref name="window"/>, else that one if the menu is popped up there.</summary>
    internal IReadOnlyList<TWindow> Targets(TWindow? window)
    {
        lock (_gate) return [.. _open.Select(p => p.Window).Where(w => window is null || ReferenceEquals(w, window)).Distinct()];
    }
}
