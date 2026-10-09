using System.Globalization;
using System.Runtime.Versioning;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>What Electron's <c>LibnotifyNotification</c> asks libnotify for, and what libnotify then sends to
/// <c>org.freedesktop.Notifications</c>: pure, so the mapping is tested without a bus.</summary>
internal static class LinuxNotificationRules
{
    internal const string Service = "org.freedesktop.Notifications";
    internal const string ObjectPath = "/org/freedesktop/Notifications";
    internal const string Interface = "org.freedesktop.Notifications";
    internal const string NotifySignature = "susssasa{sv}i";

    /// <summary>IDS_NOTIFICATION_SHOW_BUTTON's en-US text, the label of the "default" action.</summary>
    internal const string ShowButton = "Show";

    /// <summary><c>NotifyUrgency</c> as the "urgency" byte hint: anything but "critical" or "low" is normal.</summary>
    internal static byte Urgency(string urgency) => urgency switch
    {
        "critical" => 2,
        "low" => 0,
        _ => 1,
    };

    /// <summary><c>NOTIFY_EXPIRES_NEVER</c> (0) for "never", else <c>NOTIFY_EXPIRES_DEFAULT</c> (-1).</summary>
    internal static int ExpireTimeout(string timeoutType) => timeoutType == "never" ? 0 : -1;

    /// <summary><c>NotifierSupportsActions()</c>: the server must list "actions", unless
    /// <c>ELECTRON_USE_UBUNTU_NOTIFIER</c> is set, in which case the capabilities are not even asked for.</summary>
    internal static bool SupportsActions(Func<string, string?> environment, Func<IReadOnlySet<string>> capabilities)
        => environment("ELECTRON_USE_UBUNTU_NOTIFIER") is null && capabilities().Contains("actions");

    /// <summary>The Notify actions array: only the "default" action Electron adds for <c>click</c>; the
    /// <see cref="Notification.Actions"/> and the reply box are macOS and Windows only.</summary>
    internal static string[] Actions(bool supportsActions) => supportsActions ? ["default", ShowButton] : [];

    /// <summary>libnotify's <c>activate_action</c>: an ActionInvoked key runs only if registered, and Electron registers
    /// "default" alone (case-sensitive), whose callback is the click.</summary>
    internal static bool IsClick(string action, bool hasDefaultAction) => hasDefaultAction && action == "default";

    /// <summary>libnotify's name for the image hint by the server's spec version.</summary>
    internal static string ImageHintName(int major, int minor)
        => major > 1 || (major == 1 && minor >= 2) ? "image-data"
            : major == 1 && minor >= 1 ? "image_data"
            : "icon_data";

    /// <summary><c>sscanf(spec, "%d.%d")</c> over the previous values: each number that does not parse keeps its old
    /// value.</summary>
    internal static (int Major, int Minor) ParseSpecVersion(string spec, int major, int minor)
    {
        var i = 0;
        if (!ScanInt(spec, ref i, out var newMajor)) return (major, minor);
        if (i >= spec.Length || spec[i] != '.') return (newMajor, minor);
        i++;
        return ScanInt(spec, ref i, out var newMinor) ? (newMajor, newMinor) : (newMajor, minor);
    }

