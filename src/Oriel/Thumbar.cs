namespace Oriel;

/// <summary>How a thumbnail toolbar button looks and behaves; the values are Win32's <c>THBF_</c> flags.
/// Electron's <c>flags</c>.</summary>
[Flags]
public enum ThumbarButtonFlags
{
    /// <summary>Active and clickable. Electron's <c>enabled</c>, the default.</summary>
    Enabled = 0,

    /// <summary>Shown greyed out. Electron's <c>disabled</c>.</summary>
    Disabled = 1,

    /// <summary>A click closes the thumbnail. Electron's <c>dismissonclick</c>.</summary>
    DismissOnClick = 2,

    /// <summary>No border; only the icon. Electron's <c>nobackground</c>.</summary>
    NoBackground = 4,

    /// <summary>Not shown. Electron's <c>hidden</c>.</summary>
    Hidden = 8,

    /// <summary>Shown enabled but takes no clicks, as a notification. Electron's <c>noninteractive</c>.</summary>
    NonInteractive = 16,
}

/// <summary>A button in the window's taskbar thumbnail; Electron's <c>ThumbarButton</c>.</summary>
public sealed record ThumbarButton
{
    /// <summary>The icon; its 1x representation is used. Electron's <c>icon</c>.</summary>
    public required NativeImage Icon { get; init; }

    /// <summary>Runs on the UI thread when the button is clicked. Electron's <c>click</c>.</summary>
    public Action? Click { get; init; }

    /// <summary>The button's tooltip. Electron's <c>tooltip</c>.</summary>
    public string? Tooltip { get; init; }

    /// <summary>Electron's <c>flags</c>.</summary>
    public ThumbarButtonFlags Flags { get; init; }
}

internal static class Thumbar
{
    /// <summary>Win32 adds a thumbnail toolbar once, with at most this many buttons.</summary>
    internal const int MaxButtons = 7;

    /// <summary>Electron's Windows rule: all seven slots are always present, the ones without a button hidden, so
    /// later calls can update them. Null for more than seven buttons.</summary>
    internal static (int Slot, ThumbarButton? Button, uint Flags)[]? Plan(IReadOnlyList<ThumbarButton> buttons)
    {
        if (buttons.Count > MaxButtons) return null;
        var slots = new (int, ThumbarButton?, uint)[MaxButtons];
        for (var i = 0; i < MaxButtons; i++)
            slots[i] = i < buttons.Count ? (i, buttons[i], (uint)buttons[i].Flags) : (i, null, (uint)ThumbarButtonFlags.Hidden);
        return slots;
    }
}
