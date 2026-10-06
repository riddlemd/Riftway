using System.ComponentModel;
using System.Drawing;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>A console message's level; Electron's <c>level</c>.</summary>
public enum ConsoleMessageLevel
{
    /// <summary>Electron's <c>debug</c> (Chromium's verbose).</summary>
    Debug,

    /// <summary>Electron's <c>info</c>.</summary>
    Info,

    /// <summary>Electron's <c>warning</c>.</summary>
    Warning,

    /// <summary>Electron's <c>error</c>.</summary>
    Error,
}

/// <summary>A message the page logged; Electron's <c>console-message</c> details.</summary>
/// <param name="Message">The message.</param>
/// <param name="Level">Its level.</param>
/// <param name="LineNumber">The line it came from.</param>
/// <param name="SourceId">The script it came from.</param>
/// <param name="Frame">The frame that logged it: CEF does not say, so always null (drift C28).</param>
public sealed record ConsoleMessageEventArgs(string Message, ConsoleMessageLevel Level, int LineNumber, string SourceId, WebFrameMain? Frame);

/// <summary>The page's cursor; Electron's <c>cursor-changed</c> arguments.</summary>
/// <param name="Type">Electron's cursor name, e.g. <c>pointer</c>, <c>hand</c>, <c>text</c> or <c>custom</c>.</param>
/// <param name="Image">A custom cursor's image, else null.</param>
/// <param name="Scale">A custom cursor's scale factor, else 1.</param>
/// <param name="Size">A custom cursor's size in pixels, else empty.</param>
/// <param name="Hotspot">A custom cursor's hotspot, else (0, 0).</param>
public sealed record CursorChangedEventArgs(string Type, NativeImage? Image, double Scale, Size Size, Point Hotspot);

/// <summary>A preload that failed to load or threw; Electron's <c>preload-error</c> arguments.</summary>
/// <param name="PreloadPath">The preload's path.</param>
/// <param name="ErrorName">The error's name, e.g. <c>SyntaxError</c>.</param>
/// <param name="Message">The error's message.</param>
/// <param name="Stack">The error's stack.</param>
public sealed record PreloadErrorEventArgs(string PreloadPath, string ErrorName, string Message, string Stack);

/// <summary>Bounds a page asked for; Electron's <c>content-bounds-updated</c> arguments. Set
/// <see cref="CancelEventArgs.Cancel"/> to keep the window where it is.</summary>
public sealed class ContentBoundsUpdatedEventArgs(Rectangle bounds) : CancelEventArgs
{
    /// <summary>The window bounds the page asked for, in screen coordinates.</summary>
    public Rectangle Bounds { get; } = bounds;
}

/// <summary>Options for <see cref="WebContents.Close"/>; Electron's <c>close()</c> options.</summary>
public sealed record WebContentsCloseOptions
{
    /// <summary>Runs the page's <c>beforeunload</c> first, which can keep the page open.</summary>
    public bool WaitForBeforeUnload { get; init; }
}

public sealed unsafe partial class WebContents
{
    // UI thread.
    private bool _hasFocus;
    private bool _closingWithoutUnload;

    /// <summary>Handlers and listeners for this page's messages, asked before <see cref="IpcMain"/>'s. Electron's
    /// <c>webContents.ipc</c>.</summary>
    public IpcMainScope Ipc { get; } = new();

    /// <summary>The page sent a message with <c>ipcRenderer.send</c>; raised before any IPC listener. Electron's
    /// <c>ipc-message</c>.</summary>
    public event EventHandler<IpcMessageEventArgs>? IpcMessage;

    internal void RaiseIpcMessage(string channel, IpcMainEvent e) => Raise(IpcMessage, new IpcMessageEventArgs(channel, e), "ipc-message");

    /// <summary>A preload script failed to load or threw; the others still ran. Electron's <c>preload-error</c>.</summary>
    public event EventHandler<PreloadErrorEventArgs>? PreloadError;

    private void RaisePreloadError(PreloadErrorEventArgs args) => Raise(PreloadError, args, "preload-error");

    /// <summary>The page asked for new window bounds with <c>window.moveTo</c>, <c>resizeTo</c> and the like. Its window
    /// takes them unless a listener sets <see cref="CancelEventArgs.Cancel"/> (Electron's <c>event.preventDefault()</c>).
    /// Electron's <c>content-bounds-updated</c>.</summary>
    public event EventHandler<ContentBoundsUpdatedEventArgs>? ContentBoundsUpdated;

    /// <summary>Media in the page started playing. Electron's <c>media-started-playing</c>.</summary>
    public event EventHandler? MediaStartedPlaying;

    /// <summary>Media in the page paused or ended. Electron's <c>media-paused</c>.</summary>
    public event EventHandler? MediaPaused;

    /// <summary>The page logged a message. Electron's <c>console-message</c>.</summary>
    public event EventHandler<ConsoleMessageEventArgs>? ConsoleMessage;

