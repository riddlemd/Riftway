using Riftway.Cef;
using Riftway.Cef.Handlers;

namespace Riftway;

/// <summary>A page's <c>alert</c> and <c>confirm</c> as Electron's <c>-run-dialog</c> handler shows them
/// (lib/browser/api/web-contents.ts): a message box, modal to the page's window; <c>prompt</c> unsupported; a count per
/// origin for <see cref="WebPreferences.SafeDialogs"/>, and an origin the user stopped gets no more dialogs.</summary>
internal sealed class JavaScriptDialogs(Func<MessageBoxOptions, Task<MessageBoxResult>> show, Func<bool> isDestroyed)
{
    /// <summary>Electron's default checkbox label.</summary>
    internal const string DefaultSafeDialogsMessage = "Prevent this app from creating additional dialogs";

    private readonly Lock _gate = new();
    // Dialogs per origin; -1 once the user ticked the checkbox. Kept for the page's life, as Electron's originCounts.
    private readonly Dictionary<string, int> _originCounts = new(StringComparer.Ordinal);
    private readonly HashSet<CancellationTokenSource> _open = [];

    /// <summary>The origin dialogs are counted under: the frame's origin, or for a <c>file:</c> origin the frame's
    /// URL, as Electron's <c>info.frame.origin === 'file://' ? info.frame.url : info.frame.origin</c>.</summary>
    internal static string Origin(string originUrl, string? frameUrl)
    {
        if (originUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(frameUrl) ? originUrl : frameUrl;
        // CEF's origin URL is the origin with a path ("https://a.test/"); Electron's frame.origin has none.
        return originUrl.EndsWith('/') ? originUrl[..^1] : originUrl;
    }

    /// <summary>Starts a dialog: true when one is shown and <paramref name="answer"/> will be called (true for OK, false
    /// for Cancel, null when the dialog was cancelled or the page destroyed); false when the page gets no dialog and
    /// the call returns at once (<c>alert</c> returns, <c>confirm</c> false, <c>prompt</c> null).</summary>
    internal bool Run(string origin, JsDialogKind kind, string message, WebPreferences preferences, Action<bool?> answer)
    {
        string checkbox;
        lock (_gate)
        {
            if (_originCounts.GetValueOrDefault(origin) < 0) return false;
            if (preferences.DisableDialogs) return false;
            // "We don't support prompt() for some reason :)"
            if (kind == JsDialogKind.Prompt) return false;
            var count = _originCounts[origin] = _originCounts.GetValueOrDefault(origin) + 1;
            checkbox = count > 1 && preferences.SafeDialogs
                ? string.IsNullOrEmpty(preferences.SafeDialogsMessage) ? DefaultSafeDialogsMessage : preferences.SafeDialogsMessage
                : "";
        }
        var abort = new CancellationTokenSource();
        lock (_gate) _open.Add(abort);
        var options = kind == JsDialogKind.Confirm
            ? new MessageBoxOptions { Message = message, CheckboxLabel = checkbox, Signal = abort.Token, Buttons = ["OK", "Cancel"], DefaultId = 0, CancelId = 1 }
            : new MessageBoxOptions { Message = message, CheckboxLabel = checkbox, Signal = abort.Token, Buttons = ["OK"], DefaultId = -1, CancelId = 0 };
        _ = Finish(origin, abort, options, answer);
        return true;
    }

    private async Task Finish(string origin, CancellationTokenSource abort, MessageBoxOptions options, Action<bool?> answer)
    {
        try
        {
            MessageBoxResult result;
            try { result = await show(options).ConfigureAwait(false); }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[Riftway] A page's dialog could not be shown: {e.Message}");
                answer(null);
                return;
            }
            if (abort.IsCancellationRequested || isDestroyed())
            {
                answer(null);
                return;
            }
            if (result.CheckboxChecked)
                lock (_gate) _originCounts[origin] = -1;
            answer(result.Response == 0);
        }
        finally
        {
            lock (_gate) _open.Remove(abort);
            abort.Dispose();
        }
    }

    /// <summary>Closes every open dialog unanswered, as Electron's <c>-cancel-dialogs</c>; the counts stay.</summary>
    internal void CancelAll()
    {
        CancellationTokenSource[] open;
        lock (_gate)
        {
            open = [.. _open];
            _open.Clear();
        }
        foreach (var abort in open)
        {
            try { abort.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }
}
