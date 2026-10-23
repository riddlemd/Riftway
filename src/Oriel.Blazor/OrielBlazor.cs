using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Oriel.Blazor;

/// <summary>Blazor Hybrid hosting: the <c>app</c> scheme every Blazor window is served from, and the page channel.</summary>
public static class OrielBlazor
{
    /// <summary>The scheme Blazor pages load from.</summary>
    public const string Scheme = "app";

    /// <summary>Every Blazor window's base URI. One origin for all windows, so they share storage as one app does.</summary>
    public static readonly Uri AppBaseUri = new($"{Scheme}://localhost/");

    internal const string Channel = "oriel:blazor";

    private static readonly Lock s_gate = new();
    private static readonly ConcurrentDictionary<BlazorWebView, byte> s_views = new();
    private static readonly HashSet<Session> s_preloadedSessions = [];
    private static bool s_schemeRegistered;
    private static bool s_handled;
    private static string? s_preloadPath;

    /// <summary>Adds Blazor's WebView services and registers the <c>app</c> scheme as privileged.</summary>
    /// <remarks>Call before <see cref="OrielApp.Run"/>: Chromium reads privileged schemes at startup.</remarks>
    public static IServiceCollection AddOrielBlazor(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        RegisterScheme();
        services.AddBlazorWebView();
        return services;
    }

    /// <summary>Registers the <c>app</c> scheme as privileged; <see cref="AddOrielBlazor"/> calls it.</summary>
    public static void RegisterScheme()
    {
        lock (s_gate)
        {
            if (s_schemeRegistered) return;
            Protocol.RegisterSchemesAsPrivileged(new PrivilegedScheme(Scheme, new SchemePrivileges
            {
                Standard = true,
                Secure = true,
                SupportFetchApi = true,
                CorsEnabled = true,
                Stream = true,
            }));
            s_schemeRegistered = true;
        }
    }

    /// <summary>Loads <typeparamref name="TComponent"/> into <paramref name="window"/> as a Blazor Hybrid page.</summary>
    /// <param name="window">The window to render into.</param>
    /// <param name="services">The app's services; must include <see cref="AddOrielBlazor"/>'s.</param>
    /// <param name="selector">The host page element the component replaces.</param>
    /// <param name="options">Where the host page and static files are.</param>
    public static BlazorWebView LoadBlazor<TComponent>(
        this BrowserWindow window, IServiceProvider services, string selector = "#app", BlazorWebViewOptions? options = null)
        where TComponent : IComponent
        => LoadBlazor(window, typeof(TComponent), services, selector, options);

    /// <summary>Loads the component <paramref name="componentType"/> into <paramref name="window"/> as a Blazor Hybrid page.</summary>
    /// <param name="window">The window to render into.</param>
    /// <param name="componentType">The root component's type.</param>
    /// <param name="services">The app's services; must include <see cref="AddOrielBlazor"/>'s.</param>
    /// <param name="selector">The host page element the component replaces.</param>
    /// <param name="options">Where the host page and static files are.</param>
    public static BlazorWebView LoadBlazor(
        this BrowserWindow window, Type componentType, IServiceProvider services, string selector = "#app",
        BlazorWebViewOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(componentType);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(selector);

        EnsureHandled();
        EnsurePreload(window.WebContents.Session);
        var view = new BlazorWebView(window, services, options ?? new BlazorWebViewOptions());
        s_views[view] = 0;
        view.Disposed += () => s_views.TryRemove(view, out _);
        view.Start(componentType, selector);
        return view;
    }

    private static void EnsureHandled()
    {
        lock (s_gate)
        {
            if (s_handled) return;
            if (!s_schemeRegistered)
                throw new InvalidOperationException("Call services.AddOrielBlazor() (or OrielBlazor.RegisterScheme()) before OrielApp.Run().");
            Protocol.Handle(Scheme, Serve);
            s_handled = true;
        }
    }

    private static ProtocolResponse Serve(ProtocolRequest request)
    {
        // Every view serves the same app, so any live one answers; the newest has the latest host page options.
        var view = s_views.Keys.LastOrDefault();
        if (view is null) return new ProtocolResponse { StatusCode = 404, StatusText = "Not Found" };
        return view.Serve(request.Url);
    }

    private static void EnsurePreload(Session session)
    {
        lock (s_gate)
        {
            if (s_preloadedSessions.Contains(session)) return;
            session.RegisterPreloadScript(new PreloadScriptRegistration("frame", PreloadPath()));
            s_preloadedSessions.Add(session);
        }
    }

    // The preload API takes a file; the script is written once per content hash so concurrent apps share it.
    private static string PreloadPath()
    {
        if (s_preloadPath is not null) return s_preloadPath;
        var script = ReadScript("preload.js");
        var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(script)))[..16];
        var dir = Path.Combine(Path.GetTempPath(), "oriel-blazor");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"preload-{hash}.js");
        if (!File.Exists(path))
        {
            var temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temp, script);
            File.Move(temp, path, overwrite: true);
        }
        return s_preloadPath = path;
    }

    internal static string ReadScript(string name)
    {
        using var stream = typeof(OrielBlazor).Assembly.GetManifestResourceStream("Oriel.Blazor." + name)
            ?? throw new InvalidOperationException($"Missing embedded script {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
