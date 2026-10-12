using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>The kind of message box, which picks its icon; Electron's <c>type</c> option, in its order.</summary>
public enum MessageBoxType
{
    /// <summary>No icon; Electron's <c>none</c>.</summary>
    None = 0,

    /// <summary>The information icon; Electron's <c>info</c>.</summary>
    Info,

    /// <summary>The warning icon; Electron's <c>warning</c>.</summary>
    Warning,

    /// <summary>The error icon; Electron's <c>error</c>.</summary>
    Error,

    /// <summary>A question; on Windows the information icon, as in Electron. Electron's <c>question</c>.</summary>
    Question,
}

/// <summary>Options for <see cref="Dialog.ShowMessageBoxAsync(BaseWindow?, MessageBoxOptions)"/>; Electron's
/// <c>showMessageBox</c> options.</summary>
public sealed record MessageBoxOptions
{
    /// <summary>The main text.</summary>
    public string Message { get; init; } = "";

    /// <summary>Picks the icon unless <see cref="Icon"/> is set.</summary>
    public MessageBoxType Type { get; init; }

    /// <summary>The button labels, in order; empty shows a single OK. On Windows "OK", "Yes", "No", "Cancel", "Retry"
    /// and "Close" (any ASCII case) become the system's buttons and the rest command links, unless
    /// <see cref="NoLink"/>.</summary>
    public IReadOnlyList<string> Buttons { get; init; } = [];

    /// <summary>The index of the button selected when the box opens; -1 for the platform default.</summary>
    public int DefaultId { get; init; } = -1;

    /// <summary>The index returned when the box is dismissed (Esc, its close button, <see cref="Signal"/>). Null picks
    /// the first "cancel" or "no" button (any case), else 0, or 1 when <see cref="DefaultId"/> is 0 and there are
    /// several buttons.</summary>
    public int? CancelId { get; init; }

    /// <summary>The window title; empty uses the app's name on Windows and sets none on Linux, as Electron. macOS shows
    /// none.</summary>
    public string Title { get; init; } = "";

    /// <summary>Extra text below the message, which then shows highlighted.</summary>
    public string Detail { get; init; } = "";

    /// <summary>A checkbox label; empty shows no checkbox.</summary>
    public string CheckboxLabel { get; init; } = "";

    /// <summary>Whether the checkbox starts checked; needs <see cref="CheckboxLabel"/>.</summary>
    public bool CheckboxChecked { get; init; }

    /// <summary>An icon in place of the type's; its 1x representation is used.</summary>
    public NativeImage? Icon { get; init; }

    /// <summary>Windows: keeps custom buttons as plain buttons rather than command links, and keeps "OK" and the like
    /// in the given order rather than as the system's buttons.</summary>
    public bool NoLink { get; init; }

    /// <summary>Treats <c>&amp;</c> in button labels as an access key marker for the platform: on Windows the labels are
    /// used as given, which already works so.</summary>
    public bool NormalizeAccessKeys { get; init; }

    /// <summary>macOS only: the width of the message text. Ignored elsewhere.</summary>
    public int TextWidth { get; init; }

    /// <summary>Closes the box when cancelled, which then answers <see cref="CancelId"/>; Electron's
    /// <c>signal</c>.</summary>
    public CancellationToken Signal { get; init; }
}

/// <summary>What a message box returned; Electron's <c>showMessageBox</c> result.</summary>
/// <param name="Response">The index of the button chosen, or the cancel id when dismissed.</param>
/// <param name="CheckboxChecked">The checkbox's state when the box closed.</param>
public sealed record MessageBoxResult(int Response, bool CheckboxChecked);

/// <summary>Message boxes, per Electron's <c>lib/browser/api/dialog.ts</c> and <c>shell/browser/ui/message_box_win.cc</c>,
/// <c>message_box_mac.mm</c> and <c>message_box_gtk.cc</c>.</summary>
public static partial class Dialog
{
    /// <summary>Shows a message box and completes with the answer; other windows keep running. Electron's
    /// <c>dialog.showMessageBox()</c>.</summary>
    /// <remarks>With a window the box is modal to it and the window is disabled until it closes; without one it stands
    /// alone. A window that already shows a modal box is disabled, so a second box for it stands alone too, as in
    /// Electron. On Windows each box runs on its own thread; on macOS and Linux on the UI thread, which keeps running.</remarks>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    /// <exception cref="ArgumentException">An option Electron rejects, such as <see cref="MessageBoxOptions.CheckboxChecked"/>
    /// without a label.</exception>
    /// <exception cref="PlatformNotSupportedException">Linux: GTK 3 cannot be used (drift MB5); the task fails with it.</exception>
    public static Task<MessageBoxResult> ShowMessageBoxAsync(BaseWindow? window, MessageBoxOptions options)
    {
        var settings = PrepareMessageBox(options);
        // Electron answers an already-aborted signal without a box.
        if (options.Signal.IsCancellationRequested) return Task.FromResult(new MessageBoxResult(settings.CancelId, settings.CheckboxChecked));
        if (OperatingSystem.IsMacOS()) return ShowAsyncOnMac(window, options, settings);
        if (OperatingSystem.IsLinux()) return ShowAsyncOnLinux(window, options, settings);
        if (!OperatingSystem.IsWindows()) throw MessageBoxNotYet();
        return ShowAsyncOnWindows(window, options, settings);
    }

