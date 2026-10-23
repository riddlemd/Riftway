using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.FileProviders;

namespace Oriel.Blazor;

/// <summary>Blazor Hybrid in one <see cref="BrowserWindow"/>: the Razor renderer runs in-process and its render
/// batches reach the page over Oriel's IPC.</summary>
/// <remarks>Disposed when the window's page is destroyed.</remarks>
public sealed class BlazorWebView : WebViewManager
{
    private const string WebViewScript = "_framework/blazor.webview.js";
    private static readonly Lazy<byte[]> s_shim = new(() => Encoding.UTF8.GetBytes(OrielBlazor.ReadScript("webview-shim.js") + "\n"));

    private readonly WebContents _webContents;
    private readonly BlazorWebViewOptions _options;
    private int _disposed;

    internal event Action? Disposed;

    internal BlazorWebView(BrowserWindow window, IServiceProvider services, BlazorWebViewOptions options)
        : this(window.WebContents, services, options, Dispatcher.CreateDefault(), ContentRoot(options))
    {
        Window = window;
    }

    private BlazorWebView(WebContents webContents, IServiceProvider services, BlazorWebViewOptions options, Dispatcher dispatcher, string contentRoot)
        : base(services, dispatcher, OrielBlazor.AppBaseUri, FileProvider(contentRoot), new JSComponentConfigurationStore(), options.HostPage)
    {
        _webContents = webContents;
        _options = options;
        _webContents.Ipc.On(OrielBlazor.Channel, OnMessage);
        _webContents.Destroyed += OnDestroyed;
    }

    /// <summary>The window this view renders into.</summary>
    public BrowserWindow Window { get; } = null!;

    internal void Start(Type componentType, string selector)
    {
        _ = Dispatcher.InvokeAsync(() => AddRootComponentAsync(componentType, selector, ParameterView.Empty));
        Navigate(_options.StartPath);
    }

    /// <inheritdoc />
    protected override void NavigateCore(Uri absoluteUri) => _ = _webContents.LoadUrl(absoluteUri.AbsoluteUri);

    /// <inheritdoc />
    protected override void SendMessage(string message)
    {
        if (_webContents.IsDestroyed()) return;
        _webContents.Send(OrielBlazor.Channel, JsonValue.Create(message));
    }

    private void OnMessage(IpcMainEvent e)
    {
        if (e.Args.Count == 0 || e.Args[0].ValueKind != JsonValueKind.String) return;
        var message = e.Args[0].GetString()!;
        // Blazor's message handling is not thread-safe; the dispatcher runs messages one at a time, in order.
        _ = Dispatcher.InvokeAsync(() => MessageReceived(OrielBlazor.AppBaseUri, message));
    }

    internal ProtocolResponse Serve(string url)
    {
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
        _webContents.Ipc.RemoveAllListeners(OrielBlazor.Channel);
        Disposed?.Invoke();
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    private static string ContentRoot(BlazorWebViewOptions options)
        => Path.GetFullPath(options.ContentRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"));

    private static IFileProvider FileProvider(string contentRoot)
        => Directory.Exists(contentRoot) ? new PhysicalFileProvider(contentRoot) : new NullFileProvider();
}
