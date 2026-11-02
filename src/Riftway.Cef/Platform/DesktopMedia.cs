using System.Globalization;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>A capture source as webrtc's <c>DesktopCapturer::Source</c> reports it.</summary>
/// <param name="Id">The native id: a display or monitor id for a screen, the window handle for a window.</param>
/// <param name="Title">The window title; empty for a screen.</param>
/// <param name="DisplayId">For a screen, the matching <c>Display.Id</c> as Electron derives it; empty when not known.</param>
/// <param name="WindowId">The second id part: Chromium's own window id for its windows, else 0.</param>
public sealed record CaptureSource(long Id, string Title, string DisplayId = "", long WindowId = 0);

/// <summary>Pixels as Chromium's <c>DesktopFrame</c> holds them: BGRA rows, <paramref name="Width"/> × 4 bytes each.</summary>
public sealed record CaptureBitmap(int Width, int Height, byte[] Bgra);

/// <summary>A parsed <c>content::DesktopMediaID</c> of the screen or window type.</summary>
public readonly record struct DesktopMediaId(bool IsScreen, long Id, long WindowId, bool NativePickerSession = false);

/// <summary>A window as macOS lists it, from ScreenCaptureKit's <c>SCWindow</c> or a CGWindowList dictionary.</summary>
/// <param name="Id">The CGWindowID.</param>
/// <param name="Title">The title, or null when the window has none.</param>
/// <param name="OwnerName">The owning application's name.</param>
/// <param name="Pid">The owning process.</param>
/// <param name="Layer">The window layer; 0 for ordinary windows.</param>
/// <param name="X">Frame left, in points.</param>
/// <param name="Y">Frame top, in points.</param>
/// <param name="Width">Frame width, in points.</param>
/// <param name="Height">Frame height, in points.</param>
/// <param name="OnScreen">CGWindowList's <c>kCGWindowIsOnscreen</c>.</param>
public sealed record MacWindowEntry(uint Id, string? Title, string OwnerName, int Pid, int Layer, double X, double Y, double Width, double Height, bool OnScreen = true);

/// <summary>Chromium's desktop media rules that need no OS: ids, names, thumbnail sizes, the per-OS window filters
/// and the display ids Electron gives screen sources.</summary>
public static class DesktopMedia
{
    /// <summary>Chromium's <c>DesktopMediaList</c> default thumbnail size, which Electron also defaults to.</summary>
    public const int DefaultThumbnailSize = 150;

    /// <summary><c>DesktopMediaID::ToString</c> for a screen or window.</summary>
    public static string FormatId(DesktopMediaId id)
        => $"{(id.IsScreen ? "screen" : "window")}:{id.Id.ToString(CultureInfo.InvariantCulture)}:{id.WindowId.ToString(CultureInfo.InvariantCulture)}{(id.NativePickerSession ? ":s" : "")}";

