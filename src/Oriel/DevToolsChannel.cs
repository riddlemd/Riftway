using System.Buffers;
using System.Text.Json;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>DevTools protocol to one browser, in-process: commands out, results and events back.</summary>
/// <remarks>UI thread only, as CEF requires for the host calls.</remarks>
internal sealed unsafe class DevToolsChannel
{
    // Every message both ways on stderr, for debugging the page channel.
    private static readonly bool s_trace = Environment.GetEnvironmentVariable("ORIEL_TRACE_DEVTOOLS") == "1";

    private readonly Dictionary<int, Action<JsonElement, string?>> _pendingResults = [];
    private readonly DevToolsMessageObserver _observer;

    private cef_browser_t* _browser;
    private cef_registration_t* _registration;
    private int _nextId;

    public DevToolsChannel() => _observer = new DevToolsMessageObserver(OnMessage);

    /// <summary>A protocol event: method name and its params.</summary>
    public event Action<string, JsonElement>? EventReceived;

    public void Attach(cef_browser_t* browser)
    {
        _browser = browser;
        var host = browser->get_host(browser);
        NativeObject.AddRef(_observer.Native);
        _registration = host->add_dev_tools_message_observer(host, _observer.Native);
        if (_registration is null) Console.Error.WriteLine("[Oriel] DevTools observer registration failed.");
        NativeObject.Release(host);
    }

    public void Detach()
    {
        // Releasing the registration is what unregisters the observer.
        NativeObject.Release(_registration);
        _registration = null;
        _browser = null;
        _pendingResults.Clear();
    }

    /// <summary>Sends <paramref name="method"/>; <paramref name="onResult"/> gets the result, or an error message.</summary>
    public void Send(string method, Action<Utf8JsonWriter>? writeParams = null, Action<JsonElement, string?>? onResult = null)
    {
        if (_browser is null) return;

        var id = ++_nextId;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            if (writeParams is not null)
            {
                writer.WritePropertyName("params");
                writer.WriteStartObject();
                writeParams(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        if (onResult is not null) _pendingResults[id] = onResult;
        if (s_trace) Trace("->", buffer.WrittenSpan);

        var host = _browser->get_host(_browser);
        fixed (byte* message = buffer.WrittenSpan)
        {
            if (host->send_dev_tools_message(host, message, (nuint)buffer.WrittenCount) == 0)
                Console.Error.WriteLine($"[Oriel] DevTools rejected {method}.");
        }
        NativeObject.Release(host);
    }

    private static void Trace(string direction, ReadOnlySpan<byte> message)
    {
        var text = System.Text.Encoding.UTF8.GetString(message);
        Console.Error.WriteLine($"[devtools {direction}] {(text.Length > 400 ? text[..400] + "…" : text)}");
    }

    // CEF calls this mid-iteration of its observer list; a handler that navigates (or sends any
    // browser-handled command) re-enters that list, which Chromium's ObserverList flags.
    private void OnMessage(ReadOnlySpan<byte> message)
    {
        var copy = message.ToArray();
        CefThread.PostUi(() => Handle(copy));
    }

    private void Handle(byte[] message)
    {
        if (_browser is null) return;
        if (s_trace) Trace("<-", message);
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;

        if (root.TryGetProperty("id", out var id))
        {
            if (!_pendingResults.Remove(id.GetInt32(), out var onResult)) return;
            var error = root.TryGetProperty("error", out var e) ? e.GetProperty("message").GetString() ?? "DevTools error" : null;
            onResult(root.TryGetProperty("result", out var result) ? result.Clone() : default, error);
        }
        else if (root.TryGetProperty("method", out var method))
        {
            var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            EventReceived?.Invoke(method.GetString()!, parameters);
        }
    }
}
