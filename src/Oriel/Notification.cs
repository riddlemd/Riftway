using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>An action on a <see cref="Notification"/>; Electron's <c>NotificationAction</c>.</summary>
public sealed record NotificationAction
{
    /// <summary>"button", or (Windows only) "selection" for a drop-down of <see cref="Items"/> with a submit button.
    /// Other types are left out of the toast.</summary>
    public string Type { get; init; } = "button";

    /// <summary>The button's label; for a selection, the submit button's label ("Select" when empty).</summary>
    public string Text { get; init; } = "";

    /// <summary>Windows only: the choices of a "selection" action.</summary>
    public IReadOnlyList<string> Items { get; init; } = [];
}

/// <summary>Which action was used; Electron's <c>action</c> event details.</summary>
/// <param name="ActionIndex">The action's index in <see cref="Notification.Actions"/>.</param>
/// <param name="SelectionIndex">Windows: the chosen item of a "selection" action, or -1 if none was chosen.</param>
public sealed record NotificationActionEventArgs(int ActionIndex, int SelectionIndex);

/// <summary>A toast activation as <see cref="Notification.HandleActivation"/> reports it; Electron's
/// <c>ActivationArguments</c>.</summary>
/// <param name="Type">"click", "action" or "reply".</param>
/// <param name="Arguments">The raw activation arguments from Windows.</param>
public sealed record ActivationArguments(string Type, string Arguments)
{
    /// <summary>For "action": the index of the action used.</summary>
    public int? ActionIndex { get; init; }

    /// <summary>For "reply": the text entered.</summary>
    public string? Reply { get; init; }

    /// <summary>Every input's value by id (the reply box, selections); null when there were none.</summary>
    public IReadOnlyDictionary<string, string>? UserInputs { get; init; }
}

/// <summary>Why a <see cref="Notification"/> closed.</summary>
public enum NotificationCloseReason
{
    /// <summary>The user dismissed it.</summary>
    UserCanceled,

    /// <summary>The app hid it.</summary>
    ApplicationHidden,

    /// <summary>It timed out (into the Action Center on Windows).</summary>
    TimedOut,
}

/// <summary>A desktop notification; Electron's <c>Notification</c>. Windows shows it as a toast from
/// <see cref="OrielApp.AppUserModelId"/>. As in Electron, the first notification (or <see cref="IsSupported"/>) of an
/// unpackaged app writes a Start-menu shortcut named after the executable's product name and registers the toast
/// activator CLSID under HKCU, so that Windows shows the app's toasts and delivers their buttons. Linux sends it to
/// <c>org.freedesktop.Notifications</c> on the session bus as libnotify does, waiting for the server's answer.
/// Use it on the UI thread; its events arrive there.</summary>
/// <remarks>A shown notification stays alive until it is clicked, closed or fails, even with no reference to it.</remarks>
public sealed class Notification
{
    private const int MaxWindowsIdLength = 64;
    private const string DefaultGroup = "Notifications";

    private static readonly List<Notification> s_live = [];
    private static readonly Lock s_activationLock = new();
    private static Action<ActivationArguments>? s_activationHandler;
    private static ActivationArguments? s_pendingActivation;

    private readonly string _id = Guid.NewGuid().ToString();
    private readonly string _groupId = "";
    private object? _handle;
    private Sink? _sink;
    private int _showAttempt;
    private bool _dismissed;

    internal static INotificationBackend Backend { get; set; } = OperatingSystem.IsWindows()
        ? new ToastNotificationBackend()
        : OperatingSystem.IsLinux()
            ? new LinuxNotificationBackend()
            : new UnsupportedNotificationBackend();

    internal static Func<bool> IsAppReady { get; set; } = () => OrielApp.CurrentOrNull is { IsReady: true };

    /// <summary>Creates a notification; as Electron's constructor does, readies the platform's notification service
    /// (on Windows, the toast activator).</summary>
    /// <exception cref="InvalidOperationException">The app is not ready yet, as in Electron.</exception>
    public Notification()
    {
        if (!IsAppReady()) throw new InvalidOperationException("Cannot create Notification before app is ready");
        if (OrielApp.CurrentOrNull is not null) Backend.Prepare();
    }

    /// <summary>The notification's identifier: the toast's <c>Tag</c> on Windows. A random lowercase UUID when not set or
    /// set to "". Electron's <c>id</c>.</summary>
    /// <exception cref="ArgumentException">On Windows, longer than 64 UTF-16 characters.</exception>
    public string Id
    {
        get => _id;
        init => _id = value is { Length: > 0 } ? CheckWindowsLength(value, "id") : Guid.NewGuid().ToString();
    }

