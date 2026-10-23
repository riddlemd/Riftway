using Oriel.Blazor;

namespace Oriel.BlazorSample;

/// <summary>Opens Blazor windows; every window shares the app's services, so singletons are shared too.</summary>
public sealed class WindowOpener(IServiceProvider services)
{
    private int _count;

    public BrowserWindow Open()
    {
        var n = Interlocked.Increment(ref _count);
        var window = new BrowserWindow(new BrowserWindowOptions { Width = 900, Height = 640, Title = $"Oriel Blazor Sample {n}" });
        window.LoadBlazor<App>(services, options: new BlazorWebViewOptions
        {
            RootComponents = [new BlazorRootComponent(typeof(Footer), "#footer")],
            // Answers what no static file holds: an app's own endpoints, here one greeting.
            RequestHandler = (request, _) => Task.FromResult(new Uri(request.Url).AbsolutePath == "/api/greeting"
                ? ProtocolResponse.Text($"hello {request.Method}")
                : null),
        });
        return window;
    }
}
