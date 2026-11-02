using Microsoft.AspNetCore.Components;

namespace Riftway.Blazor;

/// <summary>Where a Blazor window's host page and static files come from.</summary>
public sealed record BlazorWebViewOptions
{
    /// <summary>The static files root; <c>wwwroot</c> beside the app by default.</summary>
    public string? ContentRoot { get; init; }

    /// <summary>The host page, relative to <see cref="ContentRoot"/>.</summary>
    public string HostPage { get; init; } = "index.html";

    /// <summary>The path loaded first, relative to the app's base URI (Blazor routes from it).</summary>
    public string StartPath { get; init; } = "/";

    /// <summary>Asked first for every request the page makes to the app's origin: a response answers it, null leaves
    /// it to the static files and the host page. MAUI's <c>BlazorWebView.WebResourceRequested</c>.</summary>
    /// <remarks>Runs off the UI thread, once per request; the page may make several at once.</remarks>
    public Func<ProtocolRequest, CancellationToken, Task<ProtocolResponse?>>? RequestHandler { get; init; }

    /// <summary>Root components rendered beside the one <c>LoadBlazor</c> renders, such as <c>HeadOutlet</c> at
    /// <c>head::after</c>. MAUI's <c>BlazorWebView.RootComponents</c>.</summary>
    public IReadOnlyList<BlazorRootComponent> RootComponents { get; init; } = [];
}

/// <summary>A root component and the host page element it renders into.</summary>
/// <param name="ComponentType">The component's type; it must implement <see cref="IComponent"/>.</param>
/// <param name="Selector">A CSS selector for the element it replaces, or <c>head::after</c>.</param>
public sealed record BlazorRootComponent(Type ComponentType, string Selector);
