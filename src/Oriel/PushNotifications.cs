using System.Text.Json.Nodes;
using Oriel.Cef;
using Oriel.Cef.Platform;

// Each AppKit call runs inside a lambda behind an OperatingSystem.IsMacOS() check the analyzer cannot follow.
#pragma warning disable CA1416

namespace Oriel;

/// <summary>Apple Push Notification service registration and delivery (macOS). Electron's <c>pushNotifications</c>.</summary>
/// <remarks>APNs answers only an app signed with the <c>aps-environment</c> entitlement.</remarks>
public static class PushNotifications
{
    private static readonly Lock s_lock = new();
    // apns_promise_set_: every registration still waiting for APNs, answered together.
    private static List<TaskCompletionSource<string>> s_pending = [];

    /// <summary>A push notification arrived; the argument is its user info. Electron's
    /// <c>received-apns-notification</c>.</summary>
    public static event EventHandler<JsonObject>? ReceivedApnsNotification;

    // [NSApp registerForRemoteNotifications] on the main thread; replaceable for tests.
    internal static Action Register { get; set; } = () => OnMain(MacApp.RegisterForRemoteNotifications);

    /// <summary>Registers with APNs; the task completes with the device token in lowercase hex, or faults with
    /// <see cref="InvalidOperationException"/> carrying the error's code, domain and user info. Electron's
    /// <c>pushNotifications.registerForAPNSNotifications()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on macOS.</exception>
    public static Task<string> RegisterForApnsNotificationsAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("pushNotifications is macOS only.");
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Register();
        lock (s_lock) s_pending.Add(done);
        return done.Task;
    }

    /// <summary>Stops receiving push notifications. Electron's <c>pushNotifications.unregisterForAPNSNotifications()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not on macOS.</exception>
    public static void UnregisterForApnsNotifications()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("pushNotifications is macOS only.");
        OnMain(MacApp.UnregisterForRemoteNotifications);
    }

    /// <summary><c>base::ToLowerASCII(base::HexEncode(token))</c>.</summary>
    internal static string TokenString(byte[] token) => Convert.ToHexStringLower(token);

    /// <summary><c>ResolveAPNSPromiseSetWithToken</c>.</summary>
    internal static void Resolve(string token)
    {
        foreach (var done in TakePending()) done.SetResult(token);
    }

    /// <summary><c>RejectAPNSPromiseSetWithError</c>.</summary>
    internal static void Reject(string error)
    {
        foreach (var done in TakePending()) done.SetException(new InvalidOperationException(error));
    }

    internal static void OnReceived(JsonObject userInfo) => ReceivedApnsNotification?.Invoke(null, userInfo);

    // A registration made while these are answered waits for the next answer, as Electron moves the set out first.
    private static List<TaskCompletionSource<string>> TakePending()
    {
        lock (s_lock)
        {
            var pending = s_pending;
            s_pending = [];
            return pending;
        }
    }

    private static void OnMain(Action action)
    {
        if (OrielApp.CurrentOrNull is { IsReady: true }) CefThread.RunOnUi(action);
        else action();
    }
}
