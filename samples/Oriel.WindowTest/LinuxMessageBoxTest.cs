using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Oriel;
using Oriel.Cef.Platform;

/// <summary>Linux message boxes end to end: each GtkMessageDialog is found among GTK's toplevels, read back and answered
/// as a user would, all on the GLib main loop.</summary>
[SupportedOSPlatform("linux")]
static class LinuxMessageBoxTest
{
    private const int GtkMessageInfo = 0, GtkMessageWarning = 1, GtkMessageQuestion = 2, GtkMessageError = 3, GtkMessageOther = 4;

    internal static async Task RunAsync(BrowserWindow win, MessageBoxTest.CheckFn check, TimeSpan step)
    {
        win.Show();
        win.Focus();
        await Task.Delay(300);
        var probe = OnMain(() => (Maps: File.ReadAllText("/proc/self/maps").Contains("libgtk-3.so", StringComparison.Ordinal), Display: gdk_display_get_default() != 0));
        check("CEF's GtkUi has loaded and initialised GTK 3 before any message box", probe is { Maps: true, Display: true }, probe);

        var scenarios = new (string Name, Func<Task> Run)[]
        {
            ("owned box", () => OwnedAsync(win, check, step)),
            ("Escape and the close button", () => DismissAsync(win, check, step)),
            ("Signal", () => SignalAsync(win, check, step)),
            ("window-less box", () => AloneAsync(check, step)),
            ("ShowMessageBoxSync", () => SyncAsync(win, check, step)),
            ("ShowErrorBox", () => ErrorBoxAsync(check, step)),
        };
        foreach (var (name, run) in scenarios)
        {
            try { await run(); }
            catch (Exception e) { check($"message box: {name} ran through", false, e.GetBaseException().Message); }
            finally
            {
                // A box left open would hang the run.
                OnMain(() => { foreach (var d in Dialogs()) gtk_window_close(d); return 0; });
                await Task.Delay(300);
            }
        }
        check("CheckboxChecked without CheckboxLabel throws Electron's error",
            Throws<ArgumentException>(() => Dialog.ShowMessageBoxAsync(new MessageBoxOptions { CheckboxChecked = true })), null);
    }

    private static async Task OwnedAsync(BrowserWindow win, MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var parent = win.GetNativeWindowHandle();
        var box = Dialog.ShowMessageBoxAsync(win, new MessageBoxOptions
        {
            Title = "Oriel mb gtk", Message = "Save changes?", Detail = "They will be lost.", Type = MessageBoxType.Warning,
            Buttons = ["Save", "cancel", "Don't Save"], DefaultId = 0, CheckboxLabel = "Remember", CheckboxChecked = true,
        });
        var read = WaitRead(step);
        check("a GTK box shows the title, the message and the detail as secondary text",
            read is { Title: "Oriel mb gtk", Text: "Save changes?", Secondary: "They will be lost." }, read);
        check("Type picks the GtkMessageType (warning)", read?.MessageType == GtkMessageWarning, read?.MessageType);
        check("the buttons come in order with response ids 0..n, \"cancel\" as GTK's _Cancel",
            read is { } r && r.ButtonList == "Save=0,_Cancel=1,Don't Save=2", read?.ButtonList);
        check("DefaultId makes its button the default", read?.Default == "Save", read?.Default);
        check("the checkbox sits in the message area, starting checked", read is { Checkbox: "Remember", Checked: true }, read);
        check("a box with a window is transient for its X11 window and modal", read is { Modal: true } && read.TransientFor == parent, $"{read?.TransientFor} vs {parent}, modal {read?.Modal}");
        // Electron's GtkMessageBox disables the window itself, as openbox ignores the modal hint.
        check("a box with a window disables the window", !win.IsEnabled, null);
        if (read is not null)
        {
            OnMain(() => { gtk_toggle_button_set_active(read.CheckButton, 0); return 0; });
            OnMain(() => Click(read.Dialog, "Don't Save"));
        }
        var answer = await Within(box, step);
        check("clicking answers that button's index and the checkbox's state", answer is { Response: 2, CheckboxChecked: false }, answer);
        check("the window is enabled again once the box answers", Eventually(() => win.IsEnabled, step), null);
        check("the box is gone once it answers", WaitGone(step), null);
    }

