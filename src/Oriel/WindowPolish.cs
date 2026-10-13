using System.ComponentModel;

namespace Oriel;

/// <summary>The window edge or corner the user drags. Electron's <c>details.edge</c> for <c>will-resize</c>.</summary>
public enum ResizeEdge
{
    /// <summary>Electron's <c>bottom-right</c>, its fallback.</summary>
    BottomRight,

    /// <summary>Electron's <c>top</c>.</summary>
    Top,

    /// <summary>Electron's <c>bottom</c>.</summary>
    Bottom,

    /// <summary>Electron's <c>left</c>.</summary>
    Left,

    /// <summary>Electron's <c>right</c>.</summary>
    Right,

    /// <summary>Electron's <c>top-left</c>.</summary>
    TopLeft,

    /// <summary>Electron's <c>top-right</c>.</summary>
    TopRight,

    /// <summary>Electron's <c>bottom-left</c>.</summary>
    BottomLeft,
}

/// <summary>Arguments for <see cref="BaseWindow.WillResize"/>; cancel to keep the current size.</summary>
public sealed class WillResizeEventArgs(Rectangle newBounds, ResizeEdge edge) : CancelEventArgs
{
    /// <summary>The bounds the window is about to take, in DIPs. Electron's <c>newBounds</c>.</summary>
    public Rectangle NewBounds { get; } = newBounds;

    /// <summary>The edge being dragged. Electron's <c>details.edge</c>.</summary>
    public ResizeEdge Edge { get; } = edge;
}

/// <summary>Arguments for <see cref="BaseWindow.WillMove"/>; cancel to keep the window where it is.</summary>
public sealed class WillMoveEventArgs(Rectangle newBounds) : CancelEventArgs
{
    /// <summary>The bounds the window is about to take, in DIPs. Electron's <c>newBounds</c>.</summary>
    public Rectangle NewBounds { get; } = newBounds;
}

/// <summary>Options for <see cref="BaseWindow.SetIgnoreMouseEvents"/>; Electron's <c>options</c>.</summary>
public sealed record IgnoreMouseEventsOptions
{
    /// <summary>Still deliver mouse moves to the page while ignoring clicks; applies only when ignoring. Windows only for
    /// now. Electron's <c>forward</c>.</summary>
    public bool Forward { get; init; }
}

internal static class WindowPolish
{
    /// <summary>Electron's <c>GetWindowResizeEdge</c>: Win32's <c>WMSZ_</c> codes, anything else bottom-right.</summary>
    internal static ResizeEdge EdgeFromWmsz(int wmsz) => wmsz switch
    {
        1 => ResizeEdge.Left,
        2 => ResizeEdge.Right,
        3 => ResizeEdge.Top,
        4 => ResizeEdge.TopLeft,
        5 => ResizeEdge.TopRight,
        6 => ResizeEdge.Bottom,
        7 => ResizeEdge.BottomLeft,
        _ => ResizeEdge.BottomRight,
    };

    /// <summary>Opacity clamped to 0–1, as Electron keeps it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The opacity is NaN.</exception>
    internal static double ClampOpacity(double opacity)
        => double.IsNaN(opacity) ? throw new ArgumentOutOfRangeException(nameof(opacity), "Opacity is NaN.") : Math.Clamp(opacity, 0, 1);

    /// <summary>Electron's Windows layered alpha: the clamped opacity times 255, truncated.</summary>
    internal static byte ToAlpha(double opacity) => (byte)(ClampOpacity(opacity) * 255);
}
