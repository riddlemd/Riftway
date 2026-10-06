using Oriel.Cef;
using Oriel.Cef.Handlers;

namespace Oriel;

/// <summary>Why a cookie changed. Electron's <c>changed</c> event <c>cause</c>, from <c>net::CookieChangeCause</c>.</summary>
public enum CookieChangeCause
{
    /// <summary><c>inserted</c>: the cookie was added.</summary>
    Inserted,
    /// <summary><c>inserted-no-change-overwrite</c>: it replaced an identical cookie.</summary>
    InsertedNoChangeOverwrite,
    /// <summary><c>inserted-no-value-change-overwrite</c>: it replaced a cookie with the same value but other attributes.</summary>
    InsertedNoValueChangeOverwrite,
    /// <summary><c>explicit</c>: it was deleted on request.</summary>
    Explicit,
    /// <summary><c>overwrite</c>: a new cookie replaced it.</summary>
    Overwrite,
    /// <summary><c>expired</c>: it expired.</summary>
    Expired,
    /// <summary><c>evicted</c>: the store dropped it to make room.</summary>
    Evicted,
    /// <summary><c>expired-overwrite</c>: a cookie that had already expired replaced it.</summary>
    ExpiredOverwrite,
}

/// <summary>A cookie was added, replaced or removed. Electron's <c>cookies</c> <c>changed</c> event.</summary>
/// <param name="cookie">The cookie: the old one when <paramref name="removed"/> is true.</param>
/// <param name="cause">Why it changed.</param>
/// <param name="removed">Whether the cookie left the store.</param>
public sealed class CookieChangedEventArgs(Cookie cookie, CookieChangeCause cause, bool removed) : EventArgs
{
    /// <summary>The cookie that changed.</summary>
    public Cookie Cookie { get; } = cookie;

    /// <summary>Why it changed.</summary>
    public CookieChangeCause Cause { get; } = cause;

    /// <summary>Whether the cookie was removed.</summary>
    public bool Removed { get; } = removed;
}

/// <summary>The changes between two reads of a cookie store, with the causes Chromium's <c>CookieMonster</c> gives.</summary>
internal static class CookieDiff
{
    /// <summary>What makes cookies the same cookie: <c>CanonicalCookie::RefUniqueKey</c> without the partition.</summary>
    internal readonly record struct Key(string Name, string Domain, string Path);

    /// <summary>A stored cookie and its creation time, which tells a replaced cookie from an untouched one.</summary>
    internal sealed record Stored(Cookie Cookie, long Creation)
    {
        public Key Key => new(Cookie.Name, Cookie.Domain, Cookie.Path);
    }

    /// <summary>An Oriel call between the two reads, which settles causes a diff alone cannot.</summary>
    internal abstract record Operation;

    /// <summary><see cref="Cookies.SetAsync"/> wrote the cookie with this key.</summary>
    internal sealed record Set(Key Key) : Operation;

    /// <summary><see cref="Cookies.RemoveAsync"/> deleted cookies with this name.</summary>
    internal sealed record Remove(string Name) : Operation;

    internal static Stored FromCef(CefCookie cookie) => new(Cookies.ToCookie(cookie), cookie.Creation);

    /// <summary>The events for going from <paramref name="before"/> to <paramref name="after"/>, in Chromium's order: a
    /// replacement is the old cookie's <see cref="CookieChangeCause.Overwrite"/>, then the new one's insertion.</summary>
    /// <remarks>A removal nothing explains is <see cref="CookieChangeCause.Expired"/> when its expiry has passed, else
    /// <see cref="CookieChangeCause.ExpiredOverwrite"/>, how pages delete cookies.</remarks>
    internal static List<CookieChangedEventArgs> Compare(IReadOnlyList<Stored> before, IReadOnlyList<Stored> after, double nowUnixSeconds,
        Operation? operation = null)
    {
        var events = new List<CookieChangedEventArgs>();
        var old = Index(before);
        var now = Index(after);
        foreach (var (key, was) in old)
        {
            var written = operation is Set set && set.Key == key;
            if (now.TryGetValue(key, out var @is))
            {
                if (!written && was == @is) continue;
                events.Add(new(was.Cookie, CookieChangeCause.Overwrite, true));
                events.Add(new(@is.Cookie, InsertCause(was.Cookie, @is.Cookie), false));
            }
            else
            {
                var cause = operation is Remove remove && remove.Name == key.Name ? CookieChangeCause.Explicit
                    : written ? CookieChangeCause.ExpiredOverwrite
                    : was.Cookie.ExpirationDate is { } expiry && expiry <= nowUnixSeconds ? CookieChangeCause.Expired
                    : CookieChangeCause.ExpiredOverwrite;
                events.Add(new(was.Cookie, cause, true));
            }
        }
        foreach (var (key, @is) in now)
            if (!old.ContainsKey(key)) events.Add(new(@is.Cookie, CookieChangeCause.Inserted, false));
        return events;
    }

