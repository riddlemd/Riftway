using System.Text.Json;
using System.Text.Json.Nodes;
using Oriel.Cef;

namespace Oriel;

public sealed unsafe partial class WebContents
{
    // UI thread: a webFrame call from a frame's preload; the answer goes back to that preload as an invoke reply.
    private void OnWebFrameRequest(int context, JsonElement root)
    {
        var id = root.GetProperty("id").GetInt32();
        Task<JsonNode?> work;
        if (!_contextFrames.TryGetValue(context, out var frameId) || !_frames.TryGetValue(frameId, out var frame))
            work = Task.FromException<JsonNode?>(new InvalidOperationException("The frame has gone."));
        else
        {
            try { work = WebFrameOperation(root, frame); }
            catch (Exception e) { work = Task.FromException<JsonNode?>(e); }
        }
        work.ContinueWith(t =>
        {
            var reply = t.IsCompletedSuccessfully
                ? new JsonObject { ["t"] = "reply", ["id"] = id, ["ok"] = true, ["value"] = t.Result }
                : new JsonObject { ["t"] = "reply", ["id"] = id, ["ok"] = false, ["error"] = t.Exception?.InnerException?.Message ?? "failed" };
            CefThread.RunOnUi(() => Deliver(context, reply.ToJsonString()));
        }, TaskScheduler.Default);
    }

    private Task<JsonNode?> WebFrameOperation(JsonElement root, FrameState frame)
    {
        static Task<JsonNode?> Done() => Task.FromResult<JsonNode?>(null);
        switch (root.GetProperty("op").GetString())
        {
            case "zoomLevel":
                SetZoomLevel(root.GetProperty("value").GetDouble());
                return Done();
            case "insertCSS":
                return InsertCssInFrameAsync(root.GetProperty("css").GetString()!, frame).ContinueWith(t => (JsonNode?)JsonValue.Create(t.Result), TaskScheduler.Default);
            case "removeCSS":
                return RemoveInsertedCssAsync(root.GetProperty("key").GetString()!).ContinueWith(_ => (JsonNode?)null, TaskScheduler.Default);
            case "insertText":
                return InsertTextAsync(root.GetProperty("text").GetString()!).ContinueWith(_ => (JsonNode?)null, TaskScheduler.Default);
            case "exec":
                return EvaluateAsync(root.GetProperty("code").GetString()!, root.GetProperty("userGesture").GetBoolean(), frame);
            case "execIsolated":
                var scripts = root.GetProperty("scripts").EnumerateArray()
                    .Select(s => new WebSource(s.GetProperty("code").GetString()!, s.TryGetProperty("url", out var url) ? url.GetString() : null)).ToList();
                return ExecuteJavaScriptInIsolatedWorldInFrameAsync(root.GetProperty("worldId").GetInt32(), scripts, root.GetProperty("userGesture").GetBoolean(), frame);
            default:
                throw new InvalidOperationException("Unknown webFrame call.");
        }
    }

    // UI thread: a frame's preload world started; it learns its frame token and the page's zoom.
    private void TellFrameItself(int context, string frameId)
        => Deliver(context, new JsonObject { ["t"] = "frame", ["token"] = frameId, ["zoomLevel"] = _zoomLevelForFrames }.ToJsonString());

    // The zoom the page's preloads report; Chromium sends no notice of zoom changes, so only Oriel's own are seen.
    private double _zoomLevelForFrames;

    // UI thread.
    private void TellFramesZoom(double level)
    {
        _zoomLevelForFrames = level;
        var message = new JsonObject { ["t"] = "zoom", ["level"] = level }.ToJsonString();
        foreach (var frame in _frames.Values)
            if (frame.IsolatedContext is { } context) Deliver(context, message);
    }
}
