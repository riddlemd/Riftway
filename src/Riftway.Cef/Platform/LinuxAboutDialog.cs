using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>Electron's Linux <c>Browser::ShowAboutPanel</c>: a GtkAboutDialog of the options set, destroyed on any
/// response. GTK main thread (CEF's UI thread) only.</summary>
[SupportedOSPlatform("linux")]
public static unsafe partial class LinuxAboutDialog
{
    private const string LibGtk = "libgtk-3.so.0";
    private const string LibPixbuf = "libgdk_pixbuf-2.0.so.0";
    private const string LibGObject = "libgobject-2.0.so.0";
    private const string LibGLib = "libglib-2.0.so.0";
    // G_CONNECT_SWAPPED: the callback gets the dialog, not the emitter's arguments.
    private const int ConnectSwapped = 2;

    /// <summary>Shows the dialog with whichever fields are set; an icon that does not load is skipped, as Electron
    /// only logs it.</summary>
    public static void Show(string? applicationName, string? applicationVersion, string? copyright, string? website, string? iconPath,
        IReadOnlyList<string>? authors)
    {
        var dialog = gtk_about_dialog_new();
        if (applicationName is not null) gtk_about_dialog_set_program_name(dialog, applicationName);
        if (applicationVersion is not null) gtk_about_dialog_set_version(dialog, applicationVersion);
        if (copyright is not null) gtk_about_dialog_set_copyright(dialog, copyright);
        if (website is not null) gtk_about_dialog_set_website(dialog, website);
        if (iconPath is not null)
        {
            nint error = 0;
            var icon = gdk_pixbuf_new_from_file_at_size(iconPath, 64, 64, &error);
            if (error != 0) g_error_free(error);
            else
            {
                gtk_about_dialog_set_logo(dialog, icon);
                g_object_unref(icon);
            }
        }
        if (authors is { Count: > 0 })
        {
            var strings = authors.Select(a => Marshal.StringToCoTaskMemUTF8(a)).ToArray();
            var array = new nint[strings.Length + 1];
            strings.CopyTo(array, 0);
            fixed (nint* p = array) gtk_about_dialog_set_authors(dialog, p);
            foreach (var s in strings) Marshal.FreeCoTaskMem(s);
        }
        var destroy = NativeLibrary.GetExport(NativeLibrary.Load(LibGtk), "gtk_widget_destroy");
        g_signal_connect_data(dialog, "response", destroy, dialog, 0, ConnectSwapped);
        gtk_widget_show_all(dialog);
    }

    [LibraryImport(LibGtk)] private static partial nint gtk_about_dialog_new();
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] private static partial void gtk_about_dialog_set_program_name(nint dialog, string name);
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] private static partial void gtk_about_dialog_set_version(nint dialog, string version);
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] private static partial void gtk_about_dialog_set_copyright(nint dialog, string copyright);
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] private static partial void gtk_about_dialog_set_website(nint dialog, string website);
    [LibraryImport(LibGtk)] private static partial void gtk_about_dialog_set_logo(nint dialog, nint logo);
    [LibraryImport(LibGtk)] private static partial void gtk_about_dialog_set_authors(nint dialog, nint* authors);
    [LibraryImport(LibGtk)] private static partial void gtk_widget_show_all(nint widget);
    [LibraryImport(LibPixbuf, StringMarshalling = StringMarshalling.Utf8)] private static partial nint gdk_pixbuf_new_from_file_at_size(string file, int width, int height, nint* error);
    [LibraryImport(LibGObject)] private static partial void g_object_unref(nint obj);
    [LibraryImport(LibGObject, StringMarshalling = StringMarshalling.Utf8)] private static partial ulong g_signal_connect_data(nint instance, string signal, nint handler, nint data, nint destroyData, int flags);
    [LibraryImport(LibGLib)] private static partial void g_error_free(nint error);
}
