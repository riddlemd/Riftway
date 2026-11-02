using System.Runtime.InteropServices;

/// <summary>Reads the shown GtkAboutDialog back from GTK and answers it. CEF's UI thread (GTK's main thread) only.</summary>
static class LinuxGtkAbout
{
    private const string LibGtk = "libgtk-3.so.0";

    internal sealed record Fields(string? Name, string? Version, string? Copyright, string? Website, string Authors, int LogoSize);

    /// <summary>The visible about dialog's fields, or null when none shows.</summary>
    internal static Fields? Read()
    {
        var dialog = Find();
        if (dialog == 0) return null;
        var authors = new List<string>();
        var array = gtk_about_dialog_get_authors(dialog);
        for (var i = 0; array != 0 && Marshal.ReadIntPtr(array, i * IntPtr.Size) is var a && a != 0; i++) authors.Add(Marshal.PtrToStringUTF8(a)!);
        var logo = gtk_about_dialog_get_logo(dialog);
        return new Fields(Marshal.PtrToStringUTF8(gtk_about_dialog_get_program_name(dialog)), Marshal.PtrToStringUTF8(gtk_about_dialog_get_version(dialog)),
            Marshal.PtrToStringUTF8(gtk_about_dialog_get_copyright(dialog)), Marshal.PtrToStringUTF8(gtk_about_dialog_get_website(dialog)),
            string.Join(",", authors), logo == 0 ? 0 : gdk_pixbuf_get_width(logo));
    }

    /// <summary>Answers the visible about dialog as its close button does.</summary>
    internal static void Respond()
    {
        if (Find() is var dialog and not 0) gtk_dialog_response(dialog, -7 /* GTK_RESPONSE_CLOSE */);
    }

    // gtk_window_list_toplevels' GList: data, next, prev.
    private static nint Find()
    {
        var type = gtk_about_dialog_get_type();
        var list = gtk_window_list_toplevels();
        try
        {
            for (var node = list; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var window = Marshal.ReadIntPtr(node);
                if (g_type_check_instance_is_a(window, type) != 0 && gtk_widget_get_visible(window) != 0) return window;
            }
            return 0;
        }
        finally { g_list_free(list); }
    }

    [DllImport(LibGtk)] private static extern nuint gtk_about_dialog_get_type();
    [DllImport(LibGtk)] private static extern nint gtk_window_list_toplevels();
    [DllImport(LibGtk)] private static extern int gtk_widget_get_visible(nint widget);
    [DllImport(LibGtk)] private static extern nint gtk_about_dialog_get_program_name(nint dialog);
    [DllImport(LibGtk)] private static extern nint gtk_about_dialog_get_version(nint dialog);
    [DllImport(LibGtk)] private static extern nint gtk_about_dialog_get_copyright(nint dialog);
    [DllImport(LibGtk)] private static extern nint gtk_about_dialog_get_website(nint dialog);
    [DllImport(LibGtk)] private static extern nint gtk_about_dialog_get_authors(nint dialog);
    [DllImport(LibGtk)] private static extern nint gtk_about_dialog_get_logo(nint dialog);
    [DllImport(LibGtk)] private static extern void gtk_dialog_response(nint dialog, int response);
    [DllImport("libgdk_pixbuf-2.0.so.0")] private static extern int gdk_pixbuf_get_width(nint pixbuf);
    [DllImport("libgobject-2.0.so.0")] private static extern int g_type_check_instance_is_a(nint instance, nuint type);
    [DllImport("libglib-2.0.so.0")] private static extern void g_list_free(nint list);
}
