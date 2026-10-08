namespace Oriel;

public sealed unsafe partial class OrielApp
{
    /// <summary>A <see cref="BrowserWindow"/> was created. Electron's <c>browser-window-created</c>.</summary>
    public event EventHandler<BrowserWindow>? BrowserWindowCreated;

    /// <summary>A <see cref="WebContents"/> was created. Electron's <c>web-contents-created</c>.</summary>
    public event EventHandler<WebContents>? WebContentsCreated;

    /// <summary>A <see cref="Session"/> was created. Electron's <c>session-created</c>.</summary>
    public event EventHandler<Session>? SessionCreated;

    /// <summary>A window got focus. Electron's <c>browser-window-focus</c>.</summary>
    public event EventHandler<BrowserWindow>? BrowserWindowFocus;

    /// <summary>A window lost focus. Electron's <c>browser-window-blur</c>.</summary>
    public event EventHandler<BrowserWindow>? BrowserWindowBlur;

    internal void RaiseBrowserWindowCreated(BrowserWindow window) => RaiseApp(BrowserWindowCreated, window, "browser-window-created");

    internal void RaiseWebContentsCreated(WebContents page) => RaiseApp(WebContentsCreated, page, "web-contents-created");

    internal void RaiseSessionCreated(Session session) => RaiseApp(SessionCreated, session, "session-created");

    internal void RaiseBrowserWindowFocus(BrowserWindow window, bool focused)
        => RaiseApp(focused ? BrowserWindowFocus : BrowserWindowBlur, window, focused ? "browser-window-focus" : "browser-window-blur");

    private void RaiseApp<T>(EventHandler<T>? handler, T value, string name)
    {
        try { handler?.Invoke(this, value); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] An app {name} handler failed: {e}"); }
    }
}