    private static async Task DismissAsync(BrowserWindow win, MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var escaped = Dialog.ShowMessageBoxAsync(win, new MessageBoxOptions
        {
            Message = "Quit?", Buttons = ["Quit", "No"], CheckboxLabel = "Always", CheckboxChecked = true, Type = MessageBoxType.Question,
        });
        var read = WaitRead(step);
        check("Type question is GTK_MESSAGE_QUESTION and \"No\" is GTK's _No", read is { MessageType: GtkMessageQuestion, ButtonList: "Quit=0,_No=1" }, read);
        var active = read is not null && Eventually(() => OnMain(() => gtk_window_is_active(read.Dialog) != 0), step);
        if (active) Xdotool("key Escape");
        var answer = await Within(escaped, step);
        check("Escape answers the detected cancel id and the checkbox's state", active && answer is { Response: 1, CheckboxChecked: true }, $"active {active}, {answer}");

        var closed = Dialog.ShowMessageBoxAsync(win, new MessageBoxOptions { Message = "Close me", Buttons = ["A", "B", "C"], DefaultId = 0, CancelId = 2 });
        read = WaitRead(step);
        check("Type none is GTK_MESSAGE_OTHER", read?.MessageType == GtkMessageOther, read?.MessageType);
        var xid = read is null ? 0 : OnMain(() => XidOf(read.Dialog));
        if (xid != 0) Wmctrl($"-i -c 0x{xid:x}");
        answer = await Within(closed, step);
        check("the window manager's close answers CancelId", xid != 0 && answer is { Response: 2 }, answer);
    }

    private static async Task SignalAsync(BrowserWindow win, MessageBoxTest.CheckFn check, TimeSpan step)
    {
        using var abort = new CancellationTokenSource();
        var box = Dialog.ShowMessageBoxAsync(win, new MessageBoxOptions { Message = "Abort me", Buttons = ["A", "B", "C"], CancelId = 2, Signal = abort.Token });
        var shown = WaitRead(step) is not null;
        await abort.CancelAsync();
        var answer = await Within(box, step);
        check("aborting the signal closes the box with the cancel id", shown && answer is { Response: 2 } && WaitGone(step), answer);
    }

    private static async Task AloneAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var alone = Dialog.ShowMessageBoxAsync(new MessageBoxOptions { Message = "Alone", Buttons = ["One", "Two"], DefaultId = 1, Type = MessageBoxType.Info });
        var read = WaitRead(step);
        check("without a window the box is neither transient nor modal", read is { TransientFor: 0, Modal: false }, read);
        check("Type info is GTK_MESSAGE_INFO, and DefaultId 1 makes Two the default", read is { MessageType: GtkMessageInfo, Default: "Two" }, read);
        if (read is not null) OnMain(() => Click(read.Dialog, "Two"));
        check("its button answers its index", await Within(alone, step) is { Response: 1 }, null);

        var pixels = new byte[16 * 16 * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (0, 0, 255, 255);
        var icon = NativeImage.CreateFromBitmap(pixels, new NativeImageBitmapOptions { Width = 16, Height = 16 });
        var plain = Dialog.ShowMessageBoxAsync(new MessageBoxOptions { Message = "No buttons", Icon = icon });
        read = WaitRead(step);
        check("no Buttons adds GTK's OK (Electron's \"_Ok\") as response 0, and no Title leaves GTK's", read is { ButtonList: "_Ok=0", Title: "" }, read);
        check("Icon becomes the dialog's image, scaled to 48 pixels with its colours", read?.Image == "48x48 ff0000ff", read?.Image);
        if (read is not null) OnMain(() => Click(read.Dialog, "_Ok"));
        check("the OK answers 0", await Within(plain, step) is { Response: 0 }, null);
    }

