namespace Oriel;

/// <summary>Electron's names for <c>WM_APPCOMMAND</c> commands (<c>AppCommandToString</c> in
/// <c>native_window_views_win.cc</c>, v44.5.1).</summary>
internal static class AppCommands
{
    /// <summary>The command in a <c>WM_APPCOMMAND</c> lParam: <c>GET_APPCOMMAND_LPARAM</c>.</summary>
    internal static int FromLParam(nint lParam) => (short)((int)((lParam >> 16) & 0xFFFF) & ~0xF000);

    /// <summary>Electron's name for an <c>APPCOMMAND_*</c> value; <c>unknown</c> for any other.</summary>
    internal static string Name(int command) => command switch
    {
        1 => "browser-backward",
        2 => "browser-forward",
        3 => "browser-refresh",
        4 => "browser-stop",
        5 => "browser-search",
        6 => "browser-favorites",
        7 => "browser-home",
        8 => "volume-mute",
        9 => "volume-down",
        10 => "volume-up",
        11 => "media-nexttrack",
        12 => "media-previoustrack",
        13 => "media-stop",
        14 => "media-play-pause",
        15 => "launch-mail",
        16 => "launch-media-select",
        17 => "launch-app1",
        18 => "launch-app2",
        19 => "bass-down",
        20 => "bass-boost",
        21 => "bass-up",
        22 => "treble-down",
        23 => "treble-up",
        24 => "microphone-volume-mute",
        25 => "microphone-volume-down",
        26 => "microphone-volume-up",
        27 => "help",
        28 => "find",
        29 => "new",
        30 => "open",
        31 => "close",
        32 => "save",
        33 => "print",
        34 => "undo",
        35 => "redo",
        36 => "copy",
        37 => "cut",
        38 => "paste",
        39 => "reply-to-mail",
        40 => "forward-mail",
        41 => "send-mail",
        42 => "spell-check",
        43 => "dictate-or-command-control-toggle",
        44 => "mic-on-off-toggle",
        45 => "correction-list",
        46 => "media-play",
        47 => "media-pause",
        48 => "media-record",
        49 => "media-fast-forward",
        50 => "media-rewind",
        51 => "media-channel-up",
        52 => "media-channel-down",
        53 => "delete",
        _ => "unknown",
    };
}

/// <summary>Electron's reasons for <c>WM_QUERYENDSESSION</c> and <c>WM_ENDSESSION</c> (<c>EndSessionToStringVec</c>).</summary>
internal static class EndSessionReasons
{
    private const long CloseApp = 0x1, Critical = 0x40000000, Logoff = 0x80000000;

    /// <summary>The reasons in the message's lParam: <c>shutdown</c> alone for 0, else <c>close-app</c>,
    /// <c>critical</c> and <c>logoff</c> in that order, for each <c>ENDSESSION_*</c> flag set.</summary>
    internal static IReadOnlyList<string> From(nint lParam)
    {
        var flags = (long)lParam & 0xFFFFFFFF;
        if (flags == 0) return ["shutdown"];
        var reasons = new List<string>();
        if ((flags & CloseApp) != 0) reasons.Add("close-app");
        if ((flags & Critical) != 0) reasons.Add("critical");
        if ((flags & Logoff) != 0) reasons.Add("logoff");
        return reasons;
    }
}

/// <summary>A rectangle by its edges, in pixels: Win32's <c>RECT</c>.</summary>
internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
}

/// <summary>Chromium's aspect-ratio sizing, which Electron's <c>setAspectRatio</c> uses on Windows.</summary>
internal static class AspectRatioSizing
{
    /// <summary>The ratio Chromium keeps: Electron scales <c>SizeF(ratio, 1)</c> by 100 and Chromium divides the two
    /// floats (<c>NativeWindowViews::SetAspectRatio</c>, <c>DesktopWindowTreeHostWin::SetAspectRatio</c>).</summary>
    internal static float Ratio(double aspectRatio) => (float)aspectRatio * 100f / (1f * 100f);