    /// <summary>The link under the pointer changed; empty when it leaves one. Electron's <c>update-target-url</c>.</summary>
    public event EventHandler<string>? UpdateTargetUrl;

    /// <summary>The page changed its cursor. Electron's <c>cursor-changed</c>.</summary>
    public event EventHandler<CursorChangedEventArgs>? CursorChanged;

    /// <summary>The page's view took keyboard focus. Electron's <c>focus</c>.</summary>
    public event EventHandler? Focused;

    /// <summary>The page's view lost keyboard focus. Electron's <c>blur</c>.</summary>
    public event EventHandler? Blurred;

    /// <summary>The page is gone for good. Electron's <c>destroyed</c>.</summary>
    public event EventHandler? Destroyed;

    /// <summary>A frame was created in the page. Electron's <c>frame-created</c>; subframes only (drift C29).</summary>
    public event EventHandler<WebFrameMain>? FrameCreated;

    /// <summary>A <c>beforeunload</c> handler objects to the page closing, reloading or navigating away; the page stays
    /// unless a listener sets <see cref="CancelEventArgs.Cancel"/> (Electron's <c>event.preventDefault()</c>), which lets
    /// it go. No dialog is shown. Electron's <c>will-prevent-unload</c>.</summary>
    public event EventHandler<CancelEventArgs>? WillPreventUnload;

    /// <summary>Whether the page is gone. Electron's <c>webContents.isDestroyed()</c>.</summary>
    public bool IsDestroyed() => _destroyed.IsCancellationRequested;

    /// <summary>Closes the page, as if it called <c>window.close()</c>; a window's page closes its window. Electron's
    /// <c>webContents.close()</c>.</summary>
    public void Close(WebContentsCloseOptions? options = null)
    {
        if (!(options?.WaitForBeforeUnload ?? false))
        {
            // Chrome style asks beforeunload even when forced, so the forced close answers it (drift C32).
            WithHost(host =>
            {
                _closingWithoutUnload = true;
                host->close_browser(host, 1);
            });
            return;
        }
        // Electron's: user activation first, or Chromium skips a beforeunload handler that has not seen any.
        _ = ExecuteJavaScriptAsync("0", userGesture: true).ContinueWith(_ => WithHost(host => host->close_browser(host, 0)), TaskScheduler.Default);
    }

    /// <summary>Focuses the page, and on macOS and Linux its window first, as Electron does. Electron's
    /// <c>webContents.focus()</c>.</summary>
    public void Focus()
    {
        if (!OperatingSystem.IsWindows()) BrowserWindow.FromWebContents(this)?.Focus();
        WithHost(host => host->set_focus(host, 1));
    }

    /// <summary>Whether the page's view has keyboard focus in a visible window. Electron's <c>webContents.isFocused()</c>.</summary>
    public bool IsFocused() => CefThread.InvokeOnUi(() => _hasFocus) && BrowserWindow.FromWebContents(this) is not { IsVisible: false };

    internal FocusHandler FocusEvents => _focusEvents ??= new FocusHandler { GotFocus = OnGotFocus };

    private FocusHandler? _focusEvents;

    // UI thread. CEF reports only the gain; the loss comes from the window, the view's one way to lose focus here.
    private void OnGotFocus()
    {
        _hasFocus = true;
        Raise(Focused, EventArgs.Empty, "focus");
    }

    internal void OnViewBlurred()
    {
        if (!_hasFocus) return;
        _hasFocus = false;
        Raise(Blurred, EventArgs.Empty, "blur");
    }

    // UI thread: true leaves the page.
    private bool OnBeforeUnload(bool isReload)
    {
        if (_closingWithoutUnload) return true;
        var args = new CancelEventArgs();
        Raise(WillPreventUnload, args, "will-prevent-unload");
        return args.Cancel;
    }

    // UI thread. Electron's default moves the window: BrowserWindow::OnSetContentBounds.
    private void OnContentsBoundsChanged(cef_rect_t rect)
    {
        var args = new ContentBoundsUpdatedEventArgs(new Rectangle(rect.x, rect.y, rect.width, rect.height));
        Raise(ContentBoundsUpdated, args, "content-bounds-updated");
        if (!args.Cancel) BrowserWindow.FromWebContents(this)?.SetBounds(args.Bounds);
    }

    private void OnConsoleMessage(cef_log_severity_t severity, string message, string source, int line)
        => Raise(ConsoleMessage, new ConsoleMessageEventArgs(message, LevelOf(severity), line, source, null), "console-message");

    private void OnStatusMessage(string url) => Raise(UpdateTargetUrl, url, "update-target-url");

    private void OnCursorChanged(CursorChange change)
    {
        var image = change.Bgra is { } bgra
            ? NativeImage.CreateFromBitmap(bgra, new NativeImageBitmapOptions { Width = change.Width, Height = change.Height })
            : null;
        Raise(CursorChanged, new CursorChangedEventArgs(CursorName(change.Type), image, change.Bgra is null ? 1 : change.Scale,
            change.Bgra is null ? Size.Empty : new Size(change.Width, change.Height), new Point(change.HotspotX, change.HotspotY)), "cursor-changed");
    }