    private static async Task SyncAsync(BrowserWindow win, MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var parent = win.GetNativeWindowHandle();
        var sync = Task.Run(() => Dialog.ShowMessageBoxSync(win, new MessageBoxOptions { Message = "Sync", Buttons = ["Yes", "Go"], Type = MessageBoxType.Error }));
        var read = WaitRead(step);
        check("ShowMessageBoxSync with a window: transient, modal, \"Yes\" as GTK's _Yes, error type",
            read is { Modal: true, ButtonList: "_Yes=0,Go=1", MessageType: GtkMessageError } && read.TransientFor == parent, read);
        var ran = false;
        Oriel.Cef.CefThread.PostUi(() => ran = true);
        await Task.Delay(700);
        check("while a Sync box runs gtk_dialog_run's nested loop, CEF's UI tasks wait", sync.IsCompleted is false && !ran, ran);
        if (read is not null) OnMain(() => Click(read.Dialog, "Go"));
        check("ShowMessageBoxSync returns the button's index", await Within(sync, step) == 1, sync.IsCompleted ? sync.Result : null);
        check("the window is enabled again once a Sync box returns", win.IsEnabled, null);
        check("UI tasks run again once it returns", Eventually(() => ran, step), ran);

        var escaped = Task.Run(() => Dialog.ShowMessageBoxSync(win, new MessageBoxOptions { Message = "Sync escape", Buttons = ["Stay", "Leave"], CancelId = 1 }));
        read = WaitRead(step);
        var active = read is not null && Eventually(() => OnMain(() => gtk_window_is_active(read.Dialog) != 0), step);
        if (active) Xdotool("key Escape");
        check("Escape stops a Sync box with the cancel id", active && await Within(escaped, step) == 1, escaped.IsCompleted ? escaped.Result : null);

        using var abort = new CancellationTokenSource();
        var aborted = Task.Run(() => Dialog.ShowMessageBoxSync(new MessageBoxOptions { Message = "Sync abort", Buttons = ["X", "Cancel"], Signal = abort.Token }));
        var shown = WaitRead(step) is not null;
        await abort.CancelAsync();
        check("cancelling the signal from another thread stops a Sync box with the cancel id (drift MB2)",
            shown && await Within(aborted, step) == 1 && WaitGone(step), aborted.IsCompleted ? aborted.Result : null);
    }

