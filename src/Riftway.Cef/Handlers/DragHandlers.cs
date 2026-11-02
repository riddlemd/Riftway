using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Riftway.Cef.Interop;

namespace Riftway.Cef.Handlers;

/// <summary>A rectangle of the page that drags the window, or that is cut out of one; DIPs, relative to the page.</summary>
public readonly record struct DraggableRegion(int X, int Y, int Width, int Height, bool Draggable);

/// <summary><c>cef_drag_handler_t</c>: reports the page's <c>app-region: drag</c> and <c>no-drag</c> rectangles.</summary>
public sealed unsafe class DragHandler
{
    public DragHandler(Action<IReadOnlyList<DraggableRegion>> regionsChanged)
    {
        RegionsChanged = regionsChanged;
        Native = NativeObject.Create<cef_drag_handler_t>(this);
        Native->on_draggable_regions_changed = &OnDraggableRegionsChanged;
    }

    public cef_drag_handler_t* Native { get; }

    /// <summary>The main frame's regions changed; an empty list when the last one went away.</summary>
    public Action<IReadOnlyList<DraggableRegion>> RegionsChanged { get; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDraggableRegionsChanged(cef_drag_handler_t* self, cef_browser_t* browser, cef_frame_t* frame, nuint count, cef_draggable_region_t* regions)
    {
        var owner = NativeObject.Owner<DragHandler>(self);
        // Electron takes the regions of the main frame's document only.
        if (frame->is_main(frame) != 0)
        {
            var list = new DraggableRegion[(int)count];
            for (var i = 0; i < list.Length; i++)
            {
                var r = regions[i];
                list[i] = new DraggableRegion(r.bounds.x, r.bounds.y, r.bounds.width, r.bounds.height, r.draggable != 0);
            }
            Guard.Run(() => owner.RegionsChanged(list));
        }
        NativeObject.Release(frame);
        NativeObject.Release(browser);
    }
}
