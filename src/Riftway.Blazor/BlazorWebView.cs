using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.FileProviders;

namespace Riftway.Blazor;

/// <summary>Blazor Hybrid in one <see cref="BrowserWindow"/>: the Razor renderer runs in-process and its render
/// batches reach the page over Riftway's IPC.</summary>
/// <remarks>Disposed when the window's page is destroyed.</remarks>
public sealed class BlazorWebView : WebViewManager
{
    private const string WebViewScript = "_framework/blazor.webview.js";
    private static readonly Lazy<byte[]> s_shim = new(() => Encoding.UTF8.GetBytes(RiftwayBlazor.ReadScript("webview-shim.js") + "\n"));

    private readonly WebContents _webContents;
    private readonly BlazorWebViewOptions _options;
    // The page's messages in arrival order, handled off Chromium's UI thread; see OnMessage.
    private readonly Channel<string> _inbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly IFileProvider _staticFiles;
    private int _disposed;

    internal event Action? Disposed;

    internal BlazorWebView(BrowserWindow window, IServiceProvider services, BlazorWebViewOptions options)
        : this(window.WebContents, services, options, Dispatcher.CreateDefault(), StaticFiles(options, AppContext.BaseDirectory))
    {
        Window = window;
    }

    private BlazorWebView(WebContents webContents, IServiceProvider services, BlazorWebViewOptions options, Dispatcher dispatcher, IFileProvider staticFiles)
        : base(services, dispatcher, RiftwayBlazor.AppBaseUri, staticFiles, new JSComponentConfigurationStore(), options.HostPage)
    {
        _staticFiles = staticFiles;
        _webContents = webContents;
        _options = options;
        _webContents.Ipc.On(RiftwayBlazor.Channel, OnMessage);
        _webContents.Destroyed += OnDestroyed;
        _ = Task.Run(PumpAsync);
    }

    /// <summary>The window this view renders into.</summary>
    public BrowserWindow Window { get; } = null!;

    internal void Start(Type componentType, string selector)
    {
        // Off the UI thread, like every render: the components' first lifecycle runs inside.
        _ = Task.Run(() => Dispatcher.InvokeAsync(async () =>
        {
            await AddRootComponentAsync(componentType, selector, ParameterView.Empty).ConfigureAwait(false);
            foreach (var root in _options.RootComponents)
                await AddRootComponentAsync(root.ComponentType, root.Selector, ParameterView.Empty).ConfigureAwait(false);
        }));
        Navigate(_options.StartPath);
    }

    /// <summary>The app's handler for a request, if it has one and answers it.</summary>
    internal Task<ProtocolResponse?> HandleAsync(ProtocolRequest request, CancellationToken cancellationToken)
        => _options.RequestHandler is { } handler ? handler(request, cancellationToken) : Task.FromResult<ProtocolResponse?>(null);

    /// <inheritdoc />
    protected override void NavigateCore(Uri absoluteUri) => _ = _webContents.LoadUrl(absoluteUri.AbsoluteUri);

    /// <inheritdoc />
    protected override void SendMessage(string message)
    {
        if (_webContents.IsDestroyed()) return;
        _webContents.Send(RiftwayBlazor.Channel, JsonValue.Create(message));
    }

    // Arrives on Chromium's UI thread. The default dispatcher would run the component's handler right here, and a
    // handler that blocks (a sync process wait inside a library) would freeze every window; Blazor Server hands
    // a circuit's messages to the thread pool, which is what components are written for.
    private void OnMessage(IpcMainEvent e)
    {
        if (e.Args.Count == 0 || e.Args[0].ValueKind != JsonValueKind.String) return;
        _inbox.Writer.TryWrite(e.Args[0].GetString()!);
    }

    // One message at a time and in order: Blazor's message handling is not thread-safe.
    private async Task PumpAsync()
    {
        await foreach (var message in _inbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await Dispatcher.InvokeAsync(() => MessageReceived(RiftwayBlazor.AppBaseUri, message)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The renderer has already reported it to the page; one bad message must not stop the rest.
                Console.Error.WriteLine($"[Riftway.Blazor] A page message failed: {ex}");
            }
        }
    }

    internal ProtocolResponse Serve(string url)
    {
        // The file is looked up by the whole URL, so a cache-busting query (font.woff2?v=1) would miss it and get the
        // host page instead; MAUI's handlers strip it too.
        var query = url.IndexOfAny(['?', '#']);
        if (query >= 0) url = url[..query];
        if (!TryGetResponseContent(url, allowFallbackOnHostPage: true, out var status, out var statusText, out var content, out var headers))
            return new ProtocolResponse { StatusCode = 404, StatusText = "Not Found" };

        var path = new Uri(url).AbsolutePath.TrimStart('/');
        if (path.Equals(WebViewScript, StringComparison.Ordinal))
            content = new ConcatStream(new MemoryStream(s_shim.Value, writable: false), content);

        headers.TryGetValue("Content-Type", out var contentType);
        return new ProtocolResponse
        {
            StatusCode = status,
            StatusText = statusText,
            ContentType = contentType,
            Headers = headers.Where(h => !h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase),
            Body = content,
        };
    }

    private void OnDestroyed(object? sender, EventArgs e) => _ = DisposeAsync().AsTask();

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _webContents.Destroyed -= OnDestroyed;
        _webContents.Ipc.RemoveAllListeners(RiftwayBlazor.Channel);
        _inbox.Writer.TryComplete();
        Disposed?.Invoke();
        await base.DisposeAsyncCore().ConfigureAwait(false);
        (_staticFiles as IDisposable)?.Dispose();
    }

    /// <summary>Where the static files come from: <see cref="BlazorWebViewOptions.ContentRoot"/> when given (a folder, or
    /// an ASAR archive file); otherwise, as Electron, <c>resources/app.asar</c> beside the app before <c>wwwroot</c>.</summary>
    internal static IFileProvider StaticFiles(BlazorWebViewOptions options, string baseDirectory)
    {
        if (options.ContentRoot is null)
        {
            var archive = Path.Combine(baseDirectory, "resources", "app.asar");
            if (File.Exists(archive)) return new AsarFileProvider(archive);
        }
        var root = Path.GetFullPath(options.ContentRoot ?? Path.Combine(baseDirectory, "wwwroot"));
        if (File.Exists(root)) return new AsarFileProvider(root);
        return Directory.Exists(root) ? new PhysicalFileProvider(root) : new NullFileProvider();
    }
}