    /// <summary><paramref name="rect"/> resized to <paramref name="ratio"/> from the <c>WMSZ_*</c> edge being dragged,
    /// within the size limits: <c>gfx::SizeRectToAspectRatioWithExcludedMargin</c>.</summary>
    internal static PixelRect Fit(int edge, float ratio, (int Width, int Height) minimum, (int Width, int Height)? maximum,
        (int Width, int Height) excluded, PixelRect rect)
    {
        var width = Math.Max(0, rect.Width - excluded.Width);
        var height = Math.Max(0, rect.Height - excluded.Height);
        (int Width, int Height)? max = maximum is { } m ? (Math.Max(0, m.Width - excluded.Width), Math.Max(0, m.Height - excluded.Height)) : null;
        var minWidth = Math.Max(0, minimum.Width - excluded.Width);
        var minHeight = Math.Max(0, minimum.Height - excluded.Height);

        if (IsHorizontal(edge))
        {
            height = Round(width / ratio);
            if (minHeight > height || (max is { } mx && height > mx.Height))
            {
                height = max is { } limit ? Math.Clamp(height, minHeight, Math.Max(minHeight, limit.Height)) : minHeight;
                width = Round(height * ratio);
            }
        }
        else
        {
            width = Round(height * ratio);
            if (minWidth > width || (max is { } mx && width > mx.Width))
            {
                width = max is { } limit ? Math.Clamp(width, minWidth, Math.Max(minWidth, limit.Width)) : minWidth;
                height = Round(width / ratio);
            }
        }
        if (max is { } cap)
        {
            width = Math.Min(width, cap.Width);
            height = Math.Min(height, cap.Height);
        }
        width = Math.Max(width, minWidth) + excluded.Width;
        height = Math.Max(height, minHeight) + excluded.Height;

        var (left, top, right, bottom) = (rect.Left, rect.Top, rect.Right, rect.Bottom);
        switch (edge)
        {
            case Right or Bottom or BottomRight:
                right = left + width;
                bottom = top + height;
                break;
            case Top:
                right = left + width;
                top = bottom - height;
                break;
            case Left or TopLeft:
                left = right - width;
                top = bottom - height;
                break;
            case TopRight:
                right = left + width;
                top = bottom - height;
                break;
            case BottomLeft:
                left = right - width;
                bottom = top + height;
                break;
        }
        return new PixelRect(left, top, right, bottom);
    }

    /// <summary>Whether <paramref name="edge"/> is a <c>WMSZ_*</c> value Chromium sizes for; it ignores the rest.</summary>
    internal static bool IsEdge(int edge) => edge is >= Left and <= BottomRight;

    // WMSZ_* values.
    internal const int Left = 1, Right = 2, Top = 3, TopLeft = 4, TopRight = 5, Bottom = 6, BottomLeft = 7, BottomRight = 8;

    // The width decides for these edges; the height for the others.
    private static bool IsHorizontal(int edge) => edge is Left or Right or TopLeft or BottomLeft;

    // base::ClampRound: std::round, saturated to int.
    private static int Round(float value)
    {
        var rounded = MathF.Round(value, MidpointRounding.AwayFromZero);
        return float.IsNaN(rounded) ? 0 : rounded >= int.MaxValue ? int.MaxValue : rounded <= int.MinValue ? int.MinValue : (int)rounded;
    }
}

/// <summary>Chromium's window shape on Windows (<c>DesktopWindowTreeHostWin::SetShape</c>).</summary>
internal static class WindowShape
{
    /// <summary>The shape's rectangles in pixels: above scale 1 each edge is scaled and rounded outwards
    /// (<c>SkRect::roundOut</c>); at or below 1 they are taken as they are.</summary>
    internal static PixelRect[] ToPixels(IReadOnlyList<Rectangle> rects, double scale)
    {
        var s = (float)scale;
        return [.. rects.Select(r => s > 1f
            ? new PixelRect((int)MathF.Floor(r.X * s), (int)MathF.Floor(r.Y * s), (int)MathF.Ceiling((r.X + r.Width) * s), (int)MathF.Ceiling((r.Y + r.Height) * s))
            : new PixelRect(r.X, r.Y, r.X + r.Width, r.Y + r.Height))];
    }
}

/// <summary>Chromium's <c>DesktopMediaID</c> strings for windows.</summary>
internal static class MediaSourceId
{
    /// <summary>A window's id: <c>window:</c><i>HWND</i><c>:</c><i>aura id</i>; Oriel has no aura id, so 0, Chromium's null id.</summary>
    internal static string ForWindow(nint handle) => FormattableString.Invariant($"window:{(long)handle}:0");

    /// <summary>The native window id in a <c>window:</c> media source id, or null when <c>DesktopMediaID::Parse</c>
    /// would not read it as a window.</summary>
    internal static long? ParseWindow(string sourceId)
    {
        var parts = sourceId.Split(':').Select(p => p.Trim()).ToArray();
        if (parts.Length is not (3 or 4) || parts[0] != "window") return null;
        if (!TryInt64(parts[1], out var id) || !TryInt64(parts[2], out _)) return null;
        if (parts.Length == 4 && parts[3] != "s") return null;
        return id;
    }

    // base::StringToInt64: an optional sign and decimal digits only.
    private static bool TryInt64(string text, out long value)
        => long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value);
}
