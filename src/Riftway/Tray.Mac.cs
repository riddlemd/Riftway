using System.Runtime.Versioning;
using Riftway.Cef;
using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>The macOS-only parts of a tray icon: Electron's <c>TrayIconCocoa</c> setters and its own menu handling.</summary>
internal interface IMacTrayFeatures
{
    void SetPressedImage(NativeImage image);
    void SetTitle(string title, TrayTitleFontType? fontType);
    string GetTitle();
    bool IgnoreDoubleClickEvents { get; set; }
    void PopUpContextMenu(Menu? menu);
    void CloseContextMenu();
}

/// <summary>Electron's <c>TrayIconCocoa</c> and <c>StatusItemView</c>; the menu through Riftway's port of
/// <c>ElectronMenuController</c>. UI (main) thread.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacTrayBackend(Tray owner) : ITrayBackend, IMacTrayFeatures, IMacStatusItemHandler
{
    private nint _view;
    private MacMenuController? _controller;

    public bool PopsUpMenus => false;

    public bool IgnoreDoubleClickEvents { get; set; }

    public void Create(NativeImage image, Guid? guid)
    {
        _view = MacStatusItem.Create(this);
        SetImage(image);
        // base::Uuid::AsLowercaseString.
        if (guid is { } g) MacStatusItem.SetAutosaveName(_view, g.ToString("D"));
    }

    public void SetImage(NativeImage image) => MacStatusItem.SetImage(_view, Representations(image), image.IsTemplateImage());

    public void SetPressedImage(NativeImage image) => MacStatusItem.SetAlternateImage(_view, Representations(image), image.IsTemplateImage());

    public void SetToolTip(string toolTip) => MacStatusItem.SetToolTip(_view, toolTip);

    public void SetTitle(string title, TrayTitleFontType? fontType)
        => MacStatusItem.SetTitle(_view, MacTrayRules.TitleRuns(title), MacTrayRules.FontTypeCode(fontType), title.Length);

    public string GetTitle() => MacStatusItem.Title(_view);

    // Electron's TrayIconCocoa leaves balloons and focus to the base class, which does nothing.
    public void DisplayBalloon(DisplayBalloonOptions options) { }

    public void RemoveBalloon() { }

    public void Focus() { }

    public bool ActivateForMenu() => false;

    public void SetContextMenu(Menu? menu)
    {
        var old = _controller;
        _controller = menu is null ? null : MacApplicationMenu.CreatePopupMenu(menu);
        MacStatusItem.SetControllerMenu(_view, _controller?.NativeMenu ?? 0);
        if (old is not null) MacApplicationMenu.ReleasePopupMenu(old);
    }

    // Electron posts PopUpOnUI; -performClick: then runs the menu in a nested loop until it closes.
    public void PopUpContextMenu(Menu? menu)
    {
        var view = _view;
        CefThread.PostUi(() =>
        {
            if (!MacStatusItem.IsLive(view)) return;
            if (menu is not null)
            {
                var custom = MacApplicationMenu.CreatePopupMenu(menu);
                MacStatusItem.SetItemMenu(view, custom.NativeMenu);
                try { MacStatusItem.PerformClick(view); }
                finally { MacApplicationMenu.ReleasePopupMenu(custom); }
                // The menu's handlers may have destroyed the tray.
                if (!MacStatusItem.IsLive(view)) return;
                MacStatusItem.SetItemMenu(view, MacStatusItem.ControllerMenu(view));
                return;
            }
            if (_controller is { IsMenuOpen: false }) MacStatusItem.PerformClick(view);
        });
    }

    public void CloseContextMenu() => _controller?.Cancel(MacStatusItem.CancelTracking);

    public Rectangle GetBounds()
    {
        var (x, y, w, h, primary) = MacStatusItem.WindowFrame(_view);
        return MacTrayRules.ScreenRectFromNSRect(x, y, w, h, primary);
    }

    public void Destroy()
    {
        MacStatusItem.Remove(_view);
        _view = 0;
        if (_controller is { } c) MacApplicationMenu.ReleasePopupMenu(c);
        _controller = null;
    }

    public void Signal(MacStatusItemSignal signal, in MacStatusItemEvent e)
    {
        var keys = MacTrayRules.Keys(e.ModifierFlags);
        var location = MacTrayRules.ScreenPointFromNSPoint(e.X, e.Y, e.PrimaryHeight);
        var frame = MacTrayRules.ScreenRectFromNSRect(e.FrameX, e.FrameY, e.FrameWidth, e.FrameHeight, e.PrimaryHeight);
        switch (signal)
        {
            case MacStatusItemSignal.Click:
                var (click, doubleClick) = MacTrayRules.Clicks(e.ClickCount, IgnoreDoubleClickEvents);
                if (click) owner.RaiseMac(TrayEvent.Click, frame, location, keys);
                if (doubleClick) owner.RaiseMac(TrayEvent.DoubleClick, frame, default, keys);
                return;
            case MacStatusItemSignal.DragEnded:
                owner.RaiseMac(TrayEvent.DragEnd, default, default, default);
                if (MacTrayRules.PointInRect(e.X, e.Y, e.FrameX, e.FrameY, e.FrameWidth, e.FrameHeight)) owner.RaiseMac(TrayEvent.Drop, default, default, default);
                return;
            case MacStatusItemSignal.RightMouseUp:
                owner.RaiseMac(TrayEvent.RightClick, frame, default, keys);
                return;
        }
        if (MacTrayRules.EventOf(signal) is var trayEvent and not TrayEvent.None) owner.RaiseMac(trayEvent, default, location, keys);
    }

    public void DropFiles(string[] files) => owner.RaiseFilesDropped(files);

    public void DropText(string text) => owner.RaiseTextDropped(text);

    /// <summary>The status item view; for tests.</summary>
    internal nint View => _view;

    /// <summary>The context menu's controller; for tests.</summary>
    internal MacMenuController? Controller => _controller;

    private static List<(byte[] Png, double Scale)> Representations(NativeImage image)
        => [.. image.GetScaleFactors().Select(scale => (image.ToPng(scale), scale))];
}

