using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Riftway.Cef.Interop;

namespace Riftway.Cef.Handlers;

/// <summary>A frame of a windowless browser's view: BGRA pixels, top-down, <c>Width * 4</c> bytes a row, valid only
/// during the callback; the dirty rectangles in view pixels.</summary>
public readonly unsafe ref struct PaintedFrame
{
    internal PaintedFrame(void* buffer, int width, int height, ReadOnlySpan<cef_rect_t> dirty)
    {
        Pixels = new ReadOnlySpan<byte>(buffer, checked(width * height * 4));
        Width = width;
        Height = height;
        Dirty = dirty;
    }

    public ReadOnlySpan<byte> Pixels { get; }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlySpan<cef_rect_t> Dirty { get; }
}

/// <summary>Receives a <see cref="PaintedFrame"/>; the span is only valid during the call.</summary>
public delegate void PaintCallback(PaintedFrame frame);

/// <summary><c>cef_render_handler_t</c>: what a windowless browser renders into. Its view size and screen are asked
/// for, and each frame of its view handed over; popups (<c>&lt;select&gt;</c> lists) are not drawn.</summary>
public sealed unsafe class RenderHandler
{
    public RenderHandler()
    {
        Native = NativeObject.Create<cef_render_handler_t>(this);
        Native->get_view_rect = &GetViewRect;
        Native->get_screen_info = &GetScreenInfo;
        Native->on_paint = &OnPaint;
    }

    public cef_render_handler_t* Native { get; }

    /// <summary>The view's size in DIPs; CEF needs at least 1x1. Asked on CEF's UI thread.</summary>
    public Func<(int Width, int Height)> ViewSize { get; init; } = () => (800, 600);

    /// <summary>Device pixels per DIP; frames are the view's size times this.</summary>
    public Func<float> ScaleFactor { get; init; } = () => 1f;

    /// <summary>A frame of the view, on CEF's UI thread.</summary>
    public PaintCallback? Painted { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void GetViewRect(cef_render_handler_t* self, cef_browser_t* browser, cef_rect_t* rect)
    {
        var owner = NativeObject.Owner<RenderHandler>(self);
        var (width, height) = (800, 600);
        Guard.Run(() => (width, height) = owner.ViewSize());
        *rect = new cef_rect_t { x = 0, y = 0, width = Math.Max(1, width), height = Math.Max(1, height) };
        NativeObject.Release(browser);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetScreenInfo(cef_render_handler_t* self, cef_browser_t* browser, cef_screen_info_t* info)
    {
        var owner = NativeObject.Owner<RenderHandler>(self);
        var scale = 1f;
        Guard.Run(() => scale = owner.ScaleFactor());
        info->device_scale_factor = scale > 0 ? scale : 1f;
        NativeObject.Release(browser);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPaint(cef_render_handler_t* self, cef_browser_t* browser, cef_paint_element_type_t type, nuint dirtyCount,
        cef_rect_t* dirtyRects, void* buffer, int width, int height)
    {
        var owner = NativeObject.Owner<RenderHandler>(self);
        if (type == cef_paint_element_type_t.PET_VIEW && owner.Painted is { } callback && buffer is not null && width > 0 && height > 0)
        {
            try { callback(new PaintedFrame(buffer, width, height, new ReadOnlySpan<cef_rect_t>(dirtyRects, (int)dirtyCount))); }
            catch (Exception e) { Console.Error.WriteLine($"[Riftway] A paint handler failed: {e}"); }
        }
        NativeObject.Release(browser);
    }
}