    /// <summary>Groups notifications: the toast's <c>Group</c> on Windows ("Notifications" when empty). Electron's
    /// <c>groupId</c>.</summary>
    /// <exception cref="ArgumentException">On Windows, longer than 64 UTF-16 characters.</exception>
    public string GroupId
    {
        get => _groupId;
        init => _groupId = CheckWindowsLength(value ?? "", "groupId");
    }

    /// <summary>Windows: with <see cref="GroupId"/>, a header above the group's toasts. Electron's <c>groupTitle</c>.</summary>
    public string GroupTitle { get; init; } = "";

    /// <summary>The heading.</summary>
    public string Title { get; set; } = "";

    /// <summary>The text under the title.</summary>
    public string Body { get; set; } = "";

    /// <summary>macOS only: a line under the title.</summary>
    public string Subtitle { get; set; } = "";

    /// <summary>Plays no sound.</summary>
    public bool Silent { get; set; }

    /// <summary>A path to a PNG or JPEG shown beside the text; Windows gets a PNG copy of it, as Electron gives it one.
    /// Electron's <c>icon</c> given as a string.</summary>
    public string? Icon { get; set; }

    /// <summary>The image shown beside the text, instead of <see cref="Icon"/>; Electron's <c>icon</c> given as a
    /// <c>NativeImage</c>. An empty image shows none.</summary>
    public NativeImage? IconImage { get; set; }

    /// <summary>Adds a reply box and a "Reply" button; <see cref="Reply"/> reports the text. Electron's <c>hasReply</c>.</summary>
    public bool HasReply { get; set; }

    /// <summary>The reply box's placeholder. Electron's <c>replyPlaceholder</c>.</summary>
    public string ReplyPlaceholder { get; set; } = "";

    /// <summary>macOS only: the sound's name. Electron's <c>sound</c>.</summary>
    public string Sound { get; set; } = "";

    /// <summary>"normal", "critical" or "low"; on Windows "critical" raises the toast's priority. Electron's <c>urgency</c>.</summary>
    public string Urgency { get; set; } = "";

    /// <summary>"default", or "never" to stay until dismissed (a reminder toast with a Close button on Windows).</summary>
    public string TimeoutType { get; set; } = "default";

    /// <summary>Buttons and selections; <see cref="Action"/> reports which was used.</summary>
    public IList<NotificationAction> Actions { get; set; } = [];

    /// <summary>macOS only: the close button's label. Electron's <c>closeButtonText</c>.</summary>
    public string CloseButtonText { get; set; } = "";

    /// <summary>Windows only: toast XML to show instead of the generated one; Electron's <c>toastXml</c>.</summary>
    public string? ToastXml { get; set; }

    /// <summary>Whether the notification is on screen (shown and not yet dismissed, clicked, closed or failed).</summary>
    public bool IsShown => _handle is not null && !_dismissed;

    /// <summary>The notification was shown; Electron's <c>show</c>. On Windows raised on the UI thread after
    /// <see cref="Show"/> returns, as Electron posts it, and not if <see cref="Close"/> came first; on Linux raised inside
    /// <see cref="Show"/>, as Electron's synchronous libnotify call does.</summary>
    public event EventHandler? Shown;

    /// <summary>The user clicked the notification's body; Electron's <c>click</c>. On Linux, the server's "default"
    /// action, offered only when it lists the "actions" capability.</summary>
    public event EventHandler? Click;

    /// <summary>The user used an action; Electron's <c>action</c>. Never on Linux, where Electron sends no actions.</summary>
    public event EventHandler<NotificationActionEventArgs>? Action;

    /// <summary>The user sent a reply; the argument is its text. Electron's <c>reply</c>. Never on Linux.</summary>
    public event EventHandler<string>? Reply;

    /// <summary>The notification was dismissed by the user, timed out, or was hidden by the system; Electron's
    /// <c>close</c>. <see cref="Close"/> does not raise it, as on Electron for Windows and Linux. On Linux the
    /// notification ends with it, as Electron destroys it.</summary>
    public event EventHandler<NotificationCloseReason>? Closed;

    /// <summary>The OS could not show it; the argument is Electron's error text. Electron's <c>failed</c>. A refused
    /// <see cref="Show"/> raises it after returning on Windows, as Electron posts it, and inside it with "" on Linux.</summary>
    public event EventHandler<string>? Failed;

