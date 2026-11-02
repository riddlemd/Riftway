using System.Text.Json.Nodes;
using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>What a named window keeps across restarts. <c>true</c> converts to both; <c>false</c> to null, which keeps
/// nothing. Electron's <c>WindowStatePersistence</c> (experimental there).</summary>
public sealed record WindowStatePersistence
{
    /// <summary>Whether the window's position and size are restored. Electron's <c>bounds</c>.</summary>
    public bool Bounds { get; init; } = true;

    /// <summary>Whether maximized, full screen and kiosk are restored. Electron's <c>displayMode</c>.</summary>
    public bool DisplayMode { get; init; } = true;

    /// <summary>Electron's boolean form: true keeps bounds and display mode, false nothing.</summary>
    public static implicit operator WindowStatePersistence?(bool enabled) => enabled ? new WindowStatePersistence() : null;
}

/// <summary>A window's entry under <c>windowStates</c> in <c>Local State</c>, as Electron's
/// <c>NativeWindow::SaveWindowState</c> writes it.</summary>
internal sealed record SavedWindowState(Rectangle Bounds, Rectangle WorkArea);

/// <summary>Electron's save and restore rules (<c>native_window.cc</c>), without the window.</summary>
internal static class WindowStateRules
{
    /// <summary>Electron's <c>IsNormal</c>: NativeWindow's three states, and NativeWindowMac's simple full screen.</summary>
    internal static bool IsNormal(bool minimized, bool maximized, bool fullScreen, bool simpleFullScreen)
        => !minimized && !maximized && !fullScreen && !simpleFullScreen;

    /// <summary>Electron's Views <c>Maximize</c> (Windows and Linux): a hidden window is shown maximized and raises
    /// <c>show</c>. macOS keeps CEF's maximize: there <c>show</c> comes from the occlusion change (W1).</summary>
    internal static bool MaximizeShows(bool wasVisible, bool macOS) => !wasVisible && !macOS;

    // Electron's kMinVisibleWidth / kMinVisibleHeight.
    internal const int MinVisible = 100;

    // Chromium's display::kDefaultDisplayId: the stand-in display when none is attached.
    internal const long DefaultDisplayId = 0xFF;

    internal static int Right(Rectangle r) => r.X + r.Width;

    internal static int Bottom(Rectangle r) => r.Y + r.Height;

    // gfx::Rect::IsEmpty, not Rectangle's all-zero test.
    internal static bool IsEmpty(Rectangle r) => r.Width == 0 || r.Height == 0;

    internal static bool Contains(Rectangle outer, Rectangle inner)
        => inner.X >= outer.X && Right(inner) <= Right(outer) && inner.Y >= outer.Y && Bottom(inner) <= Bottom(outer);

    /// <summary>gfx::Rect::AdjustToFit: shrinks to <paramref name="target"/>'s size, then slides inside it.</summary>
    internal static Rectangle AdjustToFit(Rectangle r, Rectangle target)
    {
        var (x, width) = AdjustAlongAxis(target.X, target.Width, r.X, r.Width);
        var (y, height) = AdjustAlongAxis(target.Y, target.Height, r.Y, r.Height);
        return new Rectangle(x, y, width, height);
    }

    private static (int Origin, int Size) AdjustAlongAxis(int dstOrigin, int dstSize, int origin, int size)
    {
        size = Math.Min(dstSize, size);
        origin = origin < dstOrigin ? dstOrigin : Math.Min(dstOrigin + dstSize, origin + size) - size;
        return (origin, size);
    }

    /// <summary>Electron's hasInvalidDisplay: the stand-in display, or one with no size.</summary>
    internal static bool IsInvalidDisplay(long id, Rectangle bounds) => id == DefaultDisplayId || IsEmpty(bounds);

    /// <summary>The display to restore onto: least Manhattan movement of the saved origin when fitted to its work
    /// area, the earliest on a tie; <paramref name="primary"/> when there are none.</summary>
    internal static int PickDisplay(Rectangle saved, IReadOnlyList<Rectangle> workAreas, int primary)
    {
        var picked = primary;
        var least = int.MaxValue;
        for (var i = 0; i < workAreas.Count; i++)
        {
            var fitted = AdjustToFit(saved, workAreas[i]);
            var displacement = Math.Abs(fitted.X - saved.X) + Math.Abs(fitted.Y - saved.Y);
            if (displacement < least)
            {
                least = displacement;
                picked = i;
            }
        }
        return picked;
    }

    /// <summary>Electron's NativeWindow::RestoreBounds; null when the saved size is zero.</summary>
    internal static Rectangle? RestoreBounds(Rectangle saved, Rectangle savedWorkArea, Rectangle workArea, bool mac)
    {
        if (saved.Width == 0 || saved.Height == 0) return null;
        var b = saved with { Width = Math.Max(MinVisible, saved.Width), Height = Math.Max(MinVisible, saved.Height) };
        if (b.Y < workArea.Y) b = b with { Y = workArea.Y };
        if (!IsEmpty(savedWorkArea) && savedWorkArea != workArea && !Contains(workArea, b)) b = AdjustToFit(b, workArea);
        if (mac) return AdjustToFit(b, workArea);

        var minY = workArea.Y + MinVisible - b.Height;
        var minX = workArea.X + MinVisible - b.Width;
        var maxY = Bottom(workArea) - MinVisible;
        var maxX = Right(workArea) - MinVisible;
        if (minX >= maxX || minY >= maxY) return AdjustToFit(b, workArea);
        return b with { X = Math.Clamp(b.X, minX, maxX), Y = Math.Clamp(b.Y, minY, maxY) };
    }

