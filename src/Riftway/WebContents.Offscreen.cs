using System.Runtime.InteropServices;
using Riftway.Cef;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway;

/// <summary>Offscreen rendering: a page that paints into <see cref="WebContents.Paint"/> instead of a window. Electron's
/// <c>webPreferences.offscreen</c>.</summary>
public sealed unsafe partial class WebContents
{
    // Electron's OffScreenRenderWidgetHostView: 60 frames a second unless set, between 1 and 240.
    internal const int DefaultFrameRate = 60;

    private RenderHandler? _offscreenRender;
    private (int Width, int Height) _offscreenSize = (800, 600);
    private float _offscreenScale = 1f;
    private bool _painting = true;
    private int _frameRate = DefaultFrameRate;

    /// <summary>A frame of an offscreen page: the whole view as an image at its device scale, and the part that changed,
    /// in pixels. Raised on the UI thread. Electron's <c>paint</c>.</summary>
    public event EventHandler<PaintEventArgs>? Paint;

    /// <summary>Whether the page renders offscreen (<see cref="WebPreferences.Offscreen"/>). Electron's
    /// <c>webContents.isOffscreen()</c>.</summary>
    public bool IsOffscreen() => _preferences.Offscreen is not null;

    /// <summary>The render handler an offscreen page's client hands CEF; null for a windowed page.</summary>
    internal RenderHandler? OffscreenRender => _offscreenRender ??= _preferences.Offscreen is null ? null : new RenderHandler
    {
        ViewSize = () => _offscreenSize,
        ScaleFactor = () => _offscreenScale,
        Painted = OnOffscreenPaint,
    };

    /// <summary>The view an offscreen page renders, in DIPs; tells CEF when it changes. The device scale is
    /// <see cref="OffscreenOptions.DeviceScaleFactor"/>, else 1, whatever the display: Electron's
    /// OffScreenRenderWidgetHostView takes 1 for an unset factor.</summary>
    internal void SetOffscreenView(int width, int height)
    {
        if (!IsOffscreen()) return;
        CefThread.RunOnUi(() =>
        {
            var size = (Math.Max(1, width), Math.Max(1, height));
            var scale = (float)(_preferences.Offscreen!.DeviceScaleFactor ?? 1);
            if (size == _offscreenSize && scale == _offscreenScale) return;
            var rescaled = scale != _offscreenScale;
            (_offscreenSize, _offscreenScale) = (size, scale);
            if (_browser is null) return;
            var host = _browser->get_host(_browser);
            if (rescaled) host->notify_screen_info_changed(host);
            host->was_resized(host);
            NativeObject.Release(host);
        });
    }

    /// <summary>The settings a windowless browser starts with.</summary>
    internal int InitialFrameRate => _frameRate;

    private void OnOffscreenPaint(PaintedFrame frame)
    {
        if (Paint is null) return;
        var bgra = frame.Pixels.ToArray();
        // CEF reports each damaged rectangle; Electron's paint carries their union.
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var r in frame.Dirty)
        {
            (left, top) = (Math.Min(left, r.x), Math.Min(top, r.y));
            (right, bottom) = (Math.Max(right, r.x + r.width), Math.Max(bottom, r.y + r.height));
        }
        var dirty = left == int.MaxValue ? new Rectangle(0, 0, frame.Width, frame.Height) : new Rectangle(left, top, right - left, bottom - top);
        var image = NativeImage.CreateFromBitmap(bgra, new NativeImageBitmapOptions { Width = frame.Width, Height = frame.Height, ScaleFactor = _offscreenScale });
        Paint?.Invoke(this, new PaintEventArgs(dirty, image));
    }

    /// <summary>Starts an offscreen page painting again after <see cref="StopPainting"/>. Electron's
    /// <c>webContents.startPainting()</c>; does nothing for a windowed page.</summary>
    public void StartPainting() => SetPainting(true);

    /// <summary>Stops an offscreen page painting: no <see cref="Paint"/> until <see cref="StartPainting"/>; the page is
    /// hidden meanwhile, as Chromium hides a widget. Electron's <c>webContents.stopPainting()</c>.</summary>
    public void StopPainting() => SetPainting(false);

    /// <summary>Whether an offscreen page is painting; false for a windowed page. Electron's
    /// <c>webContents.isPainting()</c>.</summary>
    public bool IsPainting() => IsOffscreen() && CefThread.InvokeOnUi(() => _painting);

    private void SetPainting(bool painting)
    {
        if (!IsOffscreen()) return;
        CefThread.RunOnUi(() =>
        {
            if (_painting == painting) return;
            _painting = painting;
            if (_browser is null) return;
            var host = _browser->get_host(_browser);
            host->was_hidden(host, painting ? 0 : 1);
            NativeObject.Release(host);
        });
    }

    /// <summary>Sets an offscreen page's frames a second, clamped to 1 to 240. Electron's
    /// <c>webContents.setFrameRate()</c>; does nothing for a windowed page.</summary>
    public void SetFrameRate(int fps)
    {
        if (!IsOffscreen()) return;
        var rate = Math.Clamp(fps, 1, 240);
        CefThread.RunOnUi(() =>
        {
            _frameRate = rate;
            if (_browser is null) return;
            var host = _browser->get_host(_browser);
            host->set_windowless_frame_rate(host, rate);
            NativeObject.Release(host);
        });
    }

    /// <summary>An offscreen page's frames a second; 0 for a windowed page. Electron's <c>webContents.getFrameRate()</c>.</summary>
    public int GetFrameRate() => IsOffscreen() ? CefThread.InvokeOnUi(() => _frameRate) : 0;

    /// <summary>An offscreen page's frames a second, as <see cref="GetFrameRate"/> and <see cref="SetFrameRate"/>.
    /// Electron's <c>webContents.frameRate</c>.</summary>
    public int FrameRate
    {
        get => GetFrameRate();
        set => SetFrameRate(value);
    }

    /// <summary>Repaints an offscreen page whole, raising <see cref="Paint"/> with the whole view dirty. Electron's
    /// <c>webContents.invalidate()</c>; a windowed page is not repainted (drift C63).</summary>
    public void Invalidate()
    {
        if (!IsOffscreen()) return;
        WithHost(host => host->invalidate(host, cef_paint_element_type_t.PET_VIEW));
    }
}

/// <summary>A frame of an offscreen page: Electron's <c>paint</c> arguments.</summary>
/// <param name="DirtyRect">The part of the frame that changed, in pixels; Electron's <c>dirtyRect</c>.</param>
/// <param name="Image">The whole view, at the page's device scale; Electron's <c>image</c>.</param>
public sealed record PaintEventArgs(Rectangle DirtyRect, NativeImage Image);

/// <summary>A page rendered offscreen, into <see cref="WebContents.Paint"/>: Electron's <c>webPreferences.offscreen</c>
/// object.</summary>
public sealed record OffscreenOptions
{
    /// <summary>Frames as GPU shared textures. Not supported: <c>true</c> throws (drift C63). Electron's
    /// <c>useSharedTexture</c>.</summary>
    public bool UseSharedTexture { get; init; }

    /// <summary>The device scale the page renders at; null renders at 1, whatever the display, as Electron. Electron's
    /// <c>deviceScaleFactor</c>.</summary>
    public double? DeviceScaleFactor { get; init; }
}
