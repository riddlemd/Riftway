using System.Text.Json;

namespace Riftway.Blazor;

/// <summary>Answers JS calls for a page that has gone, while a reloaded page takes over the same view.</summary>
/// <remarks>
/// A reload attaches a new page to the same <see cref="BlazorWebView"/>, and Blazor disposes the old page's components
/// before it renders the new page. A component that calls JS while it tidies up (a last <c>dispose</c> on a handle, or
/// disposing an <c>IJSObjectReference</c>, which is itself a call) waits on a page that will never answer, so the
/// new page is never rendered and stays blank. Blazor Server avoids this by failing such calls with
/// <c>JSDisconnectedException</c>; a WebView has no such state, so the view answers them itself.
/// <para>From the new page's <c>AttachPage</c> until the new page's renderer first speaks, every call is the old page's:
/// it is answered here
/// as done with no result, and not sent, since the new page would otherwise run the old page's tidy-up and answer
/// with a handle the new page's runtime also uses. Calls the old page left unanswered when it went are answered at
/// <c>AttachPage</c>, before the old page's components are disposed.</para>
/// <para>A new renderer's first message is its call to <c>Blazor._internal.attachWebRendererInterop</c> (made as it is
/// created, before any component is attached); <c>AttachToDocument</c> also ends the handover, should a renderer
/// ever start without that call. An old renderer that is tidying up sends neither.</para>
/// </remarks>
internal sealed class PageHandover
{
    private const string Prefix = "__bwv:";
    // The JS call a new renderer makes as it is created; see the remarks.
    private const string NewRendererCall = "Blazor._internal.attachWebRendererInterop";
    private readonly Lock _gate = new();
    // Calls sent to the current page and not yet answered, by async handle (0 = a call that wants no answer).
    private readonly HashSet<long> _unanswered = [];
    private bool _handingOver;

    /// <summary>Whether the old page's calls are being answered here.</summary>
    public bool HandingOver
    {
        get { lock (_gate) return _handingOver; }
    }

    /// <summary>Sees a message from the page before it is handled.</summary>
    /// <returns>Page messages to handle first: answers for the calls a page that has gone left unanswered.</returns>
    public IReadOnlyList<string> FromPage(string message)
    {
        if (Is(message, "EndInvokeJS"))
        {
            if (TryReadHandle(message, out var handle))
                lock (_gate) _unanswered.Remove(handle);
            return [];
        }

        if (!Is(message, "AttachPage")) return [];

        lock (_gate)
        {
            _handingOver = true;
            var answers = _unanswered.Order().Select(Answer).ToList();
            _unanswered.Clear();
            return answers;
        }
    }

    /// <summary>Sees a message on its way to the page.</summary>
    /// <param name="message">The message.</param>
    /// <param name="answer">When the message is not sent: the page message to handle in its place, if the call wants an answer.</param>
    /// <returns>Whether to send it.</returns>
    public bool ToPage(string message, out string? answer)
    {
        answer = null;

        if (Is(message, "AttachToDocument"))
        {
            lock (_gate) _handingOver = false;
            return true;
        }

        if (!Is(message, "BeginInvokeJS") || !TryReadCall(message, out var handle, out var identifier)) return true;

        lock (_gate)
        {
            if (identifier == NewRendererCall) _handingOver = false;

            if (!_handingOver)
            {
                if (handle != 0) _unanswered.Add(handle);
                return true;
            }
        }

        if (handle != 0) answer = Answer(handle);
        return false;
    }

    /// <summary>The page's reply to call <paramref name="handle"/>: done, with no result.</summary>
    internal static string Answer(long handle) => $"{Prefix}[\"EndInvokeJS\",{handle},true,\"[{handle},true,null]\"]";

    // Only the type is compared here, so render batches (most of the traffic, and large) are never parsed.
    private static bool Is(string message, string type)
        => message.StartsWith(Prefix, StringComparison.Ordinal)
           && message.AsSpan(Prefix.Length).StartsWith($"[\"{type}\"", StringComparison.Ordinal);

    // Both BeginInvokeJS (to the page) and EndInvokeJS (from it) carry the call's async handle first.
    private static bool TryReadHandle(string message, out long handle) => TryReadCall(message, out handle, out _);

    // A BeginInvokeJS carries the function's identifier after the handle.
    private static bool TryReadCall(string message, out long handle, out string? identifier)
    {
        handle = 0;
        identifier = null;
        try
        {
            using var document = JsonDocument.Parse(message.AsMemory(Prefix.Length));
            var root = document.RootElement;
            if (root.GetArrayLength() < 2 || root[1].ValueKind != JsonValueKind.Number || !root[1].TryGetInt64(out handle))
                return false;
            if (root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String)
                identifier = root[2].GetString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
