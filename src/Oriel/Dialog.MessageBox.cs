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

/// <summary>Options for <see cref="Dialog.ShowMessageBoxAsync(BrowserWindow?, MessageBoxOptions)"/>; Electron's
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

    /// <summary>The window title; empty uses the app's name.</summary>
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

/// <summary>Message boxes, per Electron's <c>lib/browser/api/dialog.ts</c> and <c>shell/browser/ui/message_box_win.cc</c>.</summary>
public static partial class Dialog
{
    /// <summary>Shows a message box and completes with the answer; other windows keep running. Electron's
    /// <c>dialog.showMessageBox()</c>.</summary>
    /// <remarks>With a window the box is modal to it and the window is disabled until it closes; without one it stands
    /// alone. A window that already shows a modal box is disabled, so a second box for it stands alone too, as in
    /// Electron. Each box runs on its own thread.</remarks>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    /// <exception cref="ArgumentException">An option Electron rejects, such as <see cref="MessageBoxOptions.CheckboxChecked"/>
    /// without a label.</exception>
    /// <exception cref="PlatformNotSupportedException">Not on Windows yet.</exception>
    public static Task<MessageBoxResult> ShowMessageBoxAsync(BrowserWindow? window, MessageBoxOptions options)
    {
        var settings = PrepareMessageBox(options);
        // Electron answers an already-aborted signal without a box.
        if (options.Signal.IsCancellationRequested) return Task.FromResult(new MessageBoxResult(settings.CancelId, settings.CheckboxChecked));
        if (!OperatingSystem.IsWindows()) throw MessageBoxNotYet();
        return ShowAsyncOnWindows(window, options, settings);
    }

    /// <inheritdoc cref="ShowMessageBoxAsync(BrowserWindow?, MessageBoxOptions)"/>
    public static Task<MessageBoxResult> ShowMessageBoxAsync(MessageBoxOptions options) => ShowMessageBoxAsync(null, options);

    /// <summary>Shows a message box and waits: the index of the button chosen, or the cancel id. Electron's
    /// <c>dialog.showMessageBoxSync()</c>.</summary>
    /// <remarks>As in Electron the box runs on the UI thread, which waits in it: windows still paint and the owner stays
    /// disabled, but no other UI-thread work runs until it closes. From another thread the call waits for the UI
    /// thread, so it must not come from a thread the UI thread is itself waiting on.</remarks>
    /// <exception cref="InvalidOperationException">Before <see cref="OrielApp.Ready"/>.</exception>
    /// <exception cref="ArgumentException">See <see cref="ShowMessageBoxAsync(BrowserWindow?, MessageBoxOptions)"/>.</exception>
    /// <exception cref="PlatformNotSupportedException">Not on Windows yet.</exception>
    public static int ShowMessageBoxSync(BrowserWindow? window, MessageBoxOptions options)
    {
        var settings = PrepareMessageBox(options);
        if (options.Signal.IsCancellationRequested) return settings.CancelId;
        if (!OperatingSystem.IsWindows()) throw MessageBoxNotYet();
        return ShowSyncOnWindows(window, options, settings);
    }

    /// <inheritdoc cref="ShowMessageBoxSync(BrowserWindow?, MessageBoxOptions)"/>
    public static int ShowMessageBoxSync(MessageBoxOptions options) => ShowMessageBoxSync(null, options);

    /// <summary>Shows an error box, owned by no window, and waits for it to close; usable before <see cref="OrielApp.Ready"/>.
    /// Electron's <c>dialog.showErrorBox()</c>: the window title is "Error", <paramref name="title"/> the highlighted
    /// text and <paramref name="content"/> the text below it.</summary>
    /// <remarks>Once the app is ready the box runs on the UI thread, as Electron's main thread runs it; before then on
    /// the calling thread.</remarks>
    /// <exception cref="PlatformNotSupportedException">Not on Windows yet.</exception>
    public static void ShowErrorBox(string title, string content)
    {
        if (!OperatingSystem.IsWindows()) throw MessageBoxNotYet();
        ShowErrorBoxOnWindows(ErrorBoxSettingsOf(title, content));
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
    private static int ShowSyncOnWindows(BrowserWindow? window, MessageBoxOptions options, WindowsMessageBoxSettings settings)
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
    private static nint MessageBoxOwnerOf(BrowserWindow? window) => window is null || window.IsDestroyed ? 0 : window.GetNativeWindowHandle();

    // Electron's HICON comes from the image's 1x bitmap; icon DIBs take straight alpha.
    private static WindowsIconBitmap? MessageBoxIconOf(NativeImage? icon)
    {
        if (icon is null || icon.IsEmpty()) return null;
        var bitmap = ImagePixels.Unpremultiply(icon.BitmapAt(1.0));
        return new WindowsIconBitmap(bitmap.Bgra, bitmap.Width, bitmap.Height);
    }

    // Electron's ShowMessageBox: a dedicated COM STA thread per box, the answer delivered on the UI thread.
    [SupportedOSPlatform("windows")]
    private static Task<MessageBoxResult> ShowAsyncOnWindows(BrowserWindow? window, MessageBoxOptions options, WindowsMessageBoxSettings settings)
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
}