    private static bool ScanInt(string s, ref int i, out int value)
    {
        value = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        var start = i;
        if (i < s.Length && s[i] is '+' or '-') i++;
        var digits = i;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        if (i == digits) return false;
        return int.TryParse(s.AsSpan(start, i - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary><c>notify_notification_set_image_from_pixbuf</c> of <c>GdkPixbufFromSkBitmap</c>: unpremultiplied RGBA
    /// with alpha, 8 bits a sample, rows of width × 4 bytes, as <c>(iiibiiay)</c>.</summary>
    internal static LinuxDBusVariant ImageData(ImageBitmap bitmap)
        => new("(iiibiiay)", new object?[] { bitmap.Width, bitmap.Height, bitmap.Width * 4, true, 8, 4, LinuxTrayRules.Rgba(bitmap) });

    /// <summary>Electron's <c>icon.AsBitmap()</c>: the representation closest to 1x, or null for an empty image, which
    /// draws nothing.</summary>
    internal static ImageBitmap? IconBitmap(NativeImage? image)
        => image is null || image.IsEmpty() ? null : image.BitmapAt(1.0);

    /// <summary><c>platform_util::GetXdgAppId</c>: the desktop name without its ".desktop" suffix.</summary>
    internal static string? XdgAppId(string? desktopName)
        => desktopName is null ? null : desktopName.EndsWith(".desktop", StringComparison.Ordinal) ? desktopName[..^".desktop".Length] : desktopName;

    /// <summary>The hints Electron sets, in the order it sets them: urgency, the image, the append hint the server
    /// takes, the desktop entry and the sender's pid.</summary>
    internal static List<(string Key, LinuxDBusVariant Value)> Hints(byte urgency, (string Name, LinuxDBusVariant Value)? image,
        IReadOnlySet<string> capabilities, string? appId, long pid)
    {
        var hints = new List<(string, LinuxDBusVariant)> { ("urgency", new("y", urgency)) };
        if (image is { } i) hints.Add((i.Name, i.Value));
        if (capabilities.Contains("append")) hints.Add(("append", new("s", "true")));
        else if (capabilities.Contains("x-canonical-append")) hints.Add(("x-canonical-append", new("s", "true")));
        if (appId is not null) hints.Add(("desktop-entry", new("s", appId)));
        hints.Add(("sender-pid", new("x", pid)));
        return hints;
    }

    /// <summary>libnotify's Notify call: a new notification each time (replaces_id 0), with no app icon.</summary>
    internal static object?[] NotifyArgs(string appName, string summary, string body, string[] actions,
        List<(string Key, LinuxDBusVariant Value)> hints, int timeout)
        => [appName, 0u, "", summary, body, actions, hints, timeout];

    /// <summary>The spec's NotificationClosed reason: 1 expired, 2 dismissed by the user, 3 closed by
    /// CloseNotification; 4 (undefined) and anything else count as the user's.</summary>
    internal static NotificationCloseReason CloseReason(uint reason) => reason switch
    {
        1 => NotificationCloseReason.TimedOut,
        3 => NotificationCloseReason.ApplicationHidden,
        _ => NotificationCloseReason.UserCanceled,
    };

    /// <summary>libnotify's <c>NOTIFY_CLOSED_REASON_UNSET</c> (-1) arriving as a uint: libnotify ignores it.</summary>
    internal const uint ReasonUnset = uint.MaxValue;
}

/// <summary>Electron's <c>LibnotifyNotification</c>, speaking libnotify's D-Bus protocol to
/// <c>org.freedesktop.Notifications</c> itself. The calls are synchronous on the UI thread, as libnotify makes them;
/// signals arrive on the UI thread.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxNotificationBackend : INotificationBackend
{
    // libnotify's GDBus calls default to 25 s; a little more lets libdbus's own timeout answer first.
    private static readonly TimeSpan s_callTimeout = TimeSpan.FromSeconds(30);

    private readonly List<Handle> _live = [];
    private readonly Func<string, string, object?[], object?[]?> _call;
    private readonly Func<string, string?> _environment;
    private readonly Func<string?> _desktopName;
    private readonly Action<string> _provideActivationToken;
    private readonly Func<string> _getAppName;
    private LinuxDBus? _bus;
    private string? _appName;
    // libnotify's cached proxy: GetServerInformation succeeded once, and gave the spec version.
    private bool _haveProxy;
    private int _specMajor, _specMinor;
    // Electron's GetServerCapabilities(): asked again only while the answer is empty.
    private HashSet<string> _capabilities = new(StringComparer.Ordinal);

    private sealed class Handle(INotificationSink sink, uint id, bool hasDefaultAction)
    {
        public readonly INotificationSink Sink = sink;
        public readonly bool HasDefaultAction = hasDefaultAction;
        public readonly uint Id = id;
        public string? ActivationToken;
    }

    public LinuxNotificationBackend()
    {
        _call = BusCall;
        _environment = Environment.GetEnvironmentVariable;
        _desktopName = LinuxLauncherEntry.DesktopName;
        _provideActivationToken = token => LinuxCefPatches.ProvideActivationToken(token);
        _getAppName = AppName;
    }

    /// <summary>A backend whose synchronous calls to the service go to <paramref name="call"/> (method, signature,
    /// arguments; null for an error or no answer), for tests.</summary>
    internal LinuxNotificationBackend(Func<string, string, object?[], object?[]?> call, Func<string, string?> environment,
        Func<string?> desktopName, Action<string> provideActivationToken, Func<string> appName)
        => (_call, _environment, _desktopName, _provideActivationToken, _getAppName) = (call, environment, desktopName, provideActivationToken, appName);

    public bool IsSupported => true;

    public bool PostsShowEvents => false;

    public bool KeepsDismissed => false;

    /// <summary><c>notify_init(GetApplicationName())</c>: the app name is fixed by the first notification.</summary>
    public void Prepare() => _appName ??= _getAppName();

    public void Post(Action action) => CefThread.PostUi(action);

    public object Show(Notification notification, INotificationSink sink)
    {
        Prepare();
        var supportsActions = LinuxNotificationRules.SupportsActions(_environment, Capabilities);
        var actions = LinuxNotificationRules.Actions(supportsActions);
        var urgency = LinuxNotificationRules.Urgency(notification.Urgency);
        var icon = notification.IconImage ?? (notification.Icon is { Length: > 0 } path ? NativeImage.CreateFromPath(path) : null);
        (string, LinuxDBusVariant)? image = LinuxNotificationRules.IconBitmap(icon) is { } bitmap
            ? (LinuxNotificationRules.ImageHintName(_specMajor, _specMinor), LinuxNotificationRules.ImageData(bitmap))
            : null;
        var timeout = LinuxNotificationRules.ExpireTimeout(notification.TimeoutType);
        var hints = LinuxNotificationRules.Hints(urgency, image, Capabilities(), LinuxNotificationRules.XdgAppId(_desktopName()), Environment.ProcessId);

        // notify_notification_show fails with a GError; Electron's NotificationFailed() reports no text.
        if (!EnsureProxy()) throw new InvalidOperationException("");
        var reply = _call("Notify", LinuxNotificationRules.NotifySignature,
            LinuxNotificationRules.NotifyArgs(_appName!, notification.Title, notification.Body, actions, hints, timeout));
        if (reply is not [uint id]) throw new InvalidOperationException("");
        var handle = new Handle(sink, id, supportsActions);
        _live.Add(handle);
        return handle;
    }

    /// <summary><c>notify_notification_close</c>: CloseNotification, waited for, its error ignored as Electron logs it.</summary>
    public void Hide(object handle)
    {
        if (!EnsureProxy()) return;
        _ = _call("CloseNotification", "u", [((Handle)handle).Id]);
    }

    public void Remove(object handle) { }

    public void Release(object handle) => _live.Remove((Handle)handle);

    private static string AppName()
        => OrielApp.CurrentOrNull?.Name is { Length: > 0 } name ? name
            : System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Oriel";

    private IReadOnlySet<string> Capabilities()
    {
        if (_capabilities.Count == 0 && EnsureProxy() && _call("GetCapabilities", "", []) is [List<object?> caps])
            _capabilities = new HashSet<string>(caps.OfType<string>(), StringComparer.Ordinal);
        return _capabilities;
    }

    // _notify_get_proxy: no proxy until GetServerInformation answers, and then the spec version is known.
    private bool EnsureProxy()
    {
        if (_haveProxy) return true;
        if (_call("GetServerInformation", "", []) is not [string, string, string, string spec]) return false;
        (_specMajor, _specMinor) = LinuxNotificationRules.ParseSpecVersion(spec, _specMajor, _specMinor);
        _haveProxy = true;
        return true;
    }

    private object?[]? BusCall(string method, string signature, object?[] args)
    {
        var bus = Bus();
        object?[]? reply = null;
        using var done = new ManualResetEventSlim();
        bus.Post(() => bus.CallAsync(LinuxNotificationRules.Service, LinuxNotificationRules.ObjectPath, LinuxNotificationRules.Interface,
            method, signature, args, (values, _) =>
            {
                reply = values;
                done.Set();
            }));
        return done.Wait(s_callTimeout) ? reply : null;
    }

    private LinuxDBus Bus()
    {
        if (_bus is not null) return _bus;
        var bus = LinuxDBus.Open(false, "Oriel D-Bus notifications");
        bus.Signal += signal =>
        {
            if (signal.Path == LinuxNotificationRules.ObjectPath && signal.Interface == LinuxNotificationRules.Interface)
                CefThread.PostUi(() => OnSignal(signal.Member, signal.Args));
        };
        // GDBusProxy's subscription: the service's signals on its object and interface, from the name's owner.
        bus.Post(() => bus.AddMatch($"type='signal',sender='{LinuxNotificationRules.Service}',path='{LinuxNotificationRules.ObjectPath}',interface='{LinuxNotificationRules.Interface}'"));
        return _bus = bus;
    }

    /// <summary>libnotify's <c>proxy_g_signal_cb</c> for each live notification with the signal's id. UI thread.</summary>
    internal void OnSignal(string member, object?[] args)
    {
        foreach (var handle in _live.ToList())
        {
            switch (member, args)
            {
                case ("NotificationClosed", [uint id, uint reason]) when id == handle.Id:
                    // The notification is released with its close event, so libnotify's once-only guard is not needed.
                    if (reason == LinuxNotificationRules.ReasonUnset) break;
                    handle.Sink.Dismissed(LinuxNotificationRules.CloseReason(reason));
                    break;
                case ("ActionInvoked", [uint id, string action]) when id == handle.Id:
                    // Only registered actions run; Electron registers "default" alone, as the click.
                    if (!LinuxNotificationRules.IsClick(action, handle.HasDefaultAction)) break;
                    if (handle.ActivationToken is { Length: > 0 } pending) _provideActivationToken(pending);
                    handle.Sink.Activated("", []);
                    break;
                case ("ActivationToken", [uint id, string token]) when id == handle.Id:
                    handle.ActivationToken = token;
                    break;
            }
        }
    }
}
