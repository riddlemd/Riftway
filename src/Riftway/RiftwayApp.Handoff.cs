using System.ComponentModel;
using System.Text.Json.Nodes;
using Riftway.Cef.Platform;

// Each platform call runs behind an OperatingSystem check the analyzer cannot follow into lambdas.
#pragma warning disable CA1416

namespace Riftway;

/// <summary>A Handoff activity of <see cref="Type"/> is about to be resumed from another device (macOS); Electron's
/// <c>will-continue-activity</c> arguments. Set <see cref="CancelEventArgs.Cancel"/> to say the app handles it
/// (Electron's <c>preventDefault()</c>).</summary>
public sealed class WillContinueActivityEventArgs(string type) : CancelEventArgs
{
    /// <summary>The activity's type (<c>NSUserActivity.activityType</c>).</summary>
    public string Type { get; } = type;
}

/// <summary>A Handoff activity resumed from another device (macOS); Electron's <c>continue-activity</c> arguments. Set
/// <see cref="CancelEventArgs.Cancel"/> to say the app handled it (Electron's <c>preventDefault()</c>).</summary>
public sealed class ContinueActivityEventArgs(string type, JsonObject userInfo, ContinueActivityDetails details) : CancelEventArgs
{
    /// <summary>The activity's type.</summary>
    public string Type { get; } = type;

    /// <summary>The state the other device's activity stored.</summary>
    public JsonObject UserInfo { get; } = userInfo;

    /// <summary>Electron's <c>details</c>.</summary>
    public ContinueActivityDetails Details { get; } = details;
}

/// <summary>Electron's <c>continue-activity</c> <c>details</c>.</summary>
/// <param name="WebpageUrl">The webpage the other device's activity was showing, or null. Electron's <c>webpageURL</c>.</param>
public sealed record ContinueActivityDetails(string? WebpageUrl);

/// <summary>A Handoff activity could not be resumed (macOS); Electron's <c>continue-activity-error</c> arguments.</summary>
/// <param name="Type">The activity's type.</param>
/// <param name="Error">The error's localized description.</param>
public sealed record ContinueActivityErrorEventArgs(string Type, string Error);

/// <summary>This app's activity was resumed on another device (macOS); Electron's <c>activity-was-continued</c> arguments.</summary>
/// <param name="Type">The activity's type.</param>
/// <param name="UserInfo">The activity's stored state.</param>
public sealed record ActivityWasContinuedEventArgs(string Type, JsonObject UserInfo);

/// <summary>Handoff is about to save this app's activity (macOS); Electron's <c>update-activity-state</c> arguments.
/// Set <see cref="CancelEventArgs.Cancel"/> to make the save wait until <see cref="RiftwayApp.UpdateCurrentActivity"/>
/// (Electron's <c>preventDefault()</c>); it waits a second at most.</summary>
public sealed class UpdateActivityStateEventArgs(string type, JsonObject userInfo) : CancelEventArgs
{
    /// <summary>The activity's type.</summary>
    public string Type { get; } = type;

    /// <summary>The activity's stored state.</summary>
    public JsonObject UserInfo { get; } = userInfo;
}

public sealed partial class RiftwayApp
{
    /// <summary>macOS: Handoff is about to resume an activity from another device. Electron's
    /// <c>will-continue-activity</c>.</summary>
    public event EventHandler<WillContinueActivityEventArgs>? WillContinueActivity;

    /// <summary>macOS: Handoff resumed an activity from another device; the type must be listed in the bundle's
    /// <c>NSUserActivityTypes</c>. Electron's <c>continue-activity</c>.</summary>
    public event EventHandler<ContinueActivityEventArgs>? ContinueActivity;

    /// <summary>macOS: Handoff failed to resume an activity. Electron's <c>continue-activity-error</c>.</summary>
    public event EventHandler<ContinueActivityErrorEventArgs>? ContinueActivityError;

    /// <summary>macOS: this app's activity was resumed on another device. Electron's <c>activity-was-continued</c>.</summary>
    public event EventHandler<ActivityWasContinuedEventArgs>? ActivityWasContinued;

    /// <summary>macOS: Handoff is about to save this app's activity; raised on a background thread's request, on the UI
    /// thread. Electron's <c>update-activity-state</c>.</summary>
    public event EventHandler<UpdateActivityStateEventArgs>? UpdateActivityState;

