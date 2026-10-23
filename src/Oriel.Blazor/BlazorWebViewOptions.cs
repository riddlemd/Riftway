namespace Oriel.Blazor;

/// <summary>Where a Blazor window's host page and static files come from.</summary>
public sealed record BlazorWebViewOptions
{
    /// <summary>The static files root; <c>wwwroot</c> beside the app by default.</summary>
    public string? ContentRoot { get; init; }

    /// <summary>The host page, relative to <see cref="ContentRoot"/>.</summary>
    public string HostPage { get; init; } = "index.html";

    /// <summary>The path loaded first, relative to the app's base URI (Blazor routes from it).</summary>
    public string StartPath { get; init; } = "/";
}