internal static class MacTrayRules
{
    /// <summary>The events Electron's <c>StatusItemView</c> raises with only a location.</summary>
    internal static TrayEvent EventOf(MacStatusItemSignal signal) => signal switch
    {
        MacStatusItemSignal.MouseDown => TrayEvent.MouseDown,
        MacStatusItemSignal.MouseUp => TrayEvent.MouseUp,
        MacStatusItemSignal.MouseEntered => TrayEvent.MouseEnter,
        MacStatusItemSignal.MouseExited => TrayEvent.MouseLeave,
        MacStatusItemSignal.MouseMoved => TrayEvent.MouseMove,
        MacStatusItemSignal.DragEntered => TrayEvent.DragEnter,
        MacStatusItemSignal.DragExited => TrayEvent.DragLeave,
        _ => TrayEvent.None,
    };

    /// <summary><c>handleClickNotifications:</c>: one click is a click, two a double click, and ignoring double clicks
    /// makes every press a click.</summary>
    internal static (bool Click, bool DoubleClick) Clicks(long clickCount, bool ignoreDoubleClicks)
        => (clickCount == 1 || ignoreDoubleClicks, clickCount == 2 && !ignoreDoubleClicks);

    /// <summary><c>ui::EventFlagsFromModifiers</c> read by <c>CreateEventFromFlags</c>: Shift, Control, Option as Alt and
    /// Command as Meta.</summary>
    internal static (bool Shift, bool Ctrl, bool Alt, bool Meta) Keys(ulong modifierFlags)
        => ((modifierFlags & (1 << 17)) != 0, (modifierFlags & (1 << 18)) != 0, (modifierFlags & (1 << 19)) != 0, (modifierFlags & (1 << 20)) != 0);

    /// <summary><c>gfx::ScreenPointFromNSPoint</c>: y flipped against the primary display, both truncated to int.</summary>
    internal static Point ScreenPointFromNSPoint(double x, double y, double primaryHeight) => new((int)x, (int)(primaryHeight - y));

