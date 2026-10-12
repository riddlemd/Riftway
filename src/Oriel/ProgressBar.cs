using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>How the taskbar shows a window's progress. Electron's <c>mode</c> for <c>setProgressBar</c>.</summary>
public enum ProgressBarMode
{
    /// <summary>A green bar at the given value.</summary>
    Normal,

    /// <summary>No bar.</summary>
    None,

    /// <summary>A cycling bar, ignoring the value.</summary>
    Indeterminate,

    /// <summary>A red bar at the given value.</summary>
    Error,

    /// <summary>A yellow bar at the given value.</summary>
    Paused,
}

/// <summary>Options for <see cref="BaseWindow.SetProgressBar"/>; Electron's <c>options</c> argument.</summary>
public sealed record ProgressBarOptions
{
    /// <summary>How the bar shows. Electron's <c>mode</c>.</summary>
    public ProgressBarMode Mode { get; init; } = ProgressBarMode.Normal;
}

internal static class ProgressBar
{
    /// <summary>Electron's Windows rule: above 1 or <see cref="ProgressBarMode.Indeterminate"/> cycles; below 0 or
    /// <see cref="ProgressBarMode.None"/> removes the bar; otherwise the mode's colour at the value as a percentage.</summary>
    internal static (TaskbarProgressState State, int? Percent) Map(double progress, ProgressBarMode mode)
    {
        if (progress > 1 || mode == ProgressBarMode.Indeterminate) return (TaskbarProgressState.Indeterminate, null);
        // NaN would cast to an arbitrary value; treat it as no progress.
        if (progress < 0 || double.IsNaN(progress) || mode == ProgressBarMode.None) return (TaskbarProgressState.None, null);
        var state = mode switch
        {
            ProgressBarMode.Error => TaskbarProgressState.Error,
            ProgressBarMode.Paused => TaskbarProgressState.Paused,
            _ => TaskbarProgressState.Normal,
        };
        return (state, (int)(progress * 100));
    }
}