    // CookieMonster::MaybeDeleteEquivalentCookieAndUpdateStatus: same value is "no value change"; also the same
    // Secure, SameSite, HttpOnly and expiry (CanonicalCookie::IsWebEquivalentTo) is "no change".
    private static CookieChangeCause InsertCause(Cookie was, Cookie @is)
    {
        if (was.Value != @is.Value) return CookieChangeCause.Inserted;
        return was.Secure == @is.Secure && was.SameSite == @is.SameSite && was.HttpOnly == @is.HttpOnly && was.ExpirationDate == @is.ExpirationDate
            ? CookieChangeCause.InsertedNoChangeOverwrite
            : CookieChangeCause.InsertedNoValueChangeOverwrite;
    }

    // Partitioned cookies can share a key, as CEF does not report the partition; the first one stands for them.
    private static Dictionary<Key, Stored> Index(IReadOnlyList<Stored> cookies)
    {
        var index = new Dictionary<Key, Stored>();
        foreach (var cookie in cookies) index.TryAdd(cookie.Key, cookie);
        return index;
    }
}

/// <summary>Watches a cookie store while <see cref="Cookies.Changed"/> has handlers, by reading the whole store and
/// comparing: after each Oriel write, soon after a response sets a cookie, and every <see cref="PollInterval"/>.</summary>
internal sealed class CookieWatch(Func<Task<IReadOnlyList<CefCookie>>> readAll, Func<IReadOnlyList<CookieChangedEventArgs>, Task> raise)
{
    /// <summary>How often the store is read for changes nothing announces: page scripts' writes, expiry, eviction.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    // A response's cookies reach the store after its headers do.
    private static readonly TimeSpan s_responseDelay = TimeSpan.FromMilliseconds(50);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private bool _active;
    private IReadOnlyList<CookieDiff.Stored>? _snapshot;
    private int _generation;

    /// <summary>Whether changes are being watched.</summary>
    public bool Active
    {
        get { lock (_lock) return _active; }
    }

    /// <summary>Starts watching: reads the store, then polls it.</summary>
    public void Start()
    {
        int generation;
        lock (_lock)
        {
            if (_active) return;
            _active = true;
            generation = ++_generation;
            _snapshot = null;
        }
        CefThread.RunOnUi(() => Poll(generation));
    }

    // Reposted on the UI thread rather than a timer, so the chain ends with CEF's message loop.
    private void Poll(int generation)
    {
        lock (_lock)
            if (!_active || generation != _generation) return;
        _ = ScanAsync();
        CefThread.PostUiDelayed(() => Poll(generation), PollInterval);
    }

    /// <summary>Stops watching and forgets the store's contents.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _active = false;
            _generation++;
        }
    }

    /// <summary>A response carried <c>Set-Cookie</c>: reads the store once its cookies are in.</summary>
    public void ResponseSetCookie()
    {
        if (!Active) return;
        CefThread.PostUiDelayed(() => _ = ScanAsync(), s_responseDelay);
    }

    /// <summary>Runs an Oriel write between two reads of the store, so its changes get its causes.</summary>
    public async Task RunAsync(CookieDiff.Operation operation, Func<Task> write)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var generation = Volatile.Read(ref _generation);
            await ScanCoreAsync(null, generation).ConfigureAwait(false);
            try { await write().ConfigureAwait(false); }
            finally { await ScanCoreAsync(operation, generation).ConfigureAwait(false); }
        }
        finally { _gate.Release(); }
    }

    private async Task ScanAsync()
    {
        // Overlapping polls would only read the same store twice.
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;
        try { await ScanCoreAsync(null, Volatile.Read(ref _generation)).ConfigureAwait(false); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Reading the cookie store for changes failed: {e}"); }
        finally { _gate.Release(); }
    }

    private async Task ScanCoreAsync(CookieDiff.Operation? operation, int generation)
    {
        if (!Active) return;
        var after = (await readAll().ConfigureAwait(false)).Select(CookieDiff.FromCef).ToList();
        IReadOnlyList<CookieDiff.Stored>? before;
        lock (_lock)
        {
            // Stopped, or stopped and started again, while reading: that read belongs to no watch.
            if (!_active || generation != _generation) return;
            before = _snapshot;
            _snapshot = after;
        }
        if (before is null) return;
        var events = CookieDiff.Compare(before, after, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, operation);
        if (events.Count > 0) await raise(events).ConfigureAwait(false);
    }
}