    /// <summary>The bounds to save: a maximized, minimized or full-screen window keeps the bounds already saved, so
    /// they come back when its display mode does not.</summary>
    internal static Rectangle BoundsToSave(Rectangle current, bool isNormal, JsonObject? existing)
        => !isNormal && existing is not null && ReadBounds(existing, "left", "top", "right", "bottom") is { } kept ? kept : current;

    internal static JsonObject ToJson(Rectangle bounds, bool maximized, bool fullscreen, bool kiosk, Rectangle workArea) => new()
    {
        ["left"] = bounds.X,
        ["top"] = bounds.Y,
        ["right"] = Right(bounds),
        ["bottom"] = Bottom(bounds),
        ["maximized"] = maximized,
        ["fullscreen"] = fullscreen,
        ["kiosk"] = kiosk,
        ["workAreaLeft"] = workArea.X,
        ["workAreaTop"] = workArea.Y,
        ["workAreaRight"] = Right(workArea),
        ["workAreaBottom"] = Bottom(workArea),
    };

    /// <summary>The bounds and work area of an entry; null when any of the eight is missing or not an integer
    /// (Electron's "corrupted values").</summary>
    internal static SavedWindowState? Parse(JsonObject entry)
        => ReadBounds(entry, "left", "top", "right", "bottom") is { } bounds
           && ReadBounds(entry, "workAreaLeft", "workAreaTop", "workAreaRight", "workAreaBottom") is { } workArea
            ? new SavedWindowState(bounds, workArea)
            : null;

    // base::DictValue::FindBool: only a boolean true counts.
    internal static bool Flag(JsonObject entry, string key)
        => entry[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static Rectangle? ReadBounds(JsonObject entry, string left, string top, string right, string bottom)
        => Int(entry, left) is { } l && Int(entry, top) is { } t && Int(entry, right) is { } r && Int(entry, bottom) is { } b
            ? new Rectangle(l, t, r - l, b - t)
            : null;

    private static int? Int(JsonObject entry, string key)
        => entry[key] is JsonValue value && value.TryGetValue<int>(out var n) ? n : null;
}

/// <summary>The <c>windowStates</c> dictionary in CEF's <c>Local State</c> (the sessionData directory), the file and
/// key Electron uses. UI thread only.</summary>
internal static unsafe class WindowStateStore
{
    internal const string Key = "windowStates";

    private static bool s_warned;

    /// <summary>Registers <c>windowStates</c> in <c>Local State</c>, as Electron's BrowserProcessImpl does.</summary>
    internal static void Register(cef_preferences_type_t type, cef_preference_registrar_t* registrar)
    {
        if (type != cef_preferences_type_t.CEF_PREFERENCES_TYPE_GLOBAL) return;
        var empty = Parse("{}");
        cef_string_utf16_t name = default;
        CefString.Set(&name, Key);
        registrar->add_preference(registrar, &name, empty);
        CefString.Clear(&name);
    }

    /// <summary>Every saved window, by name; empty when there are none.</summary>
    internal static JsonObject Read()
    {
        var manager = CefNative.cef_preference_manager_get_global();
        if (manager is null) return [];
        cef_string_utf16_t key = default;
        CefString.Set(&key, Key);
        var value = manager->get_preference(manager, &key);
        CefString.Clear(&key);
        NativeObject.Release(manager);
        if (value is null) return [];
        // cef_write_json takes the value's reference.
        var json = CefString.ReadUserFree(CefNative.cef_write_json(value, cef_json_writer_options_t.JSON_WRITER_DEFAULT));
        try { return JsonNode.Parse(json) as JsonObject ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    internal static void Write(JsonObject states)
    {
        var manager = CefNative.cef_preference_manager_get_global();
        if (manager is null) return;
        cef_string_utf16_t key = default, error = default;
        CefString.Set(&key, Key);
        var ok = manager->set_preference(manager, &key, Parse(states.ToJsonString()), &error) != 0;
        var message = CefString.Read(&error);
        CefString.Clear(&key);
        CefString.Clear(&error);
        NativeObject.Release(manager);
        if (!ok && !s_warned)
        {
            s_warned = true;
            Console.Error.WriteLine($"[Oriel] Window state not saved: {message}");
        }
    }

    // A new reference, which the CEF call it is passed to takes.
    private static cef_value_t* Parse(string json)
    {
        cef_string_utf16_t text = default;
        CefString.Set(&text, json);
        var value = CefNative.cef_parse_json(&text, cef_json_parser_options_t.JSON_PARSER_RFC);
        CefString.Clear(&text);
        return value;
    }
}