    /// <summary><c>gfx::ScreenRectFromNSRect</c>: the top edge flipped against the primary display, all truncated to int.</summary>
    internal static Rectangle ScreenRectFromNSRect(double x, double y, double width, double height, double primaryHeight)
        => new((int)x, (int)(primaryHeight - y - height), (int)width, (int)height);

    /// <summary><c>NSPointInRect</c>: minimum edges in, maximum edges out.</summary>
    internal static bool PointInRect(double px, double py, double x, double y, double width, double height)
        => px >= x && py >= y && px < x + width && py < y + height;

    /// <summary>The font type code <see cref="MacStatusItem.SetTitle"/> takes.</summary>
    internal static int FontTypeCode(TrayTitleFontType? fontType) => fontType switch
    {
        TrayTitleFontType.Monospaced => 1,
        TrayTitleFontType.MonospacedDigit => 2,
        _ => 0,
    };

    /// <summary>Electron's <c>NSString+ANSI</c>: a title without <c>ESC[</c> is one plain run; otherwise each
    /// <c>ESC[codes m text</c> part applies its codes to the running colours (<c>modifyAttributesForANSICodes:</c>) and
    /// appends its text, a part without <c>m</c> appends whole, and empty parts are skipped.</summary>
    internal static IReadOnlyList<MacTitleRun> TitleRuns(string title)
    {
        const string Escape = "\u001b[";
        if (!title.Contains(Escape, StringComparison.Ordinal)) return [new MacTitleRun(title, null, null)];
        var parts = title.Split(Escape);
        var runs = new List<MacTitleRun> { new(parts[0], null, null) };
        int? fg = null, bg = null;
        foreach (var part in parts.Skip(1))
        {
            if (part.Length == 0) continue;
            var sequence = part.Split('m');
            if (sequence.Length < 2)
            {
                runs.Add(new MacTitleRun(part, fg, bg));
                continue;
            }
            (fg, bg) = ApplyCodes(sequence[0], fg, bg);
            runs.Add(new MacTitleRun(string.Join('m', sequence.Skip(1)), fg, bg));
        }
        return runs;
    }

    /// <summary><c>modifyAttributesForANSICodes:</c>: bold starts off at each call and picks the other foreground
    /// palette; 0 clears, 39 and 49 drop the colour.</summary>
    internal static (int? Foreground, int? Background) ApplyCodes(string codes, int? fg, int? bg)
    {
        var bold = false;
        foreach (var codeString in codes.Split(';'))
        {
            var code = IntValue(codeString);
            switch (code)
            {
                case 0: (fg, bg) = (null, null); break;
                case 1 or 22: bold = code == 1; break;
                case >= 30 and <= 37: fg = bold ? s_boldForeground[code - 30] : s_foreground[code - 30]; break;
                case 39: fg = null; break;
                case >= 40 and <= 47: bg = s_background[code - 40]; break;
                case 49: bg = null; break;
            }
        }
        return (fg, bg);
    }

    private static readonly int[] s_foreground = [0x000000, 0xFF0000, 0x00FF00, 0xFFFF00, 0x5C5CFF, 0xFF00FF, 0x00FFFF, 0xFFFFFF];
    private static readonly int[] s_boldForeground = [0x7F7F7F, 0xCD0000, 0x00CD00, 0xCDCD00, 0x0000EE, 0xCD00CD, 0x00CDCD, 0xE5E5E5];
    // Electron's 45 passes "cd00cd" without '#', which ParseHexColorString rejects, leaving the colour uninitialised.
    private static readonly int[] s_background = [0x7F7F7F, 0xCD0000, 0x00CD00, 0xCDCD00, 0x0000EE, 0xCD00CD, 0x00CDCD, 0xE5E5E5];

    /// <summary><c>-[NSString intValue]</c>: leading whitespace, an optional sign, then digits up to the first other
    /// character; 0 when there are none, clamped to the int range.</summary>
    internal static int IntValue(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        var negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) negative = s[i++] == '-';
        long value = 0;
        for (; i < s.Length && s[i] is >= '0' and <= '9'; i++)
            value = Math.Min(value * 10 + (s[i] - '0'), (long)int.MaxValue + 1);
        value = negative ? -value : value;
        return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }
}
