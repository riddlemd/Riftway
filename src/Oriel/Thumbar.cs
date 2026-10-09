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

/// <summary>One of the seven thumbnail toolbar slots: the button there (null for a hidden placeholder), its
/// <c>THBF_</c> flags and its <c>THB_</c> mask.</summary>
internal readonly record struct ThumbarSlot(uint Id, ThumbarButton? Button, uint Flags, uint Mask);

/// <summary>What <see cref="Thumbar.Plan"/> makes of a button list: the slots, and the index of the first button whose
/// flags Electron would reject, if any.</summary>
internal sealed record ThumbarPlan(ThumbarSlot[] Slots, int? InvalidAt);

internal static class Thumbar
{
    /// <summary>Win32 adds a thumbnail toolbar once, with at most this many buttons.</summary>
    internal const int MaxButtons = 7;

    /// <summary>Electron's <c>kButtonIdBase</c>: slot <c>i</c> has id <c>40001 + i</c>.</summary>
    internal const int ButtonIdBase = 40001;

    internal const uint MaskIcon = 0x2, MaskTooltip = 0x4, MaskFlags = 0x8;

    private const uint KnownFlags = 0x1F;

    /// <summary>Electron's <c>TaskbarHost::SetThumbarButtons</c>: all seven slots are always present, the ones without
    /// a button hidden, so later calls can update them; a button's icon and tooltip are sent only when it has them,
    /// so an update without one keeps what the shell shows. Null for more than seven buttons.</summary>
    internal static ThumbarPlan? Plan(IReadOnlyList<ThumbarButton> buttons)
    {
        if (buttons.Count > MaxButtons) return null;
        var slots = new ThumbarSlot[MaxButtons];
        int? invalidAt = null;
        for (var i = 0; i < MaxButtons; i++)
        {
            var id = (uint)(ButtonIdBase + i);
            if (i >= buttons.Count)
            {
                slots[i] = new ThumbarSlot(id, null, (uint)ThumbarButtonFlags.Hidden, MaskFlags);
                continue;
            }
            var b = buttons[i];
            // GetThumbarButtonFlags rejects a flag it does not know; the enum's undefined bits are the C# form of one.
            if (((uint)b.Flags & ~KnownFlags) != 0) invalidAt ??= i;
            var mask = MaskFlags | (b.Icon.IsEmpty() ? 0 : MaskIcon) | (string.IsNullOrEmpty(b.Tooltip) ? 0 : MaskTooltip);
            slots[i] = new ThumbarSlot(id, b, (uint)b.Flags, mask);
        }
        return new ThumbarPlan(slots, invalidAt);
    }
}
