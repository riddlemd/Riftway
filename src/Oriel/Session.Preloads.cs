using Oriel.Cef;

namespace Oriel;

/// <summary>A script to register with <see cref="Session.RegisterPreloadScript"/>; Electron's <c>PreloadScriptRegistration</c>.</summary>
/// <param name="Type"><c>frame</c>: runs in every page of the session before the page's own preload.</param>
/// <param name="FilePath">The script's absolute path.</param>
public sealed record PreloadScriptRegistration(string Type, string FilePath)
{
    /// <summary>The script's id; a new one when null.</summary>
    public string? Id { get; init; }
}

/// <summary>A registered preload script; Electron's <c>PreloadScript</c>.</summary>
public sealed record PreloadScript(string Type, string Id, string FilePath);

public sealed partial class Session
{
    private readonly List<PreloadScript> _preloads = [];

    /// <summary>Adds a script that runs in every page of this session, from its next load, before the page's own
    /// preload. Returns its id. Electron's <c>ses.registerPreloadScript()</c>.</summary>
    /// <exception cref="ArgumentException">The path is not absolute, or the id is taken.</exception>
    /// <exception cref="NotSupportedException">The type is <c>service-worker</c>: CEF gives service workers no preload.</exception>
    public string RegisterPreloadScript(PreloadScriptRegistration script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.Type == "service-worker") throw new NotSupportedException("Service worker preload scripts are not supported: CEF runs no script in a service worker.");
        if (script.Type != "frame") throw new ArgumentException($"Invalid preload script type: {script.Type}", nameof(script));
        if (!Path.IsPathFullyQualified(script.FilePath)) throw new ArgumentException("Preload script must have absolute path.", nameof(script));
        var id = script.Id ?? Guid.NewGuid().ToString();
        lock (_preloads)
        {
            if (_preloads.Any(p => p.Id == id)) throw new ArgumentException($"Cannot register preload script with existing ID '{id}'", nameof(script));
            _preloads.Add(new PreloadScript(script.Type, id, script.FilePath));
        }
        PreloadsChanged();
        return id;
    }

    /// <summary>Removes a script <see cref="RegisterPreloadScript"/> added, from each page's next load. Electron's
    /// <c>ses.unregisterPreloadScript()</c>.</summary>
    public void UnregisterPreloadScript(string id)
    {
        bool removed;
        lock (_preloads) removed = _preloads.RemoveAll(p => p.Id == id) > 0;
        if (removed) PreloadsChanged();
    }

    /// <summary>The registered preload scripts, in the order they run. Electron's <c>ses.getPreloadScripts()</c>.</summary>
    public IReadOnlyList<PreloadScript> GetPreloadScripts()
    {
        lock (_preloads) return [.. _preloads];
    }

    private void PreloadsChanged()
    {
        WebContents[] pages;
        lock (_pages) pages = [.. _pages.Select(p => p.TryGetTarget(out var page) ? page : null).OfType<WebContents>()];
        CefThread.RunOnUi(() =>
        {
            foreach (var page in pages) page.OnSessionPreloadsChanged();
        });
    }
}
