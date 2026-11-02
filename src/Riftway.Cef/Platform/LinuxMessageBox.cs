using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>A message box icon for GTK: 32-bit RGBA with straight alpha, top row first.</summary>
/// <param name="Rgba">Width*Height*4 bytes.</param>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
public readonly record struct GtkIconBitmap(byte[] Rgba, int Width, int Height);

/// <summary>What a GTK message box shows; Electron's <c>electron::MessageBoxSettings</c> as <c>message_box_gtk.cc</c>
/// reads it.</summary>
/// <param name="Type">0 none, 1 info, 2 warning, 3 error, 4 question.</param>
/// <param name="Buttons">The button labels, in order; empty shows a single OK.</param>
/// <param name="DefaultId">The response of the default button; one no button has leaves none.</param>
/// <param name="CancelId">The answer when the box closes without a button.</param>
/// <param name="Title">The window title; empty leaves GTK's.</param>
/// <param name="Message">The main text.</param>
/// <param name="Detail">The secondary text; empty for none.</param>
/// <param name="CheckboxLabel">The checkbox label; empty for no checkbox.</param>
/// <param name="CheckboxChecked">Whether the checkbox starts checked.</param>
/// <param name="Icon">An image in place of the type's.</param>
/// <param name="Parent">The X11 window the box is transient for and modal to, or 0.</param>
public sealed record GtkMessageBoxSettings(int Type, IReadOnlyList<string> Buttons, int DefaultId, int CancelId, string Title, string Message,
    string Detail, string CheckboxLabel, bool CheckboxChecked, GtkIconBitmap? Icon, nint Parent);

/// <summary>A button the dialog adds: its label before translation (a GTK msgid when <see cref="Stock"/>) and response id.</summary>
internal readonly record struct GtkDialogButton(string Label, bool Stock, int Response);

/// <summary>Everything Electron's <c>GtkMessageBox</c> constructor puts in the dialog, worked out before any GTK call.</summary>
internal sealed record GtkMessageBoxPlan(int MessageType, IReadOnlyList<GtkDialogButton> Buttons, int DefaultResponse, string? Title,
    string? SecondaryText, string? CheckboxLabel, bool CheckboxChecked, bool Transient);

