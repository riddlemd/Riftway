using System.Text.Json;
using Oriel.Cef;

namespace Oriel;

/// <summary>A shared worker running anywhere in the app. Electron's <c>SharedWorkerInfo</c>.</summary>
/// <param name="Id">Its DevTools target id.</param>
/// <param name="Url">Its script's URL.</param>
public sealed record SharedWorkerInfo(string Id, string Url);

/// <summary>The app's shared workers, as DevTools lists them.</summary>
public sealed unsafe partial class WebContents
{
    // UI thread: shared workers DevTools has reported to this page's session since discovery began, by target id.
    private Dictionary<string, string>? _sharedWorkers;

    /// <summary>Every shared worker in the app, from any page; empty when this page's <c>devTools</c> web preference is
    /// off. Electron's <c>webContents.getAllSharedWorkers()</c>. Read through DevTools' target discovery, which this
    /// page's first call starts; on the UI thread that first call returns before any worker is known (drift C54).</summary>
    public IReadOnlyList<SharedWorkerInfo> GetAllSharedWorkers()
    {
        if (!_preferences.DevTools) return [];
        // DevTools queues its report of the running workers on the UI thread while discovery starts, ahead of the read.
        CefThread.InvokeOnUi(StartSharedWorkerDiscovery);
        return CefThread.InvokeOnUi(() => (IReadOnlyList<SharedWorkerInfo>)[.. (_sharedWorkers ?? []).Select(w => new SharedWorkerInfo(w.Key, w.Value))]);
    }

    // UI thread. DevTools reports the targets already running, then each change.
    private bool StartSharedWorkerDiscovery()
    {
        if (_sharedWorkers is not null || _browser is null) return true;
        _sharedWorkers = [];
        _devTools.EventReceived += OnTargetDiscovered;
        _devTools.Send("Target.setDiscoverTargets", w =>
        {
            w.WriteBoolean("discover", true);
            w.WriteStartArray("filter");
            w.WriteStartObject();
            w.WriteString("type", "shared_worker");
            w.WriteEndObject();
            w.WriteEndArray();
        });
        return true;
    }

    // UI thread.
    private void OnTargetDiscovered(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "Target.targetCreated" or "Target.targetInfoChanged":
                // Discovery is filtered to shared workers.
                var info = parameters.GetProperty("targetInfo");
                _sharedWorkers![info.GetProperty("targetId").GetString()!] = info.GetProperty("url").GetString() ?? "";
                break;
            case "Target.targetDestroyed":
                _sharedWorkers!.Remove(parameters.GetProperty("targetId").GetString()!);
                break;
        }
    }
}
