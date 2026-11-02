using System.Runtime.InteropServices;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>The GTK 3 readings Chromium 154's <c>OsSettingsProviderGtk</c> makes when the portal gives no colour scheme:
/// dark when the theme's window background is dark, high contrast when the theme's name says so.</summary>
/// <remarks>Reads the GTK that Chromium's <c>GtkUi</c> loaded and initialised in this process, so its calls belong on
/// CEF's UI thread (GTK's main thread). Without that GTK, Chromium has no toolkit theme, and neither does this.</remarks>
public static unsafe partial class LinuxGtkTheme
{
    private const string LibGtk = "libgtk-3.so.0";
    private const string LibGObject = "libgobject-2.0.so.0";
    private const string LibCairo = "libcairo.so.2";
    private const nuint GTypeNone = 1 << 2, GTypeBoolean = 5 << 2, GTypeString = 16 << 2;
    private const int Side = 24;

    /// <summary>color_utils' <c>g_luminance_midpoint</c>.</summary>
    public const float LuminanceMidpoint = 0.211692036f;

    [StructLayout(LayoutKind.Sequential)]
    private struct GValue
    {
        public nuint Type;
        public long Data0;
        public long Data1;
    }

    /// <summary>Whether Chromium loaded GTK 3 and it has a default display. UI thread.</summary>
    public static bool IsLoaded
    {
        get
        {
            if (!GtkLibraryLoaded()) return false;
            try { return gtk_settings_get_default() != 0; }
            catch (DllNotFoundException) { return false; }
        }
    }

    /// <summary><c>gtk-theme-name</c>, or null. UI thread, GTK loaded.</summary>
    public static string? ThemeName()
    {
        var value = new GValue();
        _ = g_value_init(&value, GTypeString);
        fixed (byte* name = "gtk-theme-name\0"u8) g_object_get_property(gtk_settings_get_default(), name, &value);
        var result = Marshal.PtrToStringUTF8((nint)g_value_get_string(&value));
        g_value_unset(&value);
        return result;
    }

    /// <summary><c>GtkUi::AnimationsEnabled</c>: the <c>gtk-enable-animations</c> setting. UI thread, GTK loaded.</summary>
    public static bool AnimationsEnabled()
    {
        var value = new GValue();
        _ = g_value_init(&value, GTypeBoolean);
        fixed (byte* name = "gtk-enable-animations\0"u8) g_object_get_property(gtk_settings_get_default(), name, &value);
        var result = g_value_get_boolean(&value) != 0;
        g_value_unset(&value);
        return result;
    }

    /// <summary>Chromium's <c>GetBgColor("")</c>: <c>window.background.chromium</c> rendered borderless into 24x24 and
    /// averaged, as ARGB. UI thread, GTK loaded.</summary>
    public static uint WindowBackground()
    {
        var path = gtk_widget_path_new();
        _ = gtk_widget_path_append_type(path, GTypeNone);
        fixed (byte* window = "window\0"u8) gtk_widget_path_iter_set_object_name(path, -1, window);
        fixed (byte* background = "background\0"u8) gtk_widget_path_iter_add_class(path, -1, background);
        fixed (byte* chromium = "chromium\0"u8) gtk_widget_path_iter_add_class(path, -1, chromium);
        gtk_widget_path_iter_set_state(path, -1, 0);
        var context = gtk_style_context_new();
        gtk_style_context_set_path(context, path);
        gtk_style_context_set_state(context, 0);
        gtk_style_context_set_scale(context, 1);
        gtk_widget_path_unref(path);

        var provider = gtk_css_provider_new();
        fixed (byte* css = "* {border-radius: 0px;border-style: none;box-shadow: none;}\0"u8)
            _ = gtk_css_provider_load_from_data(provider, css, -1, null);
        gtk_style_context_add_provider(context, provider, uint.MaxValue);

        var surface = cairo_image_surface_create(0, Side, Side);
        var cairo = cairo_create(surface);
        gtk_render_background(context, cairo, 0, 0, Side, Side);
        cairo_surface_flush(surface);
        var pixels = new ReadOnlySpan<uint>(cairo_image_surface_get_data(surface), Side * Side);
        var color = AveragePixel(pixels);
        cairo_destroy(cairo);
        cairo_surface_destroy(surface);
        g_object_unref(provider);
        g_object_unref(context);
        return color;
    }