/// <summary>A <c>GtkMessageDialog</c> set up and run as Electron's <c>shell/browser/ui/message_box_gtk.cc</c>. Create, run and
/// close it on CEF's UI thread, which runs the GLib main loop.</summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class LinuxMessageBox
{
    private const string Gtk = "libgtk-3.so.0";
    private const string Gdk = "libgdk-3.so.0";
    private const string Pixbuf = "libgdk_pixbuf-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string X11 = "libX11.so.6";

    internal const int MessageInfo = 0, MessageWarning = 1, MessageQuestion = 2, MessageError = 3, MessageOther = 4;

    private static bool s_gtkReady;

    private readonly int _cancelId;
    private nint _dialog;
    private nint _checkbox;
    private bool _open;
    private GCHandle _self;
    private Action<int, bool>? _callback;

    /// <summary>Builds the dialog as Electron's <c>GtkMessageBox</c> constructor does; UI thread only.</summary>
    /// <exception cref="PlatformNotSupportedException">GTK 3 could not be initialised.</exception>
    public LinuxMessageBox(GtkMessageBoxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureGtk();
        var plan = PlanOf(settings);
        _cancelId = settings.CancelId;

        using (var message = new Utf8(settings.Message))
        using (var format = new Utf8("%s"))
            _dialog = gtk_message_dialog_new(0, 0, plan.MessageType, 0 /* GTK_BUTTONS_NONE */, format, message);
        _open = true;
        if (plan.SecondaryText is { } detail)
        {
            using var text = new Utf8(detail);
            using var format = new Utf8("%s");
            gtk_message_dialog_format_secondary_text(_dialog, format, text);
        }
        if (plan.Title is { } title)
        {
            using var text = new Utf8(title);
            gtk_window_set_title(_dialog, text);
        }

        if (settings.Icon is { Width: > 0, Height: > 0 } icon) SetImage(icon);

        if (plan.CheckboxLabel is { } label)
        {
            var area = gtk_message_dialog_get_message_area(_dialog);
            nint check;
            using (var text = new Utf8(label)) check = gtk_check_button_new_with_label(text);
            gtk_toggle_button_set_active(check, plan.CheckboxChecked ? 1 : 0);
            gtk_container_add(area, check);
            gtk_widget_show(check);
            _checkbox = check;
        }

        foreach (var button in plan.Buttons)
        {
            using var text = new Utf8(button.Stock ? Translate(button.Label) : button.Label);
            gtk_dialog_add_button(_dialog, text, button.Response);
        }
        gtk_dialog_set_default_response(_dialog, plan.DefaultResponse);

        if (plan.Transient)
        {
            SetTransientFor(settings.Parent);
            gtk_window_set_modal(_dialog, 1);
        }
    }

    /// <summary>Electron's <c>RunSynchronous</c>: shows the box and runs <c>gtk_dialog_run</c>'s nested main loop until it
    /// answers; a negative response (Escape, the close button, <see cref="Close"/>) answers the cancel id. Destroys the
    /// box.</summary>
    public int RunSynchronous()
    {
        Show();
        var response = gtk_dialog_run(_dialog);
        Destroy();
        return ResponseOf(response, _cancelId);
    }

    /// <summary>Electron's <c>RunAsynchronous</c>: shows the box and returns; <paramref name="callback"/> gets the answer
    /// and the checkbox's state on the UI thread, after which the box is destroyed.</summary>
    public void RunAsynchronous(Action<int, bool> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
        _self = GCHandle.Alloc(this);
        Connect(_dialog, "delete-event"u8, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnDeleteEvent, 0);
        Connect(_dialog, "response"u8, (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnResponse, GCHandle.ToIntPtr(_self));
        Show();
    }

    /// <summary>Electron's <c>CloseMessageBox</c>: <c>gtk_window_close</c>, which answers the cancel id; nothing once the
    /// box has answered. UI thread only.</summary>
    public void Close()
    {
        if (_open) gtk_window_close(_dialog);
    }

    /// <summary>Electron's ready <c>ShowErrorBox</c>: an error box titled "Error" with no buttons of its own, run
    /// synchronously. UI thread only.</summary>
    public static void ShowError(string title, string content)
        => new LinuxMessageBox(ErrorBoxSettingsOf(title, content)).RunSynchronous();

    /// <summary>The settings Electron's <c>ShowErrorBox</c> builds once the app is ready.</summary>
    internal static GtkMessageBoxSettings ErrorBoxSettingsOf(string? title, string? content)
        // Electron leaves default_id uninitialised here; -1, no default, is the value it gets otherwise.
        => new(MessageError, [], -1, 0, "Error", title ?? "", content ?? "", "", false, null, 0);

    /// <summary>What Electron's <c>ShowErrorBox</c> writes to stderr before the app is ready: the title in red on gray,
    /// bold, then the content in black.</summary>
    public static string ErrorBoxConsoleText(string? title, string? content)
        => $"\u001b[1m\u001b[47m\u001b[31m{title}\n\u001b[30m{content}\n\u001b[0m";

    /// <summary>Runs <paramref name="action"/> on the thread running the default GLib main context (CEF's UI thread),
    /// including inside <c>gtk_dialog_run</c>'s nested loop, where CEF's own tasks wait.</summary>
    public static void PostToMainLoop(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var handle = GCHandle.Alloc(action);
        _ = g_idle_add_full(0 /* G_PRIORITY_DEFAULT */, &OnIdle, GCHandle.ToIntPtr(handle), 0);
    }

    /// <summary>The dialog Electron's constructor builds for <paramref name="settings"/>.</summary>
    internal static GtkMessageBoxPlan PlanOf(GtkMessageBoxSettings settings)
    {
        var buttons = new List<GtkDialogButton>();
        if (settings.Buttons.Count == 0) buttons.Add(ButtonOf("OK", 0));
        else for (var i = 0; i < settings.Buttons.Count; i++) buttons.Add(ButtonOf(settings.Buttons[i], i));
        return new GtkMessageBoxPlan(MessageTypeOf(settings.Type), buttons, settings.DefaultId,
            settings.Title.Length > 0 ? settings.Title : null,
            settings.Detail.Length > 0 ? settings.Detail : null,
            settings.CheckboxLabel.Length > 0 ? settings.CheckboxLabel : null,
            settings.CheckboxChecked,
            settings.Parent != 0);

        static GtkDialogButton ButtonOf(string label, int response)
            => StockMsgIdOf(label) is { } msgid ? new GtkDialogButton(msgid, true, response) : new GtkDialogButton(label, false, response);
    }

    /// <summary>The answer for a GTK response: GTK's own negative ids (Escape, the close button, a close) are the cancel id.</summary>
    internal static int ResponseOf(int response, int cancelId) => response < 0 ? cancelId : response;

    /// <summary>Electron's <c>GetMessageType</c>: none is <c>GTK_MESSAGE_OTHER</c>.</summary>
    internal static int MessageTypeOf(int type) => type switch
    {
        1 => MessageInfo,
        2 => MessageWarning,
        3 => MessageError,
        4 => MessageQuestion,
        _ => MessageOther,
    };

    /// <summary>Electron's <c>TranslateToStock</c> before translation: the GTK msgid for an ASCII-case-insensitive "cancel",
    /// "no", "ok" or "yes" (Electron asks for "_Ok", which GTK's catalogue spells "_OK"); null for any other label.</summary>
    internal static string? StockMsgIdOf(string label) => AsciiLower(label) switch
    {
        "cancel" => "_Cancel",
        "no" => "_No",
        "ok" => "_Ok",
        "yes" => "_Yes",
        _ => null,
    };

    private static string AsciiLower(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + 32);
        return new string(chars);
    }

    // gtk_util::GtkGettext: the label in GTK's own catalogue, "gtk30".
    private static string Translate(string msgid)
    {
        using var domain = new Utf8("gtk30");
        using var id = new Utf8(msgid);
        return Marshal.PtrToStringUTF8((nint)g_dgettext(domain, id)) ?? msgid;
    }

    // GTK_ICON_SIZE_DIALOG is 48 pixels; Electron scales any icon to it.
    private void SetImage(GtkIconBitmap icon)
    {
        nint bytes;
        fixed (byte* p = icon.Rgba) bytes = g_bytes_new(p, (nuint)icon.Rgba.Length);
        var pixbuf = gdk_pixbuf_new_from_bytes(bytes, 0 /* GDK_COLORSPACE_RGB */, 1, 8, icon.Width, icon.Height, icon.Width * 4);
        g_bytes_unref(bytes);
        var scaled = gdk_pixbuf_scale_simple(pixbuf, 48, 48, 2 /* GDK_INTERP_BILINEAR */);
        var image = gtk_image_new_from_pixbuf(scaled);
        gtk_message_dialog_set_image(_dialog, image);
        gtk_widget_show(image);
        g_object_unref(scaled);
        g_object_unref(pixbuf);
    }

    // gtk::SetGtkTransientForAura with GtkUiPlatformX11: the parent is an X11 window, not a GtkWindow, so the hint goes on
    // the realized dialog's X window directly.
    private void SetTransientFor(nint parent)
    {
        gtk_widget_realize(_dialog);
        var window = gtk_widget_get_window(_dialog);
        var display = gdk_x11_display_get_xdisplay(gdk_window_get_display(window));
        var xid = gdk_x11_window_get_xid(window);
        var parentId = parent;
        _ = XChangeProperty(display, xid, 68 /* XA_WM_TRANSIENT_FOR */, 33 /* XA_WINDOW */, 32, 0 /* PropModeReplace */, (byte*)&parentId, 1);
        var dialogType = Atom(display, "_NET_WM_WINDOW_TYPE_DIALOG\0"u8);
        _ = XChangeProperty(display, xid, Atom(display, "_NET_WM_WINDOW_TYPE\0"u8), 4 /* XA_ATOM */, 32, 0, (byte*)&dialogType, 1);
    }

    // GtkUiPlatformX11::ShowGtkWindow: present with an X server timestamp so the window manager raises and focuses it.
    private void Show()
    {
        gtk_widget_show(_dialog);
        gtk_window_present_with_time(_dialog, gdk_x11_get_server_time(gtk_widget_get_window(_dialog)));
    }

    private void Destroy()
    {
        _open = false;
        gtk_widget_destroy(_dialog);
        _dialog = 0;
    }

    // Electron's OnResponseDialog.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResponse(nint widget, int response, nint data)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is not LinuxMessageBox box || !box._open) return;
            box._open = false;
            gtk_widget_hide(box._dialog);
            var isChecked = box._checkbox != 0 && gtk_toggle_button_get_active(box._checkbox) != 0;
            var callback = box._callback!;
            box._callback = null;
            try { callback(ResponseOf(response, box._cancelId), isChecked); }
            finally
            {
                gtk_widget_destroy(box._dialog);
                box._dialog = 0;
                box._self.Free();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Riftway] Message box response failed: {e}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnDeleteEvent(nint widget, nint e, nint data) => gtk_widget_hide_on_delete(widget);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnIdle(nint data)
    {
        var handle = GCHandle.FromIntPtr(data);
        try { (handle.Target as Action)?.Invoke(); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Main-loop callback failed: {e}"); }
        finally { handle.Free(); }
        return 0; // G_SOURCE_REMOVE
    }

    private static void Connect(nint instance, ReadOnlySpan<byte> signal, nint handler, nint data)
    {
        Span<byte> name = stackalloc byte[signal.Length + 1];
        signal.CopyTo(name);
        name[^1] = 0;
        fixed (byte* p = name) _ = g_signal_connect_data(instance, p, handler, data, 0, 0);
    }

    private static nint Atom(nint display, ReadOnlySpan<byte> zeroTerminated)
    {
        fixed (byte* p = zeroTerminated) return XInternAtom(display, p, 0);
    }

    /// <summary>Makes sure GTK 3 is initialised on this (the UI) thread. Chromium's <c>GtkUi</c> normally has done it
    /// already; when it has not (a Qt desktop, or GTK failed there), this does it the way <c>GtkUi::Initialize</c> does.</summary>
    /// <exception cref="PlatformNotSupportedException">GTK 3 cannot be used: GTK 4 is loaded, or there is no X display.</exception>
    public static void EnsureGtk()
    {
        if (s_gtkReady) return;
        // GTK 3 and 4 abort when both are in one process; Chromium loads 4 under --gtk-version=4.
        if (File.ReadAllText("/proc/self/maps").Contains("libgtk-4.so", StringComparison.Ordinal))
            throw new PlatformNotSupportedException("Message boxes need GTK 3, but this process has loaded GTK 4.");
        if (gdk_display_get_default() == 0)
        {
            using (var name = new Utf8("NO_AT_BRIDGE"))
            using (var one = new Utf8("1"))
                _ = setenv(name, one, 1);
            using (var backends = new Utf8("x11")) gdk_set_allowed_backends(backends);
            // GDK installs an Xlib error handler that aborts on async errors; Chromium's logs them instead, so it is put back.
            var chromium = XSetErrorHandler(0);
            var ok = gtk_init_check(null, null) != 0;
            if (chromium != 0) _ = XSetErrorHandler(chromium);
            if (!ok) throw new PlatformNotSupportedException("GTK 3 could not be initialised for a message box.");
        }
        s_gtkReady = true;
    }

    /// <summary>A zero-terminated UTF-8 copy of a string in native memory.</summary>
    private readonly struct Utf8 : IDisposable
    {
        private readonly nint _p;

        public Utf8(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _p = (nint)NativeMemory.Alloc((nuint)bytes.Length + 1);
            bytes.CopyTo(new Span<byte>((void*)_p, bytes.Length));
            ((byte*)_p)[bytes.Length] = 0;
        }

        public static implicit operator byte*(Utf8 s) => (byte*)s._p;

        public void Dispose() => NativeMemory.Free((void*)_p);
    }

    // gtk_message_dialog_new and format_secondary_text are variadic; one "%s" argument passes as a plain pointer on the
    // SysV x64 and AAPCS64 Linux ABIs.
    [DllImport(Gtk)] private static extern nint gtk_message_dialog_new(nint parent, int flags, int type, int buttons, byte* format, byte* arg);
    [DllImport(Gtk)] private static extern void gtk_message_dialog_format_secondary_text(nint dialog, byte* format, byte* arg);
    [DllImport(Gtk)] private static extern nint gtk_message_dialog_get_message_area(nint dialog);
    [DllImport(Gtk)] private static extern void gtk_message_dialog_set_image(nint dialog, nint image);
    [DllImport(Gtk)] private static extern int gtk_init_check(int* argc, byte*** argv);
    [DllImport(Gtk)] private static extern void gtk_window_set_title(nint window, byte* title);
    [DllImport(Gtk)] private static extern void gtk_window_set_modal(nint window, int modal);
    [DllImport(Gtk)] private static extern void gtk_window_present_with_time(nint window, uint timestamp);
    [DllImport(Gtk)] private static extern void gtk_window_close(nint window);
    [DllImport(Gtk)] private static extern nint gtk_check_button_new_with_label(byte* label);
    [DllImport(Gtk)] private static extern void gtk_toggle_button_set_active(nint button, int active);
    [DllImport(Gtk)] private static extern int gtk_toggle_button_get_active(nint button);
    [DllImport(Gtk)] private static extern void gtk_container_add(nint container, nint widget);
    [DllImport(Gtk)] private static extern nint gtk_dialog_add_button(nint dialog, byte* text, int response);
    [DllImport(Gtk)] private static extern void gtk_dialog_set_default_response(nint dialog, int response);
    [DllImport(Gtk)] private static extern int gtk_dialog_run(nint dialog);
    [DllImport(Gtk)] private static extern nint gtk_image_new_from_pixbuf(nint pixbuf);
    [DllImport(Gtk)] private static extern void gtk_widget_show(nint widget);
    [DllImport(Gtk)] private static extern void gtk_widget_hide(nint widget);
    [DllImport(Gtk)] private static extern void gtk_widget_realize(nint widget);
    [DllImport(Gtk)] private static extern void gtk_widget_destroy(nint widget);
    [DllImport(Gtk)] private static extern int gtk_widget_hide_on_delete(nint widget);
    [DllImport(Gtk)] private static extern nint gtk_widget_get_window(nint widget);
    [DllImport(Gdk)] private static extern nint gdk_display_get_default();
    [DllImport(Gdk)] private static extern void gdk_set_allowed_backends(byte* backends);
    [DllImport(Gdk)] private static extern nint gdk_window_get_display(nint window);
    [DllImport(Gdk)] private static extern nint gdk_x11_display_get_xdisplay(nint display);
    [DllImport(Gdk)] private static extern nint gdk_x11_window_get_xid(nint window);
    [DllImport(Gdk)] private static extern uint gdk_x11_get_server_time(nint window);
    [DllImport(Pixbuf)] private static extern nint gdk_pixbuf_new_from_bytes(nint data, int colorspace, int hasAlpha, int bitsPerSample, int width, int height, int rowstride);
    [DllImport(Pixbuf)] private static extern nint gdk_pixbuf_scale_simple(nint src, int width, int height, int interpolation);
    [DllImport(GLib)] private static extern nint g_bytes_new(byte* data, nuint size);
    [DllImport(GLib)] private static extern void g_bytes_unref(nint bytes);
    [DllImport(GLib)] private static extern byte* g_dgettext(byte* domain, byte* msgid);
    [DllImport(GLib)] private static extern uint g_idle_add_full(int priority, delegate* unmanaged[Cdecl]<nint, int> function, nint data, nint notify);
    [DllImport(GObject)] private static extern nuint g_signal_connect_data(nint instance, byte* signal, nint handler, nint data, nint destroyData, int flags);
    [DllImport(GObject)] private static extern void g_object_unref(nint obj);
    [DllImport(X11)] private static extern nint XInternAtom(nint display, byte* name, int onlyIfExists);
    [DllImport(X11)] private static extern int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, byte* data, int count);
    [DllImport(X11)] private static extern nint XSetErrorHandler(nint handler);
    [DllImport("libc.so.6")] private static extern int setenv(byte* name, byte* value, int overwrite);
}
