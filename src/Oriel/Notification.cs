using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>A button on a <see cref="Notification"/>; Electron's <c>NotificationAction</c> (type "button").</summary>
public sealed record NotificationAction(string Text);

/// <summary>Why a <see cref="Notification"/> closed.</summary>
public enum NotificationCloseReason
{
    /// <summary>The user dismissed it.</summary>
    UserCanceled,

    /// <summary><see cref="Notification.Close"/> took it away.</summary>
    ApplicationHidden,

    /// <summary>It timed out (into the Action Center on Windows).</summary>
    TimedOut,
}

/// <summary>A desktop notification; Electron's <c>Notification</c>. Windows shows it as a toast from
/// <see cref="OrielApp.AppUserModelId"/>, which needs a Start-menu shortcut or an <c>AppUserModelId</c> registry entry
/// carrying that id, as Electron does. Use it on the UI thread; its events arrive there.</summary>
/// <remarks>A shown notification stays alive until it closes or fails, even with no reference to it.</remarks>
public sealed class Notification
{
    private static readonly HashSet<Notification> s_live = [];

    private object? _handle;

    internal static INotificationBackend Backend { get; set; } = OperatingSystem.IsWindows()
        ? new ToastNotificationBackend()
        : new UnsupportedNotificationBackend();

    /// <summary>The heading.</summary>
    public string Title { get; set; } = "";

    /// <summary>The text under the title.</summary>
    public string Body { get; set; } = "";

    /// <summary>macOS only: a line under the title.</summary>
    public string Subtitle { get; set; } = "";

    /// <summary>Plays no sound.</summary>
    public bool Silent { get; set; }

    /// <summary>A path to an image file shown beside the text.</summary>
    public string? Icon { get; set; }

    /// <summary>"default", or "never" to stay until dismissed (a reminder toast on Windows).</summary>
    public string TimeoutType { get; set; } = "default";

    /// <summary>Buttons; <see cref="Action"/> reports which was pressed.</summary>
    public IList<NotificationAction> Actions { get; set; } = [];

    /// <summary>Windows only: toast XML to show instead of the generated one; Electron's <c>toastXml</c>.</summary>
    public string? ToastXml { get; set; }

    /// <summary>Whether the notification is on screen (shown and not yet closed or failed).</summary>
    public bool IsShown => _handle is not null;

    /// <summary>The notification was shown; Electron's <c>show</c>.</summary>
    public event EventHandler? Shown;

    /// <summary>The user clicked the notification's body; Electron's <c>click</c>.</summary>
    public event EventHandler? Click;

    /// <summary>The user pressed a button; the argument is its index in <see cref="Actions"/>. Electron's <c>action</c>.</summary>
    public event EventHandler<int>? Action;

    /// <summary>The notification went away; Electron's <c>close</c>.</summary>
    public event EventHandler<NotificationCloseReason>? Closed;

    /// <summary>The OS could not show it; the argument is the error. Electron's <c>failed</c>.</summary>
    public event EventHandler<string>? Failed;

    /// <summary>Whether desktop notifications work here; Electron's <c>Notification.isSupported()</c>.</summary>
    public static bool IsSupported() => Backend.IsSupported;

