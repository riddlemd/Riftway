namespace Riftway;

/// <summary>Which popup requests a <c>closePopup</c> has overtaken: a close can reach the UI thread before the queued
/// show it follows, and cancelling a menu not yet open does nothing, so the show must not open it at all.</summary>
internal sealed class PopupCancelGate
{
    private long _requested;
    private long _cancelledThrough;

    /// <summary>Any thread, as the popup is asked for: the request's number.</summary>
    internal long Request() => Interlocked.Increment(ref _requested);

    /// <summary>Any thread: every request made so far is cancelled.</summary>
    internal void CancelAll() => Interlocked.Exchange(ref _cancelledThrough, Interlocked.Read(ref _requested));

    /// <summary>Whether a close came after <paramref name="request"/> was made.</summary>
    internal bool IsCancelled(long request) => request <= Interlocked.Read(ref _cancelledThrough);
}
