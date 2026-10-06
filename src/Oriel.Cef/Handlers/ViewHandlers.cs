using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef.Handlers;

/// <summary>A panel and its new bounds, in its parent's coordinates.</summary>
public unsafe delegate void LayoutCallback(cef_view_t* view, cef_rect_t bounds);

/// <summary><c>cef_panel_delegate_t</c>: tells when a panel has been laid out, so its children can be placed.</summary>
public sealed unsafe class PanelDelegate
{
    public PanelDelegate()
    {
        Native = NativeObject.Create<cef_panel_delegate_t>(this);
        Native->@base.on_layout_changed = &OnLayoutChanged;
        Native->@base.on_theme_changed = &OnThemeChanged;
    }

    public cef_panel_delegate_t* Native { get; }

    /// <summary>UI thread, after the panel's layout manager has placed its children.</summary>
    public LayoutCallback? LayoutChanged { get; init; }

    /// <summary>UI thread: the theme reset the panel's colours.</summary>
    public Action? ThemeChanged { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLayoutChanged(cef_view_delegate_t* self, cef_view_t* view, cef_rect_t* bounds)
    {
        var owner = NativeObject.Owner<PanelDelegate>(self);
        var copy = *bounds;
        if (owner.LayoutChanged is { } callback) Guard.Run(() => callback(view, copy));
        NativeObject.Release(view);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnThemeChanged(cef_view_delegate_t* self, cef_view_t* view)
    {
        var owner = NativeObject.Owner<PanelDelegate>(self);
        if (owner.ThemeChanged is { } callback) Guard.Run(callback);
        NativeObject.Release(view);
    }
}