    /// <inheritdoc cref="ShowMessageBoxAsync(BaseWindow?, MessageBoxOptions)"/>
    public static Task<MessageBoxResult> ShowMessageBoxAsync(MessageBoxOptions options) => ShowMessageBoxAsync(null, options);

    /// <summary>Shows a message box and waits: the index of the button chosen, or the cancel id. Electron's
    /// <c>dialog.showMessageBoxSync()</c>.</summary>
    /// <remarks>As in Electron the box runs on the UI thread, which waits in it: windows still paint and the owner stays
    /// disabled, but no other UI-thread work runs until it closes. From another thread the call
    /// waits for the UI thread, so it must not come from a thread the UI thread is itself waiting on.</remarks>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    /// <exception cref="ArgumentException">See <see cref="ShowMessageBoxAsync(BaseWindow?, MessageBoxOptions)"/>.</exception>
    /// <exception cref="PlatformNotSupportedException">Linux: GTK 3 cannot be used (drift MB5).</exception>
    public static int ShowMessageBoxSync(BaseWindow? window, MessageBoxOptions options)
    {
        var settings = PrepareMessageBox(options);
        if (options.Signal.IsCancellationRequested) return settings.CancelId;
        if (OperatingSystem.IsMacOS()) return ShowSyncOnMac(window, options, settings);
        if (OperatingSystem.IsLinux()) return ShowSyncOnLinux(window, options, settings);
        if (!OperatingSystem.IsWindows()) throw MessageBoxNotYet();
        return ShowSyncOnWindows(window, options, settings);
    }

    /// <inheritdoc cref="ShowMessageBoxSync(BaseWindow?, MessageBoxOptions)"/>
    public static int ShowMessageBoxSync(MessageBoxOptions options) => ShowMessageBoxSync(null, options);

    /// <summary>Shows an error box, owned by no window, and waits for it to close; usable before <see cref="OrielApp.Ready"/>,
    /// when Linux writes the title and content to stderr instead, as Electron does.
    /// Electron's <c>dialog.showErrorBox()</c>: the window title is "Error", <paramref name="title"/> the highlighted
    /// text and <paramref name="content"/> the text below it.</summary>
    /// <remarks>Once the app is ready the box runs on the UI thread, as Electron's main thread runs it; before then on
    /// the calling thread.</remarks>
    /// <exception cref="PlatformNotSupportedException">Linux: GTK 3 cannot be used (drift MB5).</exception>
    public static void ShowErrorBox(string title, string content)
    {
        if (OperatingSystem.IsMacOS())
        {
            ShowErrorBoxOnMac(title ?? "", content ?? "");
            return;
        }
        if (OperatingSystem.IsLinux())
        {
            ShowErrorBoxOnLinux(title ?? "", content ?? "");
            return;
        }
        if (!OperatingSystem.IsWindows()) throw MessageBoxNotYet();
        ShowErrorBoxOnWindows(ErrorBoxSettingsOf(title, content));
    }

    [SupportedOSPlatform("macos")]
    private static void ShowErrorBoxOnMac(string title, string content)
    {
        if (OrielApp.CurrentOrNull is { IsReady: true } && !CefThread.IsOnUiThread) CefThread.InvokeOnUi(() => ShowError());
        else ShowError();

        bool ShowError()
        {
            MacMessageBox.ShowError(title, content);
            return true;
        }
    }

