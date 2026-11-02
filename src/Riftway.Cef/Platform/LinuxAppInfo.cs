using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>The GIO and GTK calls behind Electron's Linux protocol client (<c>browser_linux.cc</c>).</summary>
[SupportedOSPlatform("linux")]
public static unsafe partial class LinuxAppInfo
{
    private const string LibGio = "libgio-2.0.so.0";
    private const string LibGLib = "libglib-2.0.so.0";
    private const string LibGObject = "libgobject-2.0.so.0";
    private const string LibGtk = "libgtk-3.so.0";
    private const string LibCairo = "libcairo.so.2";
    // GTK_ICON_LOOKUP_FORCE_SIZE.
    private const int ForceSize = 16;

    /// <summary>Electron's <c>SetDefaultWebClient</c>: the desktop file <paramref name="desktopName"/> becomes the default
    /// for <c>x-scheme-handler/&lt;scheme&gt;</c>; false when it is not installed or GIO refuses.</summary>
    public static bool SetDefaultForScheme(string desktopName, string scheme)
    {
        var info = g_desktop_app_info_new(desktopName);
        if (info == 0) return false;
        nint error = 0;
        var ok = g_app_info_set_as_default_for_type(info, "x-scheme-handler/" + scheme, &error) != 0;
        if (error != 0)
        {
            Console.Error.WriteLine($"[Riftway] {Marshal.PtrToStringUTF8(*(nint*)(error + 8))}");
            g_error_free(error);
        }
        g_object_unref(info);
        return ok;
    }

    /// <summary>The id (desktop file name) of <c>g_app_info_get_default_for_uri_scheme</c>, or null.</summary>
    public static string? DefaultAppId(string scheme)
    {
        var info = g_app_info_get_default_for_uri_scheme(scheme);
        if (info == 0) return null;
        try { return Marshal.PtrToStringUTF8(g_app_info_get_id(info)); }
        finally { g_object_unref(info); }
    }

    /// <summary>The default app for <paramref name="scheme"/>: its display name, its executable resolved as Electron's
    /// <c>ResolveExecutablePath</c> does (null when it cannot be), and its icon at 32 pixels as Cairo's premultiplied
    /// BGRA (null when the theme has none); null when there is no app.</summary>
    public static (string Name, string? Executable, (byte[] Bgra, int Width, int Height)? Icon)? DefaultApp(string scheme)
    {
        var info = g_app_info_get_default_for_uri_scheme(scheme);
        if (info == 0) return null;
        try
        {
            var name = Marshal.PtrToStringUTF8(g_app_info_get_display_name(info)) ?? "";
            var executable = Marshal.PtrToStringUTF8(g_app_info_get_executable(info));
            return (name, ResolveExecutable(executable), Icon(info));
        }
        finally { g_object_unref(info); }
    }

    private static string? ResolveExecutable(string? executable)
    {
        if (string.IsNullOrEmpty(executable)) return null;
        if (executable.StartsWith('/')) return executable;
        var found = g_find_program_in_path(executable);
        if (found == 0) return null;
        try { return Marshal.PtrToStringUTF8(found); }
        finally { g_free(found); }
    }

    // GTK 3's half of Electron's GetApplicationIcon: the theme's icon for the GIcon at 32 pixels, forced to that size.
    private static (byte[], int, int)? Icon(nint info)
    {
        var gicon = g_app_info_get_icon(info);
        if (gicon == 0) return null;
        var iconInfo = gtk_icon_theme_lookup_by_gicon_for_scale(gtk_icon_theme_get_default(), gicon, 32, 1, ForceSize);
        if (iconInfo == 0) return null;
        try
        {
            var surface = gtk_icon_info_load_surface(iconInfo, 0, null);
            if (surface == 0) return null;
            try
            {
                int width = cairo_image_surface_get_width(surface), height = cairo_image_surface_get_height(surface);
                int stride = cairo_image_surface_get_stride(surface);
                var data = cairo_image_surface_get_data(surface);
                var pixels = new byte[width * height * 4];
                for (var y = 0; y < height; y++) Marshal.Copy(data + y * stride, pixels, y * width * 4, width * 4);
                return (pixels, width, height);
            }
            finally { cairo_surface_destroy(surface); }
        }
        finally { g_object_unref(iconInfo); }
    }

    [LibraryImport(LibGio, StringMarshalling = StringMarshalling.Utf8)] private static partial nint g_desktop_app_info_new(string desktopId);
    [LibraryImport(LibGio, StringMarshalling = StringMarshalling.Utf8)] private static partial int g_app_info_set_as_default_for_type(nint info, string contentType, nint* error);
    [LibraryImport(LibGio, StringMarshalling = StringMarshalling.Utf8)] private static partial nint g_app_info_get_default_for_uri_scheme(string scheme);
    [LibraryImport(LibGio)] private static partial nint g_app_info_get_id(nint info);
    [LibraryImport(LibGio)] private static partial nint g_app_info_get_display_name(nint info);
    [LibraryImport(LibGio)] private static partial nint g_app_info_get_executable(nint info);
    [LibraryImport(LibGio)] private static partial nint g_app_info_get_icon(nint info);
    [LibraryImport(LibGLib, StringMarshalling = StringMarshalling.Utf8)] private static partial nint g_find_program_in_path(string program);
    [LibraryImport(LibGLib)] private static partial void g_free(nint memory);
    [LibraryImport(LibGLib)] private static partial void g_error_free(nint error);
    [LibraryImport(LibGObject)] private static partial void g_object_unref(nint obj);
    [LibraryImport(LibGtk)] private static partial nint gtk_icon_theme_get_default();
    [LibraryImport(LibGtk)] private static partial nint gtk_icon_theme_lookup_by_gicon_for_scale(nint theme, nint icon, int size, int scale, int flags);
    [LibraryImport(LibGtk)] private static partial nint gtk_icon_info_load_surface(nint info, nint forWindow, nint* error);
    [LibraryImport(LibCairo)] private static partial int cairo_image_surface_get_width(nint surface);
    [LibraryImport(LibCairo)] private static partial int cairo_image_surface_get_height(nint surface);
    [LibraryImport(LibCairo)] private static partial int cairo_image_surface_get_stride(nint surface);
    [LibraryImport(LibCairo)] private static partial nint cairo_image_surface_get_data(nint surface);
    [LibraryImport(LibCairo)] private static partial void cairo_surface_destroy(nint surface);
}