    private void Raise(EventHandler? handler, EventArgs args, string name)
    {
        try { handler?.Invoke(this, args); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] A {name} handler failed: {e}"); }
    }

    // CEF's mapping back from its severity, which it made from Blink's level; Electron names Blink's verbose "debug".
    internal static ConsoleMessageLevel LevelOf(cef_log_severity_t severity) => severity switch
    {
        cef_log_severity_t.LOGSEVERITY_INFO => ConsoleMessageLevel.Info,
        cef_log_severity_t.LOGSEVERITY_WARNING => ConsoleMessageLevel.Warning,
        cef_log_severity_t.LOGSEVERITY_ERROR => ConsoleMessageLevel.Error,
        _ => ConsoleMessageLevel.Debug,
    };

    // Electron's CursorTypeToString; CEF's cursor types mirror Chromium's.
    internal static string CursorName(cef_cursor_type_t type) => type switch
    {
        cef_cursor_type_t.CT_POINTER => "pointer",
        cef_cursor_type_t.CT_CROSS => "crosshair",
        cef_cursor_type_t.CT_HAND => "hand",
        cef_cursor_type_t.CT_IBEAM => "text",
        cef_cursor_type_t.CT_WAIT => "wait",
        cef_cursor_type_t.CT_HELP => "help",
        cef_cursor_type_t.CT_EASTRESIZE => "e-resize",
        cef_cursor_type_t.CT_NORTHRESIZE => "n-resize",
        cef_cursor_type_t.CT_NORTHEASTRESIZE => "ne-resize",
        cef_cursor_type_t.CT_NORTHWESTRESIZE => "nw-resize",
        cef_cursor_type_t.CT_SOUTHRESIZE => "s-resize",
        cef_cursor_type_t.CT_SOUTHEASTRESIZE => "se-resize",
        cef_cursor_type_t.CT_SOUTHWESTRESIZE => "sw-resize",
        cef_cursor_type_t.CT_WESTRESIZE => "w-resize",
        cef_cursor_type_t.CT_NORTHSOUTHRESIZE => "ns-resize",
        cef_cursor_type_t.CT_EASTWESTRESIZE => "ew-resize",
        cef_cursor_type_t.CT_NORTHEASTSOUTHWESTRESIZE => "nesw-resize",
        cef_cursor_type_t.CT_NORTHWESTSOUTHEASTRESIZE => "nwse-resize",
        cef_cursor_type_t.CT_COLUMNRESIZE => "col-resize",
        cef_cursor_type_t.CT_ROWRESIZE => "row-resize",
        cef_cursor_type_t.CT_MIDDLEPANNING => "m-panning",
        cef_cursor_type_t.CT_MIDDLE_PANNING_VERTICAL => "m-panning-vertical",
        cef_cursor_type_t.CT_MIDDLE_PANNING_HORIZONTAL => "m-panning-horizontal",
        cef_cursor_type_t.CT_EASTPANNING => "e-panning",
        cef_cursor_type_t.CT_NORTHPANNING => "n-panning",
        cef_cursor_type_t.CT_NORTHEASTPANNING => "ne-panning",
        cef_cursor_type_t.CT_NORTHWESTPANNING => "nw-panning",
        cef_cursor_type_t.CT_SOUTHPANNING => "s-panning",
        cef_cursor_type_t.CT_SOUTHEASTPANNING => "se-panning",
        cef_cursor_type_t.CT_SOUTHWESTPANNING => "sw-panning",
        cef_cursor_type_t.CT_WESTPANNING => "w-panning",
        cef_cursor_type_t.CT_MOVE => "move",
        cef_cursor_type_t.CT_VERTICALTEXT => "vertical-text",
        cef_cursor_type_t.CT_CELL => "cell",
        cef_cursor_type_t.CT_CONTEXTMENU => "context-menu",
        cef_cursor_type_t.CT_ALIAS => "alias",
        cef_cursor_type_t.CT_PROGRESS => "progress",
        cef_cursor_type_t.CT_NODROP => "nodrop",
        cef_cursor_type_t.CT_COPY => "copy",
        cef_cursor_type_t.CT_NONE => "none",
        cef_cursor_type_t.CT_NOTALLOWED => "not-allowed",
        cef_cursor_type_t.CT_ZOOMIN => "zoom-in",
        cef_cursor_type_t.CT_ZOOMOUT => "zoom-out",
        cef_cursor_type_t.CT_GRAB => "grab",
        cef_cursor_type_t.CT_GRABBING => "grabbing",
        cef_cursor_type_t.CT_CUSTOM => "custom",
        cef_cursor_type_t.CT_DND_NONE => "drag-drop-none",
        cef_cursor_type_t.CT_DND_MOVE => "drag-drop-move",
        cef_cursor_type_t.CT_DND_COPY => "drag-drop-copy",
        cef_cursor_type_t.CT_DND_LINK => "drag-drop-link",
        _ => "default",
    };
}