    /// <summary>Shows the notification, replacing it if it is already shown. Raises <see cref="Failed"/> rather than
    /// throwing when the OS refuses.</summary>
    public void Show()
    {
        if (_handle is not null) Forget();
        try
        {
            _handle = Backend.Show(AppId(), ToastXml ?? BuildToastXml(this), new Sink(this));
        }
        catch (Exception e) when (e is not PlatformNotSupportedException)
        {
            Failed?.Invoke(this, e.Message);
            return;
        }
        s_live.Add(this);
        Shown?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes the notification away; <see cref="Closed"/> follows with
    /// <see cref="NotificationCloseReason.ApplicationHidden"/>.</summary>
    public void Close()
    {
        if (_handle is not null) Backend.Hide(_handle);
    }

    private static string AppId() => OrielApp.CurrentOrNull?.AppUserModelId ?? OrielApp.DefaultAppUserModelId(null);

    private void Forget()
    {
        Backend.Release(_handle!);
        _handle = null;
        s_live.Remove(this);
    }

    /// <summary>A toast's activation arguments: "action=N" for button N, anything else for the body.</summary>
    internal void OnActivated(string arguments)
    {
        if (arguments.StartsWith("action=", StringComparison.Ordinal)
            && int.TryParse(arguments.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            Action?.Invoke(this, index);
        else
            Click?.Invoke(this, EventArgs.Empty);
    }

    internal void OnDismissed(NotificationCloseReason reason)
    {
        if (_handle is null) return;
        Forget();
        Closed?.Invoke(this, reason);
    }

    internal void OnFailed(string error)
    {
        if (_handle is null) return;
        Forget();
        Failed?.Invoke(this, error);
    }

    /// <summary>The toast XML Oriel generates: Electron's ToastGeneric layout.</summary>
    internal static string BuildToastXml(Notification n)
    {
        static string E(string s) => SecurityElement.Escape(s);
        var x = new StringBuilder("<toast launch=\"click\"");
        if (n.TimeoutType == "never") x.Append(" scenario=\"reminder\"");
        x.Append("><visual><binding template=\"ToastGeneric\">");
        x.Append("<text>").Append(E(n.Title)).Append("</text>");
        if (n.Body.Length > 0) x.Append("<text>").Append(E(n.Body)).Append("</text>");
        if (n.Icon is { Length: > 0 } icon)
            x.Append("<image placement=\"appLogoOverride\" src=\"").Append(E(new Uri(Path.GetFullPath(icon)).AbsoluteUri)).Append("\"/>");
        x.Append("</binding></visual>");
        if (n.Actions.Count > 0)
        {
            x.Append("<actions>");
            for (var i = 0; i < n.Actions.Count; i++)
                x.Append("<action content=\"").Append(E(n.Actions[i].Text)).Append("\" arguments=\"action=")
                    .Append(i.ToString(CultureInfo.InvariantCulture)).Append("\" activationType=\"foreground\"/>");
            x.Append("</actions>");
        }
        if (n.Silent) x.Append("<audio silent=\"true\"/>");
        return x.Append("</toast>").ToString();
    }

    private sealed class Sink(Notification owner) : INotificationSink
    {
        public void Activated(string arguments) => owner.OnActivated(arguments);
        public void Dismissed(NotificationCloseReason reason) => owner.OnDismissed(reason);
        public void Failed(string error) => owner.OnFailed(error);
    }
}

/// <summary>Where a backend reports what happened to a shown notification; called on the UI thread.</summary>
internal interface INotificationSink
{
    void Activated(string arguments);
    void Dismissed(NotificationCloseReason reason);
    void Failed(string error);
}

/// <summary>A platform's notification service behind <see cref="Notification"/>.</summary>
internal interface INotificationBackend
{
    bool IsSupported { get; }

    /// <summary>Shows <paramref name="xml"/> from app <paramref name="appId"/>; returns a handle, or throws if refused.</summary>
    object Show(string appId, string xml, INotificationSink sink);

    /// <summary>Takes a shown notification away; the sink then hears <see cref="NotificationCloseReason.ApplicationHidden"/>.</summary>
    void Hide(object handle);

    /// <summary>Lets go of a handle; the sink hears nothing more.</summary>
    void Release(object handle);
}

/// <summary>WinRT toast backend.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ToastNotificationBackend : INotificationBackend
{
    public bool IsSupported => WindowsToast.IsSupported();

    public object Show(string appId, string xml, INotificationSink sink)
    {
        var events = new Events(sink);
        events.Handle = WindowsToast.Show(appId, xml, events);
        return events;
    }

    public void Hide(object handle) => WindowsToast.Hide(((Events)handle).Handle);

    public void Release(object handle)
    {
        var events = (Events)handle;
        events.Released = true;
        WindowsToast.Release(events.Handle);
    }

    /// <summary>Moves WinRT's callbacks onto the UI thread, and drops them once released.</summary>
    private sealed class Events(INotificationSink sink) : IToastEvents
    {
        public nint Handle;
        public volatile bool Released;

        public void Activated(string arguments) => Post(() => sink.Activated(arguments));

        public void Dismissed(ToastDismissal reason) => Post(() => sink.Dismissed(reason switch
        {
            ToastDismissal.ApplicationHidden => NotificationCloseReason.ApplicationHidden,
            ToastDismissal.TimedOut => NotificationCloseReason.TimedOut,
            _ => NotificationCloseReason.UserCanceled,
        }));

        public void Failed(int errorCode) => Post(() => sink.Failed($"Toast failed (0x{errorCode:X8})."));

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

    public object Show(string appId, string xml, INotificationSink sink)
        => throw new PlatformNotSupportedException("Notification is not implemented on this platform.");

    public void Hide(object handle) { }

    public void Release(object handle) { }
}