    private static async Task ErrorBoxAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var error = Task.Run(() => Dialog.ShowErrorBox("Broken", "Details here"));
        var read = WaitRead(step);
        check("ShowErrorBox: title \"Error\", the title as text over the content, the error type and a lone OK, owned by nothing",
            read is { Title: "Error", Text: "Broken", Secondary: "Details here", MessageType: GtkMessageError, ButtonList: "_Ok=0", TransientFor: 0 }, read);
        check("ShowErrorBox waits for its box", !error.IsCompleted, null);
        if (read is not null) OnMain(() => Click(read.Dialog, "_Ok"));
        await Task.WhenAny(error, Task.Delay(step));
        check("ShowErrorBox returns once OK is clicked", error.IsCompletedSuccessfully, error.Status);
    }

    private sealed record BoxRead(nint Dialog, string Title, string Text, string Secondary, int MessageType, string ButtonList, string Default,
        string? Checkbox, bool Checked, nint CheckButton, bool Modal, nint TransientFor, string? Image);

    private static BoxRead? WaitRead(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var read = OnMain(() => Dialogs() is [var d, ..] ? Read(d) : null);
            if (read is not null) return read;
            Thread.Sleep(100);
        }
        return null;
    }

    // Electron destroys an answered box, so a hidden one still in GTK's toplevels counts as left over.
    private static bool WaitGone(TimeSpan timeout) => Eventually(() => OnMain(() => Dialogs(visibleOnly: false).Count == 0), timeout);

    // Message dialogs among GTK's toplevels.
    private static unsafe List<nint> Dialogs(bool visibleOnly = true)
    {
        var found = new List<nint>();
        var list = gtk_window_list_toplevels();
        for (var node = list; node != null; node = node->Next)
            if (g_type_check_instance_is_a(node->Data, gtk_message_dialog_get_type()) != 0 && (!visibleOnly || gtk_widget_get_visible(node->Data) != 0))
                found.Add(node->Data);
        g_list_free(list);
        return found;
    }

    private static unsafe BoxRead Read(nint dialog)
    {
        var buttons = new List<string>();
        var defaultLabel = "";
        foreach (var b in Children(gtk_dialog_get_action_area(dialog)))
        {
            var label = Str(gtk_button_get_label(b));
            buttons.Add($"{label}={gtk_dialog_get_response_for_widget(dialog, b)}");
            if (gtk_widget_has_default(b) != 0) defaultLabel = label;
        }
        nint check = 0;
        foreach (var c in Children(gtk_message_dialog_get_message_area(dialog)))
            if (g_type_check_instance_is_a(c, gtk_check_button_get_type()) != 0) check = c;
        nint transient = 0;
        var window = gtk_widget_get_window(dialog);
        if (window != 0 && XGetTransientForHint(gdk_x11_display_get_xdisplay(gdk_window_get_display(window)), gdk_x11_window_get_xid(window), &transient) == 0) transient = 0;
        string? image = null;
        var widget = gtk_message_dialog_get_image(dialog);
        if (widget != 0 && g_type_check_instance_is_a(widget, gtk_image_get_type()) != 0 && gtk_image_get_pixbuf(widget) is var pixbuf and not 0)
        {
            var w = gdk_pixbuf_get_width(pixbuf);
            var h = gdk_pixbuf_get_height(pixbuf);
            var p = gdk_pixbuf_get_pixels(pixbuf) + (h / 2) * gdk_pixbuf_get_rowstride(pixbuf) + (w / 2) * gdk_pixbuf_get_n_channels(pixbuf);
            image = $"{w}x{h} {p[0]:x2}{p[1]:x2}{p[2]:x2}{p[3]:x2}";
        }
        return new BoxRead(dialog, Str(gtk_window_get_title(dialog)), StringProperty(dialog, "text"), StringProperty(dialog, "secondary-text"),
            IntProperty(dialog, "message-type"), string.Join(",", buttons), defaultLabel,
            check == 0 ? null : Str(gtk_button_get_label(check)), check != 0 && gtk_toggle_button_get_active(check) != 0, check,
            gtk_window_get_modal(dialog) != 0, transient, image);
    }

    private static int Click(nint dialog, string label)
    {
        foreach (var b in Children(gtk_dialog_get_action_area(dialog)))
            if (Str(gtk_button_get_label(b)) == label)
            {
                gtk_button_clicked(b);
                return 1;
            }
        return 0;
    }

    private static unsafe List<nint> Children(nint container)
    {
        var found = new List<nint>();
        var list = gtk_container_get_children(container);
        for (var node = list; node != null; node = node->Next) found.Add(node->Data);
        g_list_free(list);
        return found;
    }

    private static nint XidOf(nint dialog) => gdk_x11_window_get_xid(gtk_widget_get_window(dialog));

    private static unsafe string StringProperty(nint obj, string name)
    {
        nint value = 0;
        var key = Marshal.StringToCoTaskMemUTF8(name);
        g_object_get(obj, (byte*)key, &value, 0);
        Marshal.FreeCoTaskMem(key);
        var text = Str(value);
        g_free(value);
        return text;
    }

    private static unsafe int IntProperty(nint obj, string name)
    {
        long value = 0;
        var key = Marshal.StringToCoTaskMemUTF8(name);
        g_object_get(obj, (byte*)key, &value, 0);
        Marshal.FreeCoTaskMem(key);
        return (int)value;
    }

    private static string Str(nint utf8) => utf8 == 0 ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";

    // GTK lives on the UI thread's GLib loop, which keeps turning inside gtk_dialog_run while CEF's tasks wait.
    private static T OnMain<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        LinuxMessageBox.PostToMainLoop(() =>
        {
            try { result = func(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        });
        if (!done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The GLib main loop did not run the call.");
        if (error is not null) throw error;
        return result;
    }

    private static void Xdotool(string args) => System.Diagnostics.Process.Start("/usr/bin/xdotool", args)!.WaitForExit();

    private static void Wmctrl(string args) => System.Diagnostics.Process.Start("/usr/bin/wmctrl", args)!.WaitForExit();

    private static bool Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    private static bool Throws<TException>(Action act) where TException : Exception
    {
        try { act(); return false; }
        catch (TException) { return true; }
    }

    private static async Task<T?> Within<T>(Task<T> task, TimeSpan timeout) where T : class => await Task.WhenAny(task, Task.Delay(timeout)) == task ? task.Result : null;

    private static async Task<int?> Within(Task<int> task, TimeSpan timeout) => await Task.WhenAny(task, Task.Delay(timeout)) == task ? task.Result : null;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct GList
    {
        public nint Data;
        public GList* Next;
        public GList* Prev;
    }

    private const string Gtk = "libgtk-3.so.0", Gdk = "libgdk-3.so.0", GObject = "libgobject-2.0.so.0", GLib = "libglib-2.0.so.0", Pixbuf = "libgdk_pixbuf-2.0.so.0";

    [DllImport(Gtk)] private static unsafe extern GList* gtk_window_list_toplevels();
    [DllImport(Gtk)] private static extern nint gtk_message_dialog_get_type();
    [DllImport(Gtk)] private static extern nint gtk_check_button_get_type();
    [DllImport(Gtk)] private static extern nint gtk_image_get_type();
    [DllImport(Gtk)] private static extern int gtk_widget_get_visible(nint widget);
    [DllImport(Gtk)] private static extern int gtk_widget_has_default(nint widget);
    [DllImport(Gtk)] private static extern nint gtk_widget_get_window(nint widget);
    [DllImport(Gtk)] private static extern nint gtk_dialog_get_action_area(nint dialog);
    [DllImport(Gtk)] private static extern int gtk_dialog_get_response_for_widget(nint dialog, nint widget);
    [DllImport(Gtk)] private static extern nint gtk_message_dialog_get_message_area(nint dialog);
    [DllImport(Gtk)] private static extern nint gtk_message_dialog_get_image(nint dialog);
    [DllImport(Gtk)] private static extern nint gtk_image_get_pixbuf(nint image);
    [DllImport(Gtk)] private static unsafe extern GList* gtk_container_get_children(nint container);
    [DllImport(Gtk)] private static extern nint gtk_button_get_label(nint button);
    [DllImport(Gtk)] private static extern void gtk_button_clicked(nint button);
    [DllImport(Gtk)] private static extern int gtk_toggle_button_get_active(nint button);
    [DllImport(Gtk)] private static extern void gtk_toggle_button_set_active(nint button, int active);
    [DllImport(Gtk)] private static extern nint gtk_window_get_title(nint window);
    [DllImport(Gtk)] private static extern int gtk_window_get_modal(nint window);
    [DllImport(Gtk)] private static extern int gtk_window_is_active(nint window);
    [DllImport(Gtk)] private static extern void gtk_window_close(nint window);
    [DllImport(Gdk)] private static extern nint gdk_display_get_default();
    [DllImport(Gdk)] private static extern nint gdk_window_get_display(nint window);
    [DllImport(Gdk)] private static extern nint gdk_x11_display_get_xdisplay(nint display);
    [DllImport(Gdk)] private static extern nint gdk_x11_window_get_xid(nint window);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_width(nint pixbuf);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_height(nint pixbuf);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_rowstride(nint pixbuf);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_n_channels(nint pixbuf);
    [DllImport(Pixbuf)] private static unsafe extern byte* gdk_pixbuf_get_pixels(nint pixbuf);
    // Variadic: one name, one out pointer, then the NULL terminator.
    [DllImport(GObject)] private static unsafe extern void g_object_get(nint obj, byte* name, void* value, nint terminator);
    [DllImport(GObject)] private static extern int g_type_check_instance_is_a(nint instance, nint type);
    [DllImport(GLib)] private static unsafe extern void g_list_free(GList* list);
    [DllImport(GLib)] private static extern void g_free(nint memory);
    [DllImport("libX11.so.6")] private static unsafe extern int XGetTransientForHint(nint display, nint window, nint* parent);
}
