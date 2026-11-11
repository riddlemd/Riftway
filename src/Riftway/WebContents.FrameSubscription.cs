using System.Text.Json;
using Riftway.Cef;

namespace Riftway;

/// <summary>Frame subscription: Electron's <c>beginFrameSubscription</c> over DevTools' screencast.</summary>
public sealed partial class WebContents
{
    private Action<NativeImage, Rectangle>? _frameSubscriber;

    /// <summary>Calls <paramref name="callback"/> with an image of the page each time it repaints, until
    /// <see cref="EndFrameSubscription"/>; a second call replaces the first subscriber. Electron's
    /// <c>webContents.beginFrameSubscription(callback)</c>.</summary>
    /// <remarks>Frames come from DevTools' screencast: whole PNG frames of the page at its device pixels, the dirty
    /// rectangle always the whole frame (drift C58). The callback runs on the UI thread.</remarks>
    public void BeginFrameSubscription(Action<NativeImage, Rectangle> callback) => BeginFrameSubscription(false, callback);

    /// <summary>As <see cref="BeginFrameSubscription(Action{NativeImage, Rectangle})"/>; Electron's
    /// <c>beginFrameSubscription(onlyDirty, callback)</c>. <paramref name="onlyDirty"/> is accepted, but every frame is
    /// whole (drift C58).</summary>
    public void BeginFrameSubscription(bool onlyDirty, Action<NativeImage, Rectangle> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _ = onlyDirty;
        CefThread.RunOnUi(() =>
        {
            var running = _frameSubscriber is not null;
            _frameSubscriber = callback;
            if (!running) _devTools.Send("Page.startScreencast", w => w.WriteString("format", "png"));
        });
    }

    /// <summary>Stops the frames <see cref="BeginFrameSubscription(Action{NativeImage, Rectangle})"/> started. Electron's
    /// <c>webContents.endFrameSubscription()</c>.</summary>
    public void EndFrameSubscription() => CefThread.RunOnUi(() =>
    {
        if (_frameSubscriber is null) return;
        _frameSubscriber = null;
        _devTools.Send("Page.stopScreencast");
    });

    // UI thread. A screencast frame: acknowledged at once, or DevTools sends no more.
    private void OnScreencastFrame(JsonElement parameters)
    {
        var session = parameters.GetProperty("sessionId").GetInt32();
        _devTools.Send("Page.screencastFrameAck", w => w.WriteNumber("sessionId", session));
        if (_frameSubscriber is not { } subscriber) return;
        NativeImage image;
        try { image = NativeImage.CreateFromBuffer(Convert.FromBase64String(parameters.GetProperty("data").GetString()!)); }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"[Riftway] A screencast frame could not be read: {e.Message}");
            return;
        }
        var size = image.GetSize();
        try { subscriber(image, new Rectangle(0, 0, size.Width, size.Height)); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] A frame subscriber failed: {e}"); }
    }
}