    /// <summary>Whether desktop notifications work here; Electron's <c>Notification.isSupported()</c>. Always true on
    /// Linux, which needs no libnotify.</summary>
    public static bool IsSupported()
    {
        if (OrielApp.CurrentOrNull is not null) Backend.Prepare();
        return Backend.IsSupported;
    }

    /// <summary>Windows: calls <paramref name="callback"/> for every click, action and reply that reaches the app, whether
    /// or not its <see cref="Notification"/> is still alive, and at once for one that arrived before any callback was set.
    /// Replaces the previous callback. Electron's <c>Notification.handleActivation()</c>.</summary>
    public static void HandleActivation(Action<ActivationArguments> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ActivationArguments? pending;
        lock (s_activationLock)
        {
            s_activationHandler = callback;
            pending = s_pendingActivation;
            s_pendingActivation = null;
        }
        if (pending is not null) callback(pending);
    }

    internal static void ResetActivationHandler()
    {
        lock (s_activationLock)
        {
            s_activationHandler = null;
            s_pendingActivation = null;
        }
    }

    /// <summary>Shows the notification, closing it first if it is already shown. Raises <see cref="Failed"/> rather than
    /// throwing when the OS refuses.</summary>
    /// <exception cref="InvalidOperationException">On Windows, <see cref="GroupTitle"/> is set without <see cref="GroupId"/>.</exception>
    public void Show()
    {
        if (OperatingSystem.IsWindows() && GroupTitle.Length > 0 && GroupId.Length == 0)
            throw new InvalidOperationException("Notification groupTitle requires groupId to be set");
        Close();
        var attempt = ++_showAttempt;
        var sink = new Sink(this);
        try
        {
            _handle = Backend.Show(this, sink);
            _sink = sink;
        }
        catch (Exception e) when (e is not PlatformNotSupportedException)
        {
            var error = e.Message;
            if (!Backend.PostsShowEvents)
            {
                Failed?.Invoke(this, error);
                return;
            }
            // Electron reports the failure from a task posted to the UI thread, unless the notification moved on.
            Backend.Post(() =>
            {
                if (_showAttempt == attempt) Failed?.Invoke(this, error);
            });
            return;
        }
        _dismissed = false;
        s_live.Add(this);
        if (!Backend.PostsShowEvents)
        {
            Shown?.Invoke(this, EventArgs.Empty);
            return;
        }
        // Electron emits show from a posted task, and not at all once close() has run in between.
        Backend.Post(() =>
        {
            if (_sink == sink) Shown?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>What a toast backend shows for <paramref name="n"/>: the generated XML (with the icon
    /// <paramref name="saveIcon"/> copies) or <see cref="ToastXml"/>, the tag, the group and the priority.</summary>
    internal static ToastRequest Toast(Notification n, Func<string?, NativeImage?, string?> saveIcon)
    {
        var xml = string.IsNullOrEmpty(n.ToastXml)
            ? BuildToastXml(n, n.IconImage is not null || n.Icon is { Length: > 0 } ? saveIcon(n.Icon, n.IconImage) : null)
            : n.ToastXml;
        return new ToastRequest(xml, n.Id, n.GroupId.Length > 0 ? n.GroupId : DefaultGroup, n.Urgency == "critical");
    }

    /// <summary>Hides the notification, or once it has been dismissed into the Action Center removes it from there.
    /// Raises nothing, and the notification's events stop.</summary>
    public void Close()
    {
        _showAttempt++;
        if (_handle is null) return;
        if (_dismissed) Backend.Remove(_handle);
        else Backend.Hide(_handle);
        Forget();
    }

    private void Forget()
    {
        Backend.Release(_handle!);
        _handle = null;
        _sink = null;
        _dismissed = false;
        s_live.Remove(this);
    }

    private static string CheckWindowsLength(string value, string option)
        => OperatingSystem.IsWindows() && value.Length > MaxWindowsIdLength
            ? throw new ArgumentException($"Notification {option} exceeds Windows limit of 64 UTF-16 characters", option)
            : value;

    /// <summary>The toast's own Activated event: Electron sends action and reply arguments down the activator's path and
    /// takes anything else as a click.</summary>
    internal void OnActivated(string arguments, IReadOnlyList<KeyValuePair<string, string>> userInputs)
    {
        if (arguments.Contains("type=action", StringComparison.Ordinal) || arguments.Contains("type=reply", StringComparison.Ordinal))
            HandleToastActivation(arguments, userInputs);
        else
            OnClicked();
    }

    private void OnClicked()
    {
        if (_handle is null) return;
        Click?.Invoke(this, EventArgs.Empty);
        if (_handle is not null) Forget();
    }

    /// <summary>An activation from the toast or the COM activator: "type=…&amp;action=…&amp;tag=…" plus the inputs' values.
    /// Reports it to <see cref="HandleActivation"/>'s callback, then to the live notification whose <see cref="Id"/> is the
    /// tag. Electron's <c>HandleToastActivation()</c>. UI thread.</summary>
    internal static void HandleToastActivation(string invokedArgs, IReadOnlyList<KeyValuePair<string, string>> userInputs)
    {
        string type = "", actionText = "", tag = "";
        foreach (var token in invokedArgs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = token.Split('=', StringSplitOptions.RemoveEmptyEntries);
            if (kv.Length != 2) continue;
            if (kv[0] == "type") type = kv[1];
            else if (kv[0] == "action") actionText = kv[1];
            else if (kv[0] == "tag") tag = kv[1];
        }
        var actionIndex = -1;
        if (actionText.Length > 0 && !int.TryParse(actionText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out actionIndex))
            actionIndex = -1;
        var replyText = "";
        foreach (var (key, value) in userInputs)
            if (key == "reply") replyText = value;

        var inputs = new Dictionary<string, string>();
        foreach (var (key, value) in userInputs) inputs[key] = value;
        var activation = type == "action"
            ? new ActivationArguments("action", invokedArgs) { ActionIndex = actionIndex }
            : type == "reply" || replyText.Length > 0
                ? new ActivationArguments("reply", invokedArgs) { Reply = replyText }
                : new ActivationArguments("click", invokedArgs);
        if (inputs.Count > 0) activation = activation with { UserInputs = inputs };

        Action<ActivationArguments>? handler;
        lock (s_activationLock)
        {
            handler = s_activationHandler;
            s_pendingActivation = handler is null ? activation : null;
        }
        handler?.Invoke(activation);

        var target = s_live.Find(n => n.Id == tag);
        if (target is null) return;
        if (type == "action")
        {
            var selectionIndex = -1;
            foreach (var (key, value) in userInputs)
            {
                if (!key.StartsWith("selection", StringComparison.Ordinal)) continue;
                if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
                    selectionIndex = parsed;
                break;
            }
            if (actionIndex >= 0) target.Action?.Invoke(target, new NotificationActionEventArgs(actionIndex, selectionIndex));
        }
        else if (type == "reply" || (replyText.Length > 0 && type.Length == 0))
            target.Reply?.Invoke(target, replyText);
        else if (type is "click" or "")
            target.OnClicked();
    }

    internal void OnDismissed(NotificationCloseReason reason)
    {
        if (_handle is not { } handle) return;
        _dismissed = true;
        Closed?.Invoke(this, reason);
        // A close handler may have shown it again; that handle is not this one's to drop.
        if (!Backend.KeepsDismissed && ReferenceEquals(_handle, handle)) Forget();
    }

    internal void OnFailed(string error)
    {
        if (_handle is null) return;
        Forget();
        Failed?.Invoke(this, error);
    }

    /// <summary>The toast XML Electron's <c>GetToastXml()</c> generates; <paramref name="iconPath"/> is the PNG copy of
    /// the icon, or null for none.</summary>
    internal static string BuildToastXml(Notification n, string? iconPath)
    {
        static string E(string s) => SecurityElement.Escape(s);
        var tag = E(n.Id);
        var reminder = n.TimeoutType == "never";
        var x = new StringBuilder("<toast");
        if (reminder) x.Append(" scenario=\"reminder\"");
        x.Append('>');
        if (n.GroupId.Length > 0 && n.GroupTitle.Length > 0)
            x.Append("<header id=\"").Append(E(n.GroupId)).Append("\" title=\"").Append(E(n.GroupTitle)).Append("\" arguments=\"\"/>");
        x.Append("<visual><binding template=\"ToastGeneric\">");
        if (n.Title.Length == 0 || n.Body.Length == 0)
        {
            var line = n.Title.Length == 0 ? n.Body : n.Title;
            x.Append("<text>").Append(E(line.Length == 0 ? "[no message]" : line)).Append("</text>");
        }
        else
            x.Append("<text>").Append(E(n.Title)).Append("</text><text>").Append(E(n.Body)).Append("</text>");
        if (iconPath is { Length: > 0 })
            x.Append("<image id=\"1\" placement=\"appLogoOverride\" hint-crop=\"none\" src=\"").Append(E(iconPath)).Append("\"/>");
        x.Append("</binding></visual>");
        if (reminder || n.HasReply || n.Actions.Count > 0)
        {
            x.Append("<actions>");
            if (reminder) x.Append("<action activationType=\"system\" arguments=\"dismiss\" content=\"Close\"/>");
            if (n.HasReply)
            {
                x.Append("<input id=\"reply\" type=\"text\"");
                if (n.ReplyPlaceholder.Length > 0) x.Append(" placeHolderContent=\"").Append(E(n.ReplyPlaceholder)).Append('"');
                x.Append("/>");
            }
            for (var i = 0; i < n.Actions.Count; i++)
            {
                var action = n.Actions[i];
                var index = i.ToString(CultureInfo.InvariantCulture);
                var arguments = "type=action&amp;action=" + index + "&amp;tag=" + tag;
                if (action.Type is "button" or "")
                    x.Append("<action activationType=\"foreground\" arguments=\"").Append(arguments).Append("\" content=\"").Append(E(action.Text)).Append("\"/>");
                else if (action.Type == "selection")
                {
                    x.Append("<input id=\"selection").Append(index).Append("\" type=\"selection\"");
                    if (action.Items.Count == 0) x.Append("/>");
                    else
                    {
                        x.Append('>');
                        for (var j = 0; j < action.Items.Count; j++)
                            x.Append("<selection id=\"").Append(j.ToString(CultureInfo.InvariantCulture)).Append("\" content=\"").Append(E(action.Items[j])).Append("\"/>");
                        x.Append("</input>");
                    }
                    x.Append("<action activationType=\"foreground\" arguments=\"").Append(arguments).Append("\" content=\"")
                        .Append(E(action.Text.Length == 0 ? "Select" : action.Text)).Append("\" hint-inputId=\"selection").Append(index).Append("\"/>");
                }
            }
            if (n.HasReply)
                x.Append("<action activationType=\"foreground\" arguments=\"type=reply&amp;tag=").Append(tag)
                    .Append("\" content=\"Reply\" hint-inputId=\"reply\"/>");
            x.Append("</actions>");
        }
        if (n.Silent) x.Append("<audio silent=\"true\"/>");
        return x.Append("</toast>").ToString();
    }

    // One per Show: a toast replaced by a later Show must not speak for the new one.
    private sealed class Sink(Notification owner) : INotificationSink
    {
        private bool Current => owner._sink == this;
        public void Activated(string arguments, IReadOnlyList<KeyValuePair<string, string>> userInputs)
        {
            if (Current) owner.OnActivated(arguments, userInputs);
        }
        public void Dismissed(NotificationCloseReason reason)
        {
            if (Current) owner.OnDismissed(reason);
        }
        public void Failed(string error)
        {
            if (Current) owner.OnFailed(error);
        }
    }
}

/// <summary>Where a backend reports what happened to a shown notification; called on the UI thread.</summary>
internal interface INotificationSink
{
    void Activated(string arguments, IReadOnlyList<KeyValuePair<string, string>> userInputs);
    void Dismissed(NotificationCloseReason reason);
    void Failed(string error);
}

/// <summary>A toast as <see cref="Notification.Toast"/> builds it.</summary>
internal readonly record struct ToastRequest(string Xml, string Tag, string Group, bool HighPriority);

/// <summary>A platform's notification service behind <see cref="Notification"/>.</summary>
internal interface INotificationBackend
{
    bool IsSupported { get; }

    /// <summary>Whether <c>show</c> and <c>failed</c> are posted to the UI thread (Windows) rather than raised inside
    /// <see cref="Notification.Show"/> (Linux, where libnotify shows synchronously).</summary>
    bool PostsShowEvents { get; }

    /// <summary>Whether a dismissed notification stays until <see cref="Notification.Close"/> (Windows' Action Center)
    /// rather than ending with its <c>close</c> event (Linux).</summary>
    bool KeepsDismissed { get; }

    /// <summary>Readies the service once per process: Electron's notification presenter creation.</summary>
    void Prepare();

    /// <summary>Runs <paramref name="action"/> later on the UI thread.</summary>
    void Post(Action action);

    /// <summary>Shows <paramref name="notification"/>; returns a handle, or throws with Electron's failure text if
    /// refused.</summary>
    object Show(Notification notification, INotificationSink sink);

    /// <summary>Takes a shown notification off the screen.</summary>
    void Hide(object handle);

    /// <summary>Removes a dismissed notification from the notification center.</summary>
    void Remove(object handle);

    /// <summary>Lets go of a handle; the sink hears nothing more.</summary>
    void Release(object handle);
}

/// <summary>WinRT toast backend.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ToastNotificationBackend : INotificationBackend
{
    private static readonly Lock s_iconLock = new();
    private static string? s_iconDirectory;

    public bool IsSupported => WindowsToast.IsSupported();

    public bool PostsShowEvents => true;

    public bool KeepsDismissed => true;

    public void Prepare() => WindowsToastActivator.Register((arguments, inputs) => OnUi(() => Notification.HandleToastActivation(arguments, inputs)));

    /// <summary>Electron re-encodes the icon as a PNG named for the current microsecond in a temporary directory that
    /// lives as long as the process.</summary>
    public string? SaveIcon(string? path, NativeImage? image)
    {
        var png = (image ?? NativeImage.CreateFromPath(path ?? "")).ToPng();
        if (png.Length == 0) return null;
        lock (s_iconLock)
        {
            if (s_iconDirectory is null)
            {
                var directory = Path.Combine(Path.GetTempPath(), $"scoped_dir{Environment.ProcessId}_{Random.Shared.Next():D9}");
                Directory.CreateDirectory(directory);
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    try { Directory.Delete(directory, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                };
                s_iconDirectory = directory;
            }
            // Microseconds since 1601, base::Time's origin on Windows.
            var file = Path.Combine(s_iconDirectory, (DateTime.UtcNow.ToFileTimeUtc() / 10).ToString(CultureInfo.InvariantCulture) + ".png");
            File.WriteAllBytes(file, png);
            return file;
        }
    }

    public void Post(Action action) => CefThread.PostUi(action);

    public object Show(Notification notification, INotificationSink sink)
    {
        var toast = Notification.Toast(notification, SaveIcon);
        var events = new Events(sink) { AppId = WindowsAppIdentity.AppUserModelId, Tag = toast.Tag, Group = toast.Group };
        events.Handle = WindowsToast.Show(events.AppId, toast.Xml, toast.Tag, toast.Group, toast.HighPriority, events);
        return events;
    }

    public void Hide(object handle) => WindowsToast.Hide(((Events)handle).Handle);

    public void Remove(object handle)
    {
        var events = (Events)handle;
        WindowsToast.RemoveFromHistory(events.AppId, events.Tag, events.Group);
    }

    public void Release(object handle)
    {
        var events = (Events)handle;
        events.Released = true;
        WindowsToast.Release(events.Handle);
    }

    // Activations can arrive before CEF runs (a toast pressed while the app starts); then there is no UI thread to post to.
    private static void OnUi(Action action)
    {
        if (OrielApp.CurrentOrNull is { IsReady: true }) CefThread.PostUi(action);
        else action();
    }

    /// <summary>Moves WinRT's callbacks onto the UI thread, and drops them once released.</summary>
    private sealed class Events(INotificationSink sink) : IToastEvents
    {
        public nint Handle;
        public volatile bool Released;
        public required string AppId, Tag, Group;

        public void Activated(string arguments, IReadOnlyList<KeyValuePair<string, string>> userInputs) => Post(() => sink.Activated(arguments, userInputs));

        public void Dismissed(ToastDismissal reason) => Post(() => sink.Dismissed(reason switch
        {
            ToastDismissal.ApplicationHidden => NotificationCloseReason.ApplicationHidden,
            ToastDismissal.TimedOut => NotificationCloseReason.TimedOut,
            _ => NotificationCloseReason.UserCanceled,
        }));

        public void Failed(int errorCode) => Post(() => sink.Failed(WindowsToast.FailureResultToString(errorCode)));

        private void Post(Action a) => CefThread.PostUi(() =>
        {
            if (!Released) a();
        });
    }
}

/// <summary>Backend for platforms without an implementation yet.</summary>
internal sealed class UnsupportedNotificationBackend : INotificationBackend
{
    public bool IsSupported => false;

    public bool PostsShowEvents => true;

    public bool KeepsDismissed => true;

    public void Prepare() { }

    public void Post(Action action) => action();

    public object Show(Notification notification, INotificationSink sink)
        => throw new PlatformNotSupportedException("Notification is not implemented on this platform.");

    public void Hide(object handle) { }

    public void Remove(object handle) { }

    public void Release(object handle) { }
}