    // Electron shows the GTK box once ready and before then writes to stderr.
    [SupportedOSPlatform("linux")]
    private static void ShowErrorBoxOnLinux(string title, string content)
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true })
        {
            Console.Error.Write(LinuxMessageBox.ErrorBoxConsoleText(title, content));
            return;
        }
        CefThread.InvokeOnUi(() =>
        {
            LinuxMessageBox.ShowError(title, content);
            return true;
        });
    }

    [SupportedOSPlatform("windows")]
    private static void ShowErrorBoxOnWindows(WindowsMessageBoxSettings settings)
    {
        if (OrielApp.CurrentOrNull is { IsReady: true } && !CefThread.IsOnUiThread)
            CefThread.InvokeOnUi(() => WindowsMessageBox.Show(settings, null));
        else WindowsMessageBox.Show(settings, null);
    }

    // Electron's ShowMessageBoxSync: TaskDialogIndirect straight on the main thread, whose modal loop keeps
    // dispatching window messages while Chromium holds back its tasks.
    [SupportedOSPlatform("windows")]
    private static int ShowSyncOnWindows(BaseWindow? window, MessageBoxOptions options, WindowsMessageBoxSettings settings)
    {
        var full = settings with { Parent = MessageBoxOwnerOf(window), Icon = MessageBoxIconOf(options.Icon) };
        return CefThread.InvokeOnUi(() =>
        {
            var closer = new WindowsMessageBoxCloser();
            using var registration = options.Signal.Register(closer.Close);
            return WindowsMessageBox.Show(full, closer).Response;
        });
    }

    /// <summary>Electron's <c>ShowErrorBox</c> settings: no buttons (the dialog's own OK), the error icon, title
    /// "Error", and the title and content as message and detail.</summary>
    internal static WindowsMessageBoxSettings ErrorBoxSettingsOf(string? title, string? content) => new()
    {
        Type = WindowsMessageBoxType.Error,
        DefaultId = -1,
        CancelId = 0,
        Title = "Error",
        Message = title ?? "",
        Detail = content ?? "",
    };

    /// <summary>The checks and defaults of Electron's <c>messageBox()</c>, in its order: the app must be ready, then the
    /// options are validated and the cancel id chosen.</summary>
    private static WindowsMessageBoxSettings PrepareMessageBox(MessageBoxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (OrielApp.CurrentOrNull is not { IsReady: true }) throw new InvalidOperationException("dialog module can only be used after app is ready");
        return MessageBoxSettingsOf(options, MessageBoxPlatform, MessageBoxAppName);
    }

    /// <summary>Electron's validation and defaults for message box options, for <paramref name="platform"/>
    /// (<c>darwin</c>, <c>linux</c> or <c>win32</c>).</summary>
    /// <exception cref="ArgumentException">What Electron rejects with its message.</exception>
    internal static WindowsMessageBoxSettings MessageBoxSettingsOf(MessageBoxOptions options, string platform, string appName)
    {
        if (!Enum.IsDefined(options.Type)) throw new ArgumentException("Invalid message box type", nameof(options));
        if (options.Buttons is null) throw new ArgumentException("Buttons must be an array", nameof(options));
        if (options.Buttons.Any(b => b is null)) throw new ArgumentException("Buttons must be strings", nameof(options));
        IReadOnlyList<string> buttons = options.NormalizeAccessKeys ? [.. options.Buttons.Select(b => NormalizeAccessKey(b, platform))] : options.Buttons;
        if (options.Title is null) throw new ArgumentException("Title must be a string", nameof(options));
        if (options.Message is null) throw new ArgumentException("Message must be a string", nameof(options));
        if (options.Detail is null) throw new ArgumentException("Detail must be a string", nameof(options));
        if (options.CheckboxLabel is null) throw new ArgumentException("checkboxLabel must be a string", nameof(options));
        if (options.CheckboxChecked && options.CheckboxLabel.Length == 0)
            throw new ArgumentException("checkboxChecked requires that checkboxLabel also be passed", nameof(options));

        return new WindowsMessageBoxSettings
        {
            Type = (WindowsMessageBoxType)options.Type,
            Buttons = buttons,
            DefaultId = options.DefaultId,
            CancelId = options.CancelId ?? CancelIdOf(buttons, options.DefaultId),
            NoLink = options.NoLink,
            Title = options.Title,
            AppName = appName,
            Message = options.Message,
            Detail = options.Detail,
            CheckboxLabel = options.CheckboxLabel,
            CheckboxChecked = options.CheckboxChecked,
        };
    }

    /// <summary>Electron's cancel id when none is given: the first button labelled "cancel" or "no" in any case, else
    /// 1 when the default is button 0 of several, else 0.</summary>
    internal static int CancelIdOf(IReadOnlyList<string> buttons, int defaultId)
    {
        for (var i = 0; i < buttons.Count; i++)
            if (buttons[i].ToLowerInvariant() is "cancel" or "no") return i;
        return defaultId == 0 && buttons.Count > 1 ? 1 : 0;
    }

    /// <summary>Electron's <c>normalizeAccessKey</c>: macOS drops single <c>&amp;</c> and halves doubled ones; Linux
    /// doubles <c>_</c> and turns <c>&amp;x</c> into <c>_x</c> and <c>&amp;&amp;</c> into <c>&amp;</c>; Windows keeps
    /// the label.</summary>
    internal static string NormalizeAccessKey(string text, string platform) => platform switch
    {
        "darwin" => DarwinAccessKey().Replace(text, "$1"),
        "linux" => LinuxAccessKey().Replace(text.Replace("_", "__", StringComparison.Ordinal), m => m.Groups[1].Value == "&" ? "&" : "_" + m.Groups[1].Value),
        _ => text,
    };

    [GeneratedRegex("&(&?)")]
    private static partial Regex DarwinAccessKey();

    [GeneratedRegex("&(.?)")]
    private static partial Regex LinuxAccessKey();

    private static string MessageBoxPlatform => OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux" : "win32";

    private static string MessageBoxAppName => OrielApp.CurrentOrNull?.Name ?? Assembly.GetEntryAssembly()?.GetName().Name ?? "Oriel";

    private static PlatformNotSupportedException MessageBoxNotYet() => new("Message boxes are not implemented on this platform yet.");

    // Electron resolves the owner's HWND on the UI thread before the box's thread starts.
    private static nint MessageBoxOwnerOf(BaseWindow? window) => window is null || window.IsDestroyed ? 0 : window.GetNativeWindowHandle();

    // Electron's HICON comes from the image's 1x bitmap; icon DIBs take straight alpha.
    private static WindowsIconBitmap? MessageBoxIconOf(NativeImage? icon)
    {
        if (icon is null || icon.IsEmpty()) return null;
        var bitmap = ImagePixels.Unpremultiply(icon.BitmapAt(1.0));
        return new WindowsIconBitmap(bitmap.Bgra, bitmap.Width, bitmap.Height);
    }

    // Electron's CreateNSAlert settings: the type, buttons, ids, texts, checkbox, the icon's 1x image and the text width.
    private static MacAlertSettings MacAlertOf(MessageBoxOptions options, WindowsMessageBoxSettings settings)
        => new((int)settings.Type, settings.Buttons, settings.DefaultId, settings.CancelId, settings.Message, settings.Detail,
            settings.CheckboxLabel, settings.CheckboxChecked, options.Icon is { } icon && !icon.IsEmpty() ? icon.ToPng(1.0) : null, options.TextWidth);

    // Electron's mac ShowMessageBox: runModal without a window, which the abort signal cannot reach; else a sheet, which
    // the signal ends with the cancel id, the answer posted to the next UI task as Electron does.
    [SupportedOSPlatform("macos")]
    private static Task<MessageBoxResult> ShowAsyncOnMac(BaseWindow? window, MessageBoxOptions options, WindowsMessageBoxSettings settings)
    {
        var mac = MacAlertOf(options, settings);
        var done = new TaskCompletionSource<MessageBoxResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            var alert = MacMessageBox.Create(mac);
            var view = window is null || window.IsDestroyed ? 0 : window.GetNativeWindowHandle();
            if (view == 0)
            {
                var response = MacMessageBox.RunModal(alert);
                var checkedNow = MacMessageBox.Checked(alert);
                MacMessageBox.Release(alert);
                done.TrySetResult(new MessageBoxResult(response, checkedNow));
                return;
            }
            var registration = options.Signal.Register(() => CefThread.RunOnUi(() => MacMessageBox.EndSheet(alert)));
            MacMessageBox.BeginSheet(alert, view, response =>
            {
                registration.Dispose();
                var answer = new MessageBoxResult(response < 0 ? settings.CancelId : response, MacMessageBox.Checked(alert));
                MacMessageBox.Release(alert);
                CefThread.PostUi(() => done.TrySetResult(answer));
            });
        });
        return done.Task;
    }

    // Electron's mac ShowMessageBoxSync: runModal without a visible window, else a sheet with the window run modally.
    // Cancelling Signal from another thread stops either and answers the cancel id (drift MB2).
    [SupportedOSPlatform("macos")]
    private static int ShowSyncOnMac(BaseWindow? window, MessageBoxOptions options, WindowsMessageBoxSettings settings)
    {
        var mac = MacAlertOf(options, settings);
        return CefThread.InvokeOnUi(() =>
        {
            var alert = MacMessageBox.Create(mac);
            var view = window is null || window.IsDestroyed || !window.IsVisible ? 0 : window.GetNativeWindowHandle();
            var aborted = false;
            // The UI thread waits in AppKit's modal loop, where CEF's tasks do not run, so the abort goes straight to it.
            using var registration = options.Signal.Register(() => MacMessageBox.PostToMainLoop(() =>
            {
                aborted = true;
                if (view == 0) MacMessageBox.AbortModal();
                else MacMessageBox.EndSheet(alert);
            }));
            var response = view == 0 ? MacMessageBox.RunModal(alert) : MacMessageBox.RunSheetModal(alert, view);
            MacMessageBox.Release(alert);
            return aborted ? settings.CancelId : response;
        });
    }

    // Electron's ShowMessageBox: a dedicated COM STA thread per box, the answer delivered on the UI thread.
    [SupportedOSPlatform("windows")]
    private static Task<MessageBoxResult> ShowAsyncOnWindows(BaseWindow? window, MessageBoxOptions options, WindowsMessageBoxSettings settings)
    {
        var full = settings with { Parent = MessageBoxOwnerOf(window), Icon = MessageBoxIconOf(options.Icon) };
        var closer = new WindowsMessageBoxCloser();
        var registration = options.Signal.Register(closer.Close);
        var done = new TaskCompletionSource<MessageBoxResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var (response, checkboxChecked) = WindowsMessageBox.Show(full, closer);
            void Finish()
            {
                registration.Dispose();
                done.TrySetResult(new MessageBoxResult(response, checkboxChecked));
            }
            try { CefThread.PostUi(Finish); }
            catch (InvalidOperationException) { Finish(); }
        }) { IsBackground = true, Name = "Oriel message box" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    // Electron's GtkMessageBox settings: the parent's X11 window and the icon's 1x bitmap as straight RGBA.
    [SupportedOSPlatform("linux")]
    private static GtkMessageBoxSettings GtkSettingsOf(BaseWindow? window, MessageBoxOptions options, WindowsMessageBoxSettings settings)
        => new((int)settings.Type, settings.Buttons, settings.DefaultId, settings.CancelId, settings.Title, settings.Message, settings.Detail,
            settings.CheckboxLabel, settings.CheckboxChecked, GtkIconOf(options.Icon), MessageBoxOwnerOf(window));

    private static GtkIconBitmap? GtkIconOf(NativeImage? icon)
    {
        if (icon is null || icon.IsEmpty()) return null;
        var bitmap = ImagePixels.Unpremultiply(icon.BitmapAt(1.0));
        var rgba = bitmap.Bgra;
        for (var i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        return new GtkIconBitmap(rgba, bitmap.Width, bitmap.Height);
    }

    // Electron's GTK ShowMessageBox: the box shows and the UI thread carries on. The abort goes through the GLib loop,
    // which also turns while a Sync box holds the UI thread, where CEF tasks would wait.
    [SupportedOSPlatform("linux")]
    private static Task<MessageBoxResult> ShowAsyncOnLinux(BaseWindow? window, MessageBoxOptions options, WindowsMessageBoxSettings settings)
    {
        var gtk = GtkSettingsOf(window, options, settings);
        var done = new TaskCompletionSource<MessageBoxResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            LinuxMessageBox box;
            try { box = new LinuxMessageBox(gtk); }
            catch (PlatformNotSupportedException e)
            {
                done.TrySetException(e);
                return;
            }
            var registration = options.Signal.Register(() => LinuxMessageBox.PostToMainLoop(box.Close));
            // Electron's GtkMessageBox goes through SetEnabled, so closing the box also re-enables a window the app disabled.
            window?.SetEnabledOnUi(false);
            box.RunAsynchronous((response, checkboxChecked) =>
            {
                window?.SetEnabledOnUi(true);
                registration.Dispose();
                done.TrySetResult(new MessageBoxResult(response, checkboxChecked));
            });
        });
        return done.Task;
    }

    // Electron's GTK ShowMessageBoxSync: gtk_dialog_run on the UI thread, whose nested GLib loop runs no CEF tasks.
    // Cancelling Signal from another thread reaches it through that loop (drift MB2).
    [SupportedOSPlatform("linux")]
    private static int ShowSyncOnLinux(BaseWindow? window, MessageBoxOptions options, WindowsMessageBoxSettings settings)
    {
        var gtk = GtkSettingsOf(window, options, settings);
        return CefThread.InvokeOnUi(() =>
        {
            var box = new LinuxMessageBox(gtk);
            using var registration = options.Signal.Register(() => LinuxMessageBox.PostToMainLoop(box.Close));
            window?.SetEnabledOnUi(false);
            try { return box.RunSynchronous(); }
            finally { window?.SetEnabledOnUi(true); }
        });
    }
}