    /// <summary>Calls <paramref name="callback"/> after GTK's <c>gtk-theme-name</c> or
    /// <c>gtk-application-prefer-dark-theme</c> changes, the settings Chromium's provider watches for these values. UI
    /// thread, GTK loaded.</summary>
    public static void Observe(delegate* unmanaged<nint, nint, nint, void> callback)
    {
        var settings = gtk_settings_get_default();
        foreach (var signal in (string[])["notify::gtk-theme-name", "notify::gtk-application-prefer-dark-theme"])
            fixed (byte* name = Encoding.UTF8.GetBytes(signal + "\0"))
                // G_CONNECT_AFTER, as the provider's ConnectSignal.
                _ = g_signal_connect_data(settings, name, callback, 0, 0, 2);
    }

    /// <summary>Sets a GTK setting, as an XSETTINGS change would; for tests. UI thread, GTK loaded.</summary>
    public static void SetSetting(string name, object value)
    {
        var v = new GValue();
        switch (value)
        {
            case bool b:
                _ = g_value_init(&v, GTypeBoolean);
                g_value_set_boolean(&v, b ? 1 : 0);
                break;
            case string s:
                _ = g_value_init(&v, GTypeString);
                fixed (byte* p = Encoding.UTF8.GetBytes(s + "\0")) g_value_set_string(&v, p);
                break;
            default:
                throw new ArgumentException($"No GTK setting type for {value.GetType()}.", nameof(value));
        }
        fixed (byte* n = Encoding.UTF8.GetBytes(name + "\0")) g_object_set_property(gtk_settings_get_default(), n, &v);
        g_value_unset(&v);
    }

