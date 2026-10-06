using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>A reply to one <c>ipcRenderer.sendSync</c>: the value an IPC listener set, or Electron's error once the
/// message is dropped without one.</summary>
internal sealed class SyncReply
{
    private readonly TaskCompletionSource<string> _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string> Sent => _sent.Task;

    public bool Send(JsonNode? value) => _sent.TrySetResult(new JsonObject { ["value"] = value?.DeepClone() }.ToJsonString());

    // Electron's ReplyChannel::EnsureReplySent, when the event is collected with no reply.
    public void NeverSent() => _sent.TrySetResult(new JsonObject { ["error"] = "reply was never sent" }.ToJsonString());
}

// Sends Electron's "reply was never sent" when the event holding it is collected, as Electron does on V8 GC.
internal sealed class SyncReplySentinel(SyncReply reply)
{
    ~SyncReplySentinel() => reply.NeverSent();
}

/// <summary>Synchronous page-to-main messages. CEF has no synchronous channel from a page, so Oriel's preload sends a
/// synchronous XHR to a private secure scheme, which a handler here answers after the IPC listeners have run. A
/// document is known by a secret it registered over the asynchronous channel first.</summary>
internal static class SyncIpc
{
    internal const string Scheme = "oriel-ipc";

    // Standard, secure, CORS and fetch: a sync XHR from any page's isolated world can reach it without mixed content.
    internal static CustomScheme Registration => new(Scheme, (int)(cef_scheme_options_t.CEF_SCHEME_OPTION_STANDARD | cef_scheme_options_t.CEF_SCHEME_OPTION_SECURE
        | cef_scheme_options_t.CEF_SCHEME_OPTION_CORS_ENABLED | cef_scheme_options_t.CEF_SCHEME_OPTION_FETCH_ENABLED | cef_scheme_options_t.CEF_SCHEME_OPTION_CSP_BYPASSING));

    private static readonly ConcurrentDictionary<string, TaskCompletionSource<(WebContents Page, int Context)>> s_documents = new(StringComparer.Ordinal);
    private static SchemeHandlerFactory? s_factory;

    internal static SchemeHandlerFactory Factory => s_factory ??= new SchemeHandlerFactory(HandleAsync);

    // UI thread: a document's isolated world announced its secret.
    internal static void Register(string secret, WebContents page, int context) => Pending(secret).TrySetResult((page, context));

    // UI thread: the document is gone, and with it the secrets it registered.
    internal static void Forget(WebContents page, int? context)
    {
        foreach (var (secret, pending) in s_documents)
            if (pending.Task.IsCompletedSuccessfully && ReferenceEquals(pending.Task.Result.Page, page) && (context is null || pending.Task.Result.Context == context))
                s_documents.TryRemove(secret, out _);
    }

    private static TaskCompletionSource<(WebContents Page, int Context)> Pending(string secret)
        => s_documents.GetOrAdd(secret, _ => new TaskCompletionSource<(WebContents, int)>(TaskCreationOptions.RunContinuationsAsynchronously));

    private static async Task<ProtocolResponse> HandleAsync(ProtocolRequest request, CancellationToken token)
    {
        // Only the preload's POST carries a body.
        if (request.BodyText is not { } body) return Answer(405, "");
        string secret, channel;
        JsonElement[] args;
        try
        {
            using var message = JsonDocument.Parse(body);
            secret = message.RootElement.GetProperty("secret").GetString()!;
            channel = message.RootElement.GetProperty("ch").GetString()!;
            args = [.. message.RootElement.GetProperty("args").EnumerateArray().Select(a => a.Clone())];
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return Answer(400, ""); }

        // The secret travels over DevTools and this request over the network service, so it may not have arrived yet.
        var pending = Pending(secret);
        try { await pending.Task.WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            s_documents.TryRemove(secret, out _);
            return Answer(403, "");
        }
        var (page, context) = pending.Task.Result;
        var reply = CefThread.InvokeOnUi(() => page.DispatchSync(context, channel, args));
        return Answer(200, await reply.ConfigureAwait(false));
    }

    // Any origin: the request comes from the page's isolated world, whose origin is the page's.
    private static ProtocolResponse Answer(int status, string json) => new()
    {
        StatusCode = status,
        ContentType = "application/json; charset=utf-8",
        Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json), writable: false),
        Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*", ["Cache-Control"] = "no-store" },
    };
}