    /// <summary><c>DesktopMediaID::Parse</c> for screen and window ids; false where Chromium gives <c>TYPE_NONE</c>.</summary>
    public static bool TryParseId(string? text, out DesktopMediaId id)
    {
        id = default;
        if (text is null) return false;
        var parts = text.Split(':');
        if (parts.Length is not (3 or 4)) return false;
        for (var i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim(s_asciiWhitespace);
        bool screen;
        if (parts[0] == "screen") screen = true;
        else if (parts[0] == "window") screen = false;
        else return false;
        if (!TryParseInt64(parts[1], out var native) || !TryParseInt64(parts[2], out var window)) return false;
        if (parts.Length == 4 && parts[3] != "s") return false;
        id = new DesktopMediaId(screen, native, window, parts.Length == 4);
        return true;
    }

    private static readonly char[] s_asciiWhitespace = [' ', '\t', '\n', '\v', '\f', '\r'];

    // base::StringToInt64: an optional sign then decimal digits only; no whitespace, no overflow.
    private static bool TryParseInt64(string s, out long value)
    {
        value = 0;
        var i = 0;
        var negative = false;
        if (i < s.Length && s[i] == '-') { negative = true; i++; }
        else if (i < s.Length && s[i] == '+') i++;
        if (i == s.Length) return false;
        for (; i < s.Length; i++)
        {
            if (s[i] is < '0' or > '9') return false;
            var digit = s[i] - '0';
            try { value = checked(value * 10 + (negative ? -digit : digit)); }
            catch (OverflowException) { return false; }
        }
        return true;
    }

    /// <summary>The thumbnail size for a <paramref name="sourceWidth"/> × <paramref name="sourceHeight"/> frame:
    /// <c>media::ComputeLetterboxRegion</c>, the frame's aspect fitted into the box. (0, 0) for an empty frame.</summary>
    public static (int Width, int Height) LetterboxSize(int boxWidth, int boxHeight, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (0, 0);
        var x = (long)sourceWidth * boxHeight;
        var y = (long)sourceHeight * boxWidth;
        var (w, h) = y < x
            ? (boxWidth, RoundedDivision(y, sourceWidth))
            : (RoundedDivision(x, sourceHeight), boxHeight);
        // Rect::ClampToCenteredSize keeps the smaller of each side.
        return (Math.Min(w, boxWidth), Math.Min(h, boxHeight));
    }

    private static int RoundedDivision(long a, int b) => (int)((a + b / 2) / b);

    /// <summary>A screen source's name: Chromium's <c>IDS_DESKTOP_MEDIA_PICKER_SINGLE_SCREEN_NAME</c> when it is the
    /// only one, else <c>IDS_DESKTOP_MEDIA_PICKER_MULTIPLE_SCREEN_NAME</c> with its 1-based place.</summary>
    public static string ScreenName(int index, int count)
        => count > 1 ? $"Screen {(index + 1).ToString(CultureInfo.InvariantCulture)}" : "Entire screen";

    /// <summary>Scales a frame into <paramref name="boxWidth"/> × <paramref name="boxHeight"/> as Chromium's
    /// <c>ScaleDesktopFrame</c>: letterboxed size, bilinear, every alpha set to 255.</summary>
    public static CaptureBitmap? ScaleThumbnail(CaptureBitmap frame, int boxWidth, int boxHeight)
    {
        var (w, h) = LetterboxSize(boxWidth, boxHeight, frame.Width, frame.Height);
        if (w <= 0 || h <= 0) return null;
        var scaled = ScaleBilinear(frame, w, h);
        // Capturers leave alpha 0 on some platforms (crbug.com/41029106), so Chromium forces it opaque.
        for (var i = 3; i < scaled.Bgra.Length; i += 4) scaled.Bgra[i] = 0xff;
        return scaled;
    }

    /// <summary>libyuv's <c>ARGBScale</c> with <c>kFilterBilinear</c>: 16.16 steps starting half a step in, blended with
    /// 7-bit fractions.</summary>
    public static CaptureBitmap ScaleBilinear(CaptureBitmap src, int width, int height)
    {
        var dst = new byte[width * height * 4];
        if (src.Width <= 0 || src.Height <= 0) return new CaptureBitmap(width, height, dst);
        var (dx, x0) = Slope(src.Width, width);
        var (dy, y0) = Slope(src.Height, height);
        long maxY = (long)(src.Height - 1) << 16;
        var y = (long)y0;
        for (var row = 0; row < height; row++, y += dy)
        {
            var yy = Math.Clamp(y, 0, maxY);
            var yi = (int)(yy >> 16);
            var yf = (int)((yy >> 9) & 0x7f);
            var yi1 = Math.Min(yi + 1, src.Height - 1);
            var x = (long)x0;
            for (var col = 0; col < width; col++, x += dx)
            {
                var xx = Math.Clamp(x, 0, (long)(src.Width - 1) << 16);
                var xi = (int)(xx >> 16);
                var xf = (int)((xx >> 9) & 0x7f);
                var xi1 = Math.Min(xi + 1, src.Width - 1);
                for (var c = 0; c < 4; c++)
                {
                    var top = Blend(src.Bgra[(yi * src.Width + xi) * 4 + c], src.Bgra[(yi * src.Width + xi1) * 4 + c], xf);
                    var bottom = Blend(src.Bgra[(yi1 * src.Width + xi) * 4 + c], src.Bgra[(yi1 * src.Width + xi1) * 4 + c], xf);
                    dst[(row * width + col) * 4 + c] = (byte)Blend(top, bottom, yf);
                }
            }
        }
        return new CaptureBitmap(width, height, dst);
    }

    private static int Blend(int a, int b, int f) => (a * (128 - f) + b * f) >> 7;

    // libyuv ScaleSlope for bilinear: FixedDiv step; when shrinking, CENTERSTART(dx, -32768), else start at 0 with the
    // step stretched so the last sample lands on the last pixel.
    private static (int Step, int Start) Slope(int src, int dst)
    {
        if (dst <= src)
        {
            var step = (int)(((long)src << 16) / dst);
            return (step, (step >> 1) - 32768);
        }
        if (src > 1 && dst > 1)
            return ((int)(((long)(src - 1) << 16) / (dst - 1)), 0);
        return (0, 0);
    }

    /// <summary>ScreenCaptureKit's shareable-window rule (Chromium's <c>ThumbnailCapturerMac::IsShareable</c>): layer 0,
    /// and at least 40 points each way.</summary>
    public static bool IsMacShareable(MacWindowEntry window)
        => window.Layer == 0 && window.Height >= MacMinWindowSize && window.Width >= MacMinWindowSize;

    private const int MacMinWindowSize = 40;

    /// <summary>Chromium's <c>ThumbnailCapturerMac</c> window list, macOS 14.4 and later: shareable windows in
    /// <paramref name="zOrder"/>'s order, a window without a title kept only when it is its app's only window or
    /// covers a display, and then named after its app.</summary>
    /// <param name="windows">ScreenCaptureKit's on-screen, non-desktop windows.</param>
    /// <param name="zOrder">CGWindowList's on-screen window ids, front to back; null keeps ScreenCaptureKit's order.</param>
    /// <param name="displays">The displays' frames, for the full-screen exception.</param>
    public static IReadOnlyList<MacWindowEntry> MacScreenCaptureKitWindows(IReadOnlyList<MacWindowEntry> windows, IReadOnlyList<uint>? zOrder,
        IReadOnlyList<(double X, double Y, double Width, double Height)> displays)
    {
        var shareable = windows.Where(IsMacShareable).ToList();
        if (zOrder is not null)
            shareable = [.. zOrder.Select(id => shareable.Find(w => w.Id == id)).OfType<MacWindowEntry>()];
        var perApp = shareable.GroupBy(w => w.Pid).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<MacWindowEntry>();
        foreach (var window in shareable)
        {
            var untitled = string.IsNullOrEmpty(window.Title);
            if (untitled && perApp[window.Pid] > 1 && !displays.Any(d => d == (window.X, window.Y, window.Width, window.Height))) continue;
            result.Add(untitled ? window with { Title = window.OwnerName } : window);
        }
        return result;
    }

    /// <summary>webrtc's CGWindowList window list (<c>GetWindowList</c> in mac/window_list_utils.cc), used before
    /// macOS 14.4: layer 0, on screen, not the Window Server's red status dot; a window without a title is listed under
    /// its app's name only until the app shows a titled window, and only one per app.</summary>
    public static IReadOnlyList<MacWindowEntry> MacCoreGraphicsWindows(IReadOnlyList<MacWindowEntry> windows)
    {
        var sources = new List<MacWindowEntry?>();
        var untitledAt = new Dictionary<int, int>();
        foreach (var window in windows)
        {
            if (window.Id == 0 || window.Layer != 0 || !window.OnScreen) continue;
            if (window.Title == "StatusIndicator" && window.OwnerName == "Window Server") continue;
            var seen = untitledAt.TryGetValue(window.Pid, out var at);
            if (string.IsNullOrEmpty(window.Title))
            {
                if (window.OwnerName.Length == 0 || seen) continue;
                sources.Add(window with { Title = window.OwnerName });
                untitledAt[window.Pid] = sources.Count - 1;
            }
            else
            {
                sources.Add(window);
                // Once a titled window shows up, the app's untitled one goes; the entry stays so no other may join.
                if (seen) sources[at] = null;
            }
        }
        return [.. sources.OfType<MacWindowEntry>()];
    }

    /// <summary>The <c>SCStreamConfiguration</c> size Chromium asks for: the box's width or height, the other side
    /// rounded from the source's aspect, so no black bands are captured.</summary>
    public static (int Width, int Height) MacScreenshotSize(int boxWidth, int boxHeight, double sourceWidth, double sourceHeight)
    {
        var box = (float)boxWidth / boxHeight;
        var source = (float)(sourceWidth / sourceHeight);
        return source > box
            ? (boxWidth, (int)MathF.Round(boxWidth / source))
            : ((int)MathF.Round(boxHeight * source), boxHeight);
    }

    /// <summary>webrtc's X11 <c>IsDesktopElement</c>: with <c>_NET_WM_WINDOW_TYPE</c>, anything not marked normal; without
    /// it, the <c>gnome-panel</c> and <c>desktop_window</c> resource names.</summary>
    /// <param name="windowTypes">The <c>_NET_WM_WINDOW_TYPE</c> atoms, or null when the property is missing or empty.</param>
    /// <param name="normalType">The <c>_NET_WM_WINDOW_TYPE_NORMAL</c> atom.</param>
    /// <param name="resName"><c>WM_CLASS</c>'s resource name, or null without a class hint.</param>
    public static bool IsX11DesktopElement(IReadOnlyList<long>? windowTypes, long normalType, string? resName)
    {
        if (windowTypes is { Count: > 0 }) return !windowTypes.Contains(normalType);
        return resName is "gnome-panel" or "desktop_window";
    }

    /// <summary>Chromium's <c>ui::GetNativeWindowIcon</c>: the largest complete icon in a <c>_NET_WM_ICON</c> array of
    /// [width, height, ARGB × width × height] runs, as premultiplied BGRA; null without one.</summary>
    public static CaptureBitmap? PickX11Icon(IReadOnlyList<uint> data)
    {
        int width = 0, height = 0;
        ulong start = 0, i = 0, count = (ulong)data.Count;
        while (i + 1 < count)
        {
            // Chromium multiplies in uint32, compares as int, and wants the run strictly inside the array.
            var area = unchecked(data[(int)i] * data[(int)i + 1]);
            if ((int)area > width * height && i + 1 + area < count)
            {
                width = (int)data[(int)i];
                height = (int)data[(int)i + 1];
                start = i + 2;
            }
            i = unchecked(i + (ulong)(2L + (int)area));
        }
        if (width == 0 || height == 0) return null;
        var bgra = new byte[width * height * 4];
        for (var p = 0; p < width * height; p++)
        {
            var argb = data[(int)start + p];
            var a = argb >> 24;
            bgra[p * 4] = Premultiply(argb & 0xff, a);
            bgra[p * 4 + 1] = Premultiply((argb >> 8) & 0xff, a);
            bgra[p * 4 + 2] = Premultiply((argb >> 16) & 0xff, a);
            bgra[p * 4 + 3] = (byte)a;
        }
        return new CaptureBitmap(width, height, bgra);
    }

    private static byte Premultiply(uint c, uint a) => (byte)((c * a + 127) / 255);

    /// <summary>What Windows says about a top-level window, for webrtc's <c>GetWindowListHandler</c>.</summary>
    public readonly record struct WindowsWindowFacts(bool Visible, bool Iconic, bool Owned, long ExStyle, bool Responding,
        bool OwnedByCurrentProcess, string Title, string ClassName, bool OnCurrentDesktop, bool Cloaked);

    private const long WsExAppWindow = 0x00040000;

    /// <summary>webrtc's Windows window rules (<c>EnumerateCapturableWindows</c> with Chromium's flags: untitled,
    /// unresponsive and this process's windows left out), then <c>IsWindowVisibleOnCurrentDesktop</c>.</summary>
    public static bool IsWindowsCapturable(WindowsWindowFacts w)
    {
        if (!w.Visible || w.Iconic) return false;
        if (w.Owned && (w.ExStyle & WsExAppWindow) == 0) return false;
        if (!w.Responding) return false;
        if (w.OwnedByCurrentProcess) return false;
        if (w.Title.Length == 0) return false;
        if (w.ClassName.Length == 0 || w.ClassName is "Progman" or "Button") return false;
        return w.OnCurrentDesktop && !w.Cloaked;
    }

    /// <summary>Chromium's <c>CapturableCurrentProcessHwndCollector</c>, then its UI-thread title check: this process's
    /// visible, unminimized windows that show in the taskbar and have a title.</summary>
    public static bool IsWindowsOwnCapturable(WindowsWindowFacts w)
        => w.OwnedByCurrentProcess && w.Visible && !w.Iconic && !(w.Owned && (w.ExStyle & WsExAppWindow) == 0) && w.Title.Length > 0;

    /// <summary>The <c>GetWindowText</c> buffer for a title of <paramref name="length"/> characters: webrtc's 500 for
    /// other processes' windows; for this process's, the full title plus its terminator, as Chromium rereads those on
    /// the UI thread.</summary>
    public static int WindowsTitleBuffer(bool ownedByCurrentProcess, int length)
        => ownedByCurrentProcess ? length + 1 : WindowsTitleLength;

    private const int WindowsTitleLength = 500;

    /// <summary>Chromium's <c>MergeAndSortWindowSources</c>: both lists in <c>EnumWindows</c> z-order, the first list
    /// winning a tie; without a z-order, simply concatenated.</summary>
    public static IReadOnlyList<CaptureSource> MergeByZOrder(IReadOnlyList<CaptureSource> a, IReadOnlyList<CaptureSource> b, IReadOnlyList<long>? zOrder)
    {
        if (zOrder is null) return [.. a, .. b];
        var result = new List<CaptureSource>();
        foreach (var hwnd in zOrder)
        {
            if (a.FirstOrDefault(s => s.Id == hwnd) is { } fromA) result.Add(fromA);
            else if (b.FirstOrDefault(s => s.Id == hwnd) is { } fromB) result.Add(fromB);
        }
        return result;
    }

    /// <summary>Electron's X11 <c>display_id</c>: the output's EDID index id as <c>BuildDisplaysFromXRandRInfo</c> makes
    /// it (or the output's place when that is 0), cut to 32 bits as Electron's map stores it.</summary>
    /// <param name="edid">The output's <c>EDID</c> property; empty without one.</param>
    /// <param name="outputId">The RandR output id.</param>
    /// <param name="outputIndex">The output's place in the screen resources.</param>
    public static uint X11DisplayId(ReadOnlySpan<byte> edid, uint outputId, int outputIndex)
    {
        var id = outputId > 0xff ? 0 : EdidIndexBasedDisplayId(edid, (byte)outputId);
        if (id == 0) id = outputIndex;
        // Electron's MonitorAtomIdToDisplayId map is flat_map<int32_t, uint32_t>.
        return unchecked((uint)id);
    }

    /// <summary><c>EdidParser::GetIndexBasedDisplayId</c>: manufacturer id, the hash of the monitor name descriptor and
    /// the output index, packed by <c>GenerateDisplayID</c>.</summary>
    public static long EdidIndexBasedDisplayId(ReadOnlySpan<byte> edid, byte outputIndex)
    {
        var manufacturer = edid.Length >= 10 ? (ushort)(edid[8] << 8 | edid[9]) : (ushort)0;
        var name = EdidDisplayName(edid);
        var hash = name.Length == 0 ? 0u : SuperFastHash(Encoding.ASCII.GetBytes(name));
        return ((long)manufacturer << 40) | ((long)hash << 8) | outputIndex;
    }

    /// <summary>EdidParser's <c>display_name_</c>: the last monitor name descriptor (0xFC) among the four at byte 54,
    /// trailing whitespace trimmed, unprintable characters made spaces.</summary>
    public static string EdidDisplayName(ReadOnlySpan<byte> edid)
    {
        const int offset0 = 54, length = 18;
        var name = "";
        // Chromium stops parsing before the descriptors when the earlier fixed fields are cut short.
        if (edid.Length < 35) return name;
        for (var i = 0; i < 4; i++)
        {
            if (edid.Length < offset0 + (i + 1) * length) break;
            var o = offset0 + i * length;
            if (edid[o] == 0 && edid[o + 1] == 0 && edid[o + 2] == 0 && edid[o + 3] == 0xfc && edid[o + 4] == 0)
                name = Encoding.Latin1.GetString(edid.Slice(o + 5, length - 5)).TrimEnd(' ', '\t', '\n', '\v', '\f', '\r');
        }
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++) if (chars[i] is < ' ' or > '~') chars[i] = ' ';
        return new string(chars);
    }