    /// <summary><c>OsSettingsProviderGtk::PreferredContrast</c>: a theme whose name holds both "high" and "contrast",
    /// in any ASCII case.</summary>
    public static bool IsHighContrastThemeName(string? name)
    {
        var lower = string.Concat((name ?? "").Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c));
        return lower.Contains("high", StringComparison.Ordinal) && lower.Contains("contrast", StringComparison.Ordinal);
    }

    /// <summary><c>CairoSurface::GetAveragePixelValue(false)</c> over premultiplied ARGB32 pixels.</summary>
    public static uint AveragePixel(ReadOnlySpan<uint> pixels)
    {
        long a = 0, r = 0, g = 0, b = 0;
        foreach (var p in pixels)
        {
            a += p >> 24;
            r += (p >> 16) & 0xFF;
            g += (p >> 8) & 0xFF;
            b += p & 0xFF;
        }
        if (a == 0) return 0;
        return (uint)(a / pixels.Length) << 24 | (uint)(r * 255 / a) << 16 | (uint)(g * 255 / a) << 8 | (uint)(b * 255 / a);
    }

    /// <summary>color_utils' <c>IsDark</c>: relative luminance below <see cref="LuminanceMidpoint"/>.</summary>
    public static bool IsDark(uint argb)
    {
        static float Linearize(uint channel)
        {
            var c = channel / 255f;
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        var luminance = 0.2126f * Linearize((argb >> 16) & 0xFF) + 0.7152f * Linearize((argb >> 8) & 0xFF) + 0.0722f * Linearize(argb & 0xFF);
        return luminance < LuminanceMidpoint;
    }

    private static bool GtkLibraryLoaded()
    {
        // RTLD_LAZY | RTLD_NOLOAD: a handle only when Chromium already loaded it.
        fixed (byte* name = "libgtk-3.so.0\0"u8)
        {
            nint handle;
            try { handle = dlopen_libc(name, 0x1 | 0x4); }
            catch (EntryPointNotFoundException) { handle = dlopen_libdl(name, 0x1 | 0x4); }
            if (handle == 0) return false;
            try { _ = dlclose_libc(handle); }
            catch (EntryPointNotFoundException) { _ = dlclose_libdl(handle); }
            return true;
        }
    }

    [LibraryImport("libc.so.6", EntryPoint = "dlopen")] private static partial nint dlopen_libc(byte* file, int mode);
    [LibraryImport("libc.so.6", EntryPoint = "dlclose")] private static partial int dlclose_libc(nint handle);
    [LibraryImport("libdl.so.2", EntryPoint = "dlopen")] private static partial nint dlopen_libdl(byte* file, int mode);
    [LibraryImport("libdl.so.2", EntryPoint = "dlclose")] private static partial int dlclose_libdl(nint handle);

    [LibraryImport(LibGtk)] private static partial nint gtk_settings_get_default();
    [LibraryImport(LibGtk)] private static partial nint gtk_widget_path_new();
    [LibraryImport(LibGtk)] private static partial int gtk_widget_path_append_type(nint path, nuint type);
    [LibraryImport(LibGtk)] private static partial void gtk_widget_path_iter_set_object_name(nint path, int pos, byte* name);
    [LibraryImport(LibGtk)] private static partial void gtk_widget_path_iter_add_class(nint path, int pos, byte* name);
    [LibraryImport(LibGtk)] private static partial void gtk_widget_path_iter_set_state(nint path, int pos, int state);
    [LibraryImport(LibGtk)] private static partial void gtk_widget_path_unref(nint path);
    [LibraryImport(LibGtk)] private static partial nint gtk_style_context_new();
    [LibraryImport(LibGtk)] private static partial void gtk_style_context_set_path(nint context, nint path);
    [LibraryImport(LibGtk)] private static partial void gtk_style_context_set_state(nint context, int state);
    [LibraryImport(LibGtk)] private static partial void gtk_style_context_set_scale(nint context, int scale);
    [LibraryImport(LibGtk)] private static partial void gtk_style_context_add_provider(nint context, nint provider, uint priority);
    [LibraryImport(LibGtk)] private static partial nint gtk_css_provider_new();
    [LibraryImport(LibGtk)] private static partial int gtk_css_provider_load_from_data(nint provider, byte* data, nint length, void* error);
    [LibraryImport(LibGtk)] private static partial void gtk_render_background(nint context, nint cairo, double x, double y, double width, double height);
    [LibraryImport(LibGObject)] private static partial void g_object_unref(nint obj);
    [LibraryImport(LibGObject)] private static partial nint g_value_init(GValue* value, nuint type);
    [LibraryImport(LibGObject)] private static partial void g_value_unset(GValue* value);
    [LibraryImport(LibGObject)] private static partial byte* g_value_get_string(GValue* value);
    [LibraryImport(LibGObject)] private static partial void g_value_set_string(GValue* value, byte* s);
    [LibraryImport(LibGObject)] private static partial void g_value_set_boolean(GValue* value, int b);
    [LibraryImport(LibGObject)] private static partial int g_value_get_boolean(GValue* value);
    [LibraryImport(LibGObject)] private static partial void g_object_get_property(nint obj, byte* name, GValue* value);
    [LibraryImport(LibGObject)] private static partial void g_object_set_property(nint obj, byte* name, GValue* value);
    [LibraryImport(LibGObject)] private static partial nuint g_signal_connect_data(nint instance, byte* signal, delegate* unmanaged<nint, nint, nint, void> handler, nint data, nint destroy, int flags);
    [LibraryImport(LibCairo)] private static partial nint cairo_image_surface_create(int format, int width, int height);
    [LibraryImport(LibCairo)] private static partial nint cairo_create(nint surface);
    [LibraryImport(LibCairo)] private static partial void cairo_surface_flush(nint surface);
    [LibraryImport(LibCairo)] private static partial byte* cairo_image_surface_get_data(nint surface);
    [LibraryImport(LibCairo)] private static partial void cairo_destroy(nint cairo);
    [LibraryImport(LibCairo)] private static partial void cairo_surface_destroy(nint surface);
}