    /// <summary>Makes a new Handoff activity current (macOS); does nothing elsewhere. Electron's
    /// <c>app.setUserActivity()</c>.</summary>
    /// <param name="type">The activity's type (<c>NSUserActivity.activityType</c>).</param>
    /// <param name="userInfo">State another device needs to resume it; numbers that are not 32-bit integers are stored
    /// as doubles, as from JavaScript.</param>
    /// <param name="webpageUrl">A webpage to show on a device without the app; any string GURL rejects means none.</param>
    /// <exception cref="ArgumentException"><paramref name="webpageUrl"/> is a valid URL of a scheme other than http or
    /// https, which <c>NSUserActivity</c> refuses.</exception>
    public void SetUserActivity(string type, JsonObject userInfo, string? webpageUrl = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(userInfo);
        var url = UserActivityUrl(webpageUrl);
        if (OperatingSystem.IsMacOS()) OnMacMain(() => MacUserActivity.SetCurrent(type, userInfo, url));
    }

    /// <summary>The current Handoff activity's type (macOS); empty without one, and elsewhere. Electron's
    /// <c>app.getCurrentActivityType()</c>.</summary>
    public string GetCurrentActivityType() => OperatingSystem.IsMacOS() ? OnMacMain(MacUserActivity.CurrentType) : "";

    /// <summary>Invalidates and drops the current Handoff activity (macOS). Electron's
    /// <c>app.invalidateCurrentActivity()</c>.</summary>
    public void InvalidateCurrentActivity()
    {
        if (OperatingSystem.IsMacOS()) OnMacMain(MacUserActivity.InvalidateCurrent);
    }

    /// <summary>Stops offering the current Handoff activity without invalidating it (macOS). Electron's
    /// <c>app.resignCurrentActivity()</c>.</summary>
    public void ResignCurrentActivity()
    {
        if (OperatingSystem.IsMacOS()) OnMacMain(MacUserActivity.ResignCurrent);
    }

    /// <summary>Merges <paramref name="userInfo"/> into the current activity's state, if its type is any, and lets a
    /// save held by <see cref="UpdateActivityState"/> go on (macOS). Electron's <c>app.updateCurrentActivity()</c>.</summary>
    /// <param name="type">Ignored, as in Electron.</param>
    /// <param name="userInfo">The entries to add or replace.</param>
    public void UpdateCurrentActivity(string type, JsonObject userInfo)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(userInfo);
        if (OperatingSystem.IsMacOS()) OnMacMain(() => MacUserActivity.UpdateCurrent(userInfo));
    }

    /// <summary><c>net::NSURLWithGURL(GURL(url))</c>: the canonical URL, or null for an invalid one.</summary>
    internal static string? UserActivityUrl(string? webpageUrl)
    {
        if (webpageUrl is null || Gurl.Spec(webpageUrl) is not { Length: > 0 } spec) return null;
        // NSUserActivity raises for any other scheme, which would abort the process, as it does Electron's.
        if (!spec.StartsWith("http://", StringComparison.Ordinal) && !spec.StartsWith("https://", StringComparison.Ordinal))
            throw new ArgumentException("A user activity's webpage URL must be http or https.", nameof(webpageUrl));
        return spec;
    }

    internal bool RaiseWillContinueActivity(string type)
    {
        var args = new WillContinueActivityEventArgs(type);
        WillContinueActivity?.Invoke(this, args);
        return args.Cancel;
    }

    internal bool RaiseContinueActivity(string type, JsonObject userInfo, string? webpageUrl)
    {
        var args = new ContinueActivityEventArgs(type, userInfo, new ContinueActivityDetails(webpageUrl));
        ContinueActivity?.Invoke(this, args);
        return args.Cancel;
    }

    internal void RaiseContinueActivityError(string type, string error) => ContinueActivityError?.Invoke(this, new ContinueActivityErrorEventArgs(type, error));

    internal void RaiseActivityWasContinued(string type, JsonObject userInfo) => ActivityWasContinued?.Invoke(this, new ActivityWasContinuedEventArgs(type, userInfo));

    internal bool RaiseUpdateActivityState(string type, JsonObject userInfo)
    {
        var args = new UpdateActivityStateEventArgs(type, userInfo);
        UpdateActivityState?.Invoke(this, args);
        return args.Cancel;
    }
}