    /// <summary>Chromium's <c>display::win::internal::DisplayInfo::DisplayIdFromMonitorInfo</c>: the hash of
    /// "adapterLow/adapterHigh/targetId" for the monitor's active path, else of its GDI device name.</summary>
    public static long WindowsDisplayId(string deviceName, (uint AdapterLow, int AdapterHigh, uint TargetId)? path)
    {
        var key = path is { } p
            ? $"{p.AdapterLow.ToString(CultureInfo.InvariantCulture)}/{p.AdapterHigh.ToString(CultureInfo.InvariantCulture)}/{p.TargetId.ToString(CultureInfo.InvariantCulture)}"
            : deviceName;
        return SuperFastHash(Encoding.UTF8.GetBytes(key));
    }

    /// <summary>Paul Hsieh's SuperFastHash, Chromium's <c>base::PersistentHash</c>.</summary>
    public static uint SuperFastHash(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        var hash = (uint)data.Length;
        var rem = data.Length & 3;
        var i = 0;
        for (var n = data.Length >> 2; n > 0; n--, i += 4)
        {
            hash += Get16(data, i);
            var tmp = (Get16(data, i + 2) << 11) ^ hash;
            hash = (hash << 16) ^ tmp;
            hash += hash >> 11;
        }
        switch (rem)
        {
            case 3:
                hash += Get16(data, i);
                hash ^= hash << 16;
                hash ^= (uint)(sbyte)data[i + 2] << 18;
                hash += hash >> 11;
                break;
            case 2:
                hash += Get16(data, i);
                hash ^= hash << 11;
                hash += hash >> 17;
                break;
            case 1:
                hash += (uint)(sbyte)data[i];
                hash ^= hash << 10;
                hash += hash >> 1;
                break;
        }
        hash ^= hash << 3;
        hash += hash >> 5;
        hash ^= hash << 4;
        hash += hash >> 17;
        hash ^= hash << 25;
        hash += hash >> 6;
        return hash;
    }

    private static uint Get16(ReadOnlySpan<byte> d, int i) => (uint)(d[i] | d[i + 1] << 8);
}
