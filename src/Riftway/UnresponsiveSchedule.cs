namespace Riftway;

/// <summary>The window's <c>unresponsive</c> timer of Electron v44's <c>browser-window.ts</c>: 50 ms after the page
/// hangs, or 5 s after a close goes ahead, unless the page answers or is destroyed first; one timer at a time.</summary>
internal sealed class UnresponsiveSchedule(Action<Action, TimeSpan> post, Func<bool> canRaise, Action raise)
{
    internal static readonly TimeSpan HangDelay = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(5);

    // A posted task cannot be withdrawn, so a cancel moves the generation and the stale task finds it moved.
    private int _generation;
    private bool _pending;

    internal void PageUnresponsive() => Schedule(HangDelay);

    internal void CloseProceeding() => Schedule(CloseDelay);

    internal void PageResponsive() => Cancel();

    internal void PageDestroyed() => Cancel();

    private void Schedule(TimeSpan delay)
    {
        if (_pending) return;
        _pending = true;
        var generation = _generation;
        post(() =>
        {
            if (generation != _generation) return;
            _pending = false;
            if (canRaise()) raise();
        }, delay);
    }

    private void Cancel()
    {
        _pending = false;
        _generation++;
    }
}
