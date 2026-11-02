using System.Text.Json;
using Riftway.Cef;

namespace Riftway;

public sealed partial class Session
{
    private ServiceWorkers? _serviceWorkers;

    /// <summary>The session's service workers. Electron's <c>ses.serviceWorkers</c>.</summary>
    /// <remarks>The first use starts watching through DevTools on the session's hidden page; read it before the pages
    /// whose workers matter load (drift SW1).</remarks>
    public ServiceWorkers ServiceWorkers
    {
        get
        {
            if (Volatile.Read(ref _serviceWorkers) is { } existing) return existing;
            var made = new ServiceWorkers(new HelperDevTools(this));
            if (Interlocked.CompareExchange(ref _serviceWorkers, made, null) is { } raced) return raced;
            _ = WatchServiceWorkersAsync(made);
            return made;
        }
    }

    private async Task WatchServiceWorkersAsync(ServiceWorkers workers)
    {
        try
        {
            var helper = await Helper().ConfigureAwait(false);
            // Subscribed before enabling: ServiceWorker.enable's first lists are what the state is built from.
            CefThread.RunOnUi(() => helper.AnyEventReceived += workers.OnDevToolsEvent);
            await helper.SendAsync("ServiceWorker.enable").ConfigureAwait(false);
        }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Watching the session's service workers failed: {e.Message}"); }
    }

    private sealed class HelperDevTools(Session session) : IServiceWorkerDevTools
    {
        public async Task<JsonElement> SendAsync(string method, Action<Utf8JsonWriter>? writeParams = null, string? sessionId = null)
        {
            var helper = await session.Helper().ConfigureAwait(false);
            return await helper.SendAsync(method, writeParams, sessionId).ConfigureAwait(false);
        }
    }
}
