using System.Collections;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Riftway.Cef;
using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>A value type for <see cref="SystemPreferences.GetUserDefault"/> and <see cref="SystemPreferences.SetUserDefault"/>;
/// Electron's <c>type</c> strings.</summary>
public enum UserDefaultType
{
    /// <summary><c>string</c>: a <see cref="string"/>.</summary>
    String,

    /// <summary><c>boolean</c>: a <see cref="bool"/>.</summary>
    Boolean,

    /// <summary><c>integer</c>: an <see cref="int"/>.</summary>
    Integer,

    /// <summary><c>float</c>: a <see cref="double"/> holding a single-precision value.</summary>
    Float,

    /// <summary><c>double</c>: a <see cref="double"/>.</summary>
    Double,

    /// <summary><c>url</c>: the URL as a <see cref="string"/>.</summary>
    Url,

    /// <summary><c>array</c>: a <see cref="JsonArray"/>.</summary>
    Array,

    /// <summary><c>dictionary</c>: a <see cref="JsonObject"/>.</summary>
    Dictionary,
}

/// <summary>A notification delivered to a <see cref="SystemPreferences.SubscribeNotification"/> callback; Electron's
/// <c>(event, userInfo, object)</c> arguments.</summary>
/// <param name="Event">The notification's name.</param>
/// <param name="UserInfo">Its user info dictionary; empty when it has none.</param>
/// <param name="Object">The sender when it is a string, else <c>""</c>.</param>
public sealed record SubscribedNotification(string Event, JsonObject UserInfo, string Object);

// Electron's electron_api_system_preferences_mac.mm.
public static partial class SystemPreferences
{
    private static readonly Dictionary<int, (MacNotificationCenter Center, nint Observer)> s_subscriptions = [];
    private static int s_nextSubscription;

    /// <summary>A standard system colour as <c>#RRGGBBAA</c> hex, adapted to vibrancy and accessibility settings.
    /// Electron's <c>systemPreferences.getSystemColor()</c>.</summary>
    /// <param name="color"><c>blue</c>, <c>brown</c>, <c>gray</c>, <c>green</c>, <c>orange</c>, <c>pink</c>, <c>purple</c>,
    /// <c>red</c> or <c>yellow</c>.</param>
    /// <exception cref="ArgumentException">Another name (<c>Unknown system color: …</c>).</exception>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static string GetSystemColor(string color)
    {
        if (!OperatingSystem.IsMacOS()) throw Unsupported("getSystemColor");
        if (MacSystemColorSelector(color) is not { } selector) throw new ArgumentException("Unknown system color: " + color, nameof(color));
        return MacNamedColor(selector) is { } argb ? ToRgbaHex(argb, includeHash: true) : "";
    }

    [SupportedOSPlatform("macos")]
    private static uint? MacNamedColor(string selector) => OnUi(() => MacSystemPreferences.NamedColor(selector));

    /// <summary>Asks the user for access to the camera or microphone, once: later calls resolve with the existing
    /// answer. Electron's <c>systemPreferences.askForMediaAccess()</c>.</summary>
    /// <remarks>The app's <c>Info.plist</c> needs <c>NSCameraUsageDescription</c> and <c>NSMicrophoneUsageDescription</c>.</remarks>
    /// <returns>Whether access was granted. Faults with <see cref="ArgumentOutOfRangeException"/> ("Invalid media type") for
    /// <see cref="MediaAccessType.Screen"/> or an undefined value, as Electron's promise rejects.</returns>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static Task<bool> AskForMediaAccessAsync(MediaAccessType mediaType)
    {
        if (!OperatingSystem.IsMacOS()) throw Unsupported("askForMediaAccess");
        if (mediaType is not (MediaAccessType.Camera or MediaAccessType.Microphone))
            return Task.FromException<bool>(new ArgumentOutOfRangeException(nameof(mediaType), "Invalid media type"));
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MacSystemPreferences.RequestMediaAccess(mediaType == MediaAccessType.Camera, granted => result.TrySetResult(granted));
        return result.Task;
    }

    /// <summary>The appearance AppKit currently applies to the app (<c>NSApplication.effectiveAppearance</c>). Electron's
    /// <c>systemPreferences.getEffectiveAppearance()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static SystemAppearance GetEffectiveAppearance()
    {
        if (!OperatingSystem.IsMacOS()) throw Unsupported("getEffectiveAppearance");
        return ToSystemAppearance(OnUi(MacSystemPreferences.EffectiveAppearanceName));
    }

    /// <summary><see cref="GetEffectiveAppearance"/> as a property. Electron's <c>systemPreferences.effectiveAppearance</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static SystemAppearance EffectiveAppearance => GetEffectiveAppearance();

    /// <summary>Whether macOS asks apps to avoid translucent backgrounds. Electron's deprecated
    /// <c>systemPreferences.accessibilityDisplayShouldReduceTransparency</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    [Obsolete("Use NativeTheme.PrefersReducedTransparency, as Electron deprecates systemPreferences.accessibilityDisplayShouldReduceTransparency.")]
    public static bool AccessibilityDisplayShouldReduceTransparency
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) throw Unsupported("accessibilityDisplayShouldReduceTransparency");
            return MacReduceTransparency();
        }
    }

    [SupportedOSPlatform("macos")]
    private static bool MacReduceTransparency() => OnUi(() => MacAppearance.ReduceTransparency);

    /// <summary>Whether this process is a trusted accessibility client. Electron's
    /// <c>systemPreferences.isTrustedAccessibilityClient()</c>.</summary>
    /// <param name="prompt">Whether macOS asks the user to trust the process when it is not trusted yet.</param>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static bool IsTrustedAccessibilityClient(bool prompt)
    {
        if (!OperatingSystem.IsMacOS()) throw Unsupported("isTrustedAccessibilityClient");
        return MacSystemPreferences.IsTrustedAccessibilityClient(prompt);
    }

    /// <summary>Whether this device can authenticate with Touch ID. Electron's <c>systemPreferences.canPromptTouchID()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static bool CanPromptTouchID()
    {
        if (!OperatingSystem.IsMacOS()) throw Unsupported("canPromptTouchID");
        return MacSystemPreferences.CanPromptTouchID();
    }

    /// <summary>Asks the user to authenticate with Touch ID. Electron's <c>systemPreferences.promptTouchID()</c>.</summary>
    /// <param name="reason">Why, shown in the prompt.</param>
    /// <returns>Completes when the user authenticated. Faults with <see cref="ArgumentException"/> for an empty reason, and
    /// with <see cref="InvalidOperationException"/> carrying LocalAuthentication's message when authentication fails.</returns>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static Task PromptTouchIDAsync(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (!OperatingSystem.IsMacOS()) throw Unsupported("promptTouchID");
        if (reason.Length == 0) return Task.FromException(new ArgumentException("reason must be non-empty", nameof(reason)));
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MacSystemPreferences.PromptTouchID(reason, (success, message) =>
        {
            if (success) result.TrySetResult();
            else result.TrySetException(new InvalidOperationException(message));
        });
        return result.Task;
    }

    /// <summary>Whether the "Swipe between pages" setting is on. Electron's
    /// <c>systemPreferences.isSwipeTrackingFromScrollEventsEnabled()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static bool IsSwipeTrackingFromScrollEventsEnabled()
    {
        if (!OperatingSystem.IsMacOS()) throw Unsupported("isSwipeTrackingFromScrollEventsEnabled");
        return OnUi(MacSystemPreferences.IsSwipeTrackingFromScrollEventsEnabled);
    }

    /// <summary>The value of <paramref name="key"/> in the app's <c>NSUserDefaults</c>, read as <paramref name="type"/>:
    /// a <see cref="string"/> (<c>""</c> when missing), <see cref="bool"/>, <see cref="int"/>, <see cref="double"/>, the
    /// URL as a <see cref="string"/>, a <see cref="JsonArray"/> or a <see cref="JsonObject"/> (empty when missing); null
    /// for an undefined type. Electron's <c>systemPreferences.getUserDefault()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static object? GetUserDefault(string key, UserDefaultType type)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!OperatingSystem.IsMacOS()) throw Unsupported("getUserDefault");
        return type switch
        {
            UserDefaultType.String => MacSystemPreferences.GetString(key),
            UserDefaultType.Boolean => MacSystemPreferences.GetBool(key),
            // v8::Integer::New takes an int32: wider values wrap.
            UserDefaultType.Integer => unchecked((int)MacSystemPreferences.GetInteger(key)),
            UserDefaultType.Float => (double)MacSystemPreferences.GetFloat(key),
            UserDefaultType.Double => MacSystemPreferences.GetDouble(key),
            UserDefaultType.Url => MacSystemPreferences.GetUrl(key) is { } url ? Gurl.Spec(url) : "",
            UserDefaultType.Array => MacSystemPreferences.GetArray(key),
            UserDefaultType.Dictionary => MacSystemPreferences.GetDictionary(key),
            _ => null,
        };
    }

    /// <summary>Sets <paramref name="key"/> in the app's <c>NSUserDefaults</c>. Electron's
    /// <c>systemPreferences.setUserDefault()</c>.</summary>
    /// <param name="key">The key.</param>
    /// <param name="type">How to store <paramref name="value"/>.</param>
    /// <param name="value">A <see cref="string"/> for <see cref="UserDefaultType.String"/> and <see cref="UserDefaultType.Url"/>
    /// (a valid URL), a <see cref="bool"/>, a number (an integral one within <see cref="int"/> for
    /// <see cref="UserDefaultType.Integer"/>), or a list or dictionary of JSON-like values (<see cref="JsonNode"/>s, strings,
    /// numbers, bools, nulls, lists and string-keyed dictionaries).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not defined (<c>Invalid type: …</c>).</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> does not fit <paramref name="type"/>
    /// (<c>Unable to convert value to: …</c>).</exception>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static void SetUserDefault(string key, UserDefaultType type, object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!OperatingSystem.IsMacOS()) throw Unsupported("setUserDefault");
        var stored = CoerceUserDefault(type, value) switch
        {
            string s when type == UserDefaultType.String => Do(() => MacSystemPreferences.SetString(key, s)),
            bool b => Do(() => MacSystemPreferences.SetBool(key, b)),
            float f => Do(() => MacSystemPreferences.SetFloat(key, f)),
            int i => Do(() => MacSystemPreferences.SetInteger(key, i)),
            double d => Do(() => MacSystemPreferences.SetDouble(key, d)),
            string spec when type == UserDefaultType.Url => MacSystemPreferences.SetUrl(key, spec),
            string json => MacSystemPreferences.SetJson(key, json, array: type == UserDefaultType.Array),
            _ => false,
        };
        if (!stored) throw UnableToConvert(type);

        static bool Do(Action set)
        {
            set();
            return true;
        }
    }

    /// <summary>Removes <paramref name="key"/> from the app's <c>NSUserDefaults</c>, restoring its registered or global
    /// value. Electron's <c>systemPreferences.removeUserDefault()</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static void RemoveUserDefault(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!OperatingSystem.IsMacOS()) throw Unsupported("removeUserDefault");
        MacSystemPreferences.Remove(key);
    }

    /// <summary>Adds <paramref name="defaults"/> to the app's registered <c>NSUserDefaults</c>. Electron's
    /// <c>systemPreferences.registerDefaults()</c>.</summary>
    /// <exception cref="ArgumentException">A value is null or cannot be converted; nothing is registered.</exception>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static void RegisterDefaults(IReadOnlyDictionary<string, object?> defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (!OperatingSystem.IsMacOS()) throw Unsupported("registerDefaults");
        if (ToElectronJson(ToValueNode(defaults)) is not { } json || !MacSystemPreferences.RegisterDefaults(json))
            throw new ArgumentException("Error processing argument at index 0, conversion failure", nameof(defaults));
    }

    /// <summary>Subscribes to <c>NSDistributedNotificationCenter</c>, e.g. <c>AppleInterfaceThemeChangedNotification</c>.
    /// Electron's <c>systemPreferences.subscribeNotification()</c>.</summary>
    /// <param name="event">The notification name, or null for any.</param>
    /// <param name="callback">Called with each notification, on the thread it is delivered on (the UI thread for
    /// distributed notifications).</param>
    /// <returns>The subscription's id, for <see cref="UnsubscribeNotification"/>.</returns>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static int SubscribeNotification(string? @event, Action<SubscribedNotification> callback)
        => Subscribe(MacNotificationCenter.Distributed, @event, callback, "subscribeNotification");

    /// <summary>Subscribes to <c>NSNotificationCenter.defaultCenter</c>, e.g. <c>NSUserDefaultsDidChangeNotification</c>.
    /// Electron's <c>systemPreferences.subscribeLocalNotification()</c>.</summary>
    /// <inheritdoc cref="SubscribeNotification" path="/param|/returns|/exception"/>
    public static int SubscribeLocalNotification(string? @event, Action<SubscribedNotification> callback)
        => Subscribe(MacNotificationCenter.Local, @event, callback, "subscribeLocalNotification");

    /// <summary>Subscribes to <c>NSWorkspace.sharedWorkspace.notificationCenter</c>, e.g.
    /// <c>NSWorkspaceDidActivateApplicationNotification</c>. Electron's
    /// <c>systemPreferences.subscribeWorkspaceNotification()</c>.</summary>
    /// <inheritdoc cref="SubscribeNotification" path="/param|/returns|/exception"/>
    public static int SubscribeWorkspaceNotification(string? @event, Action<SubscribedNotification> callback)
        => Subscribe(MacNotificationCenter.Workspace, @event, callback, "subscribeWorkspaceNotification");

    /// <summary>Removes the <see cref="SubscribeNotification"/> subscription <paramref name="id"/>. Electron's
    /// <c>systemPreferences.unsubscribeNotification()</c>.</summary>
    /// <remarks>As in Electron, ids are shared by the three centres, and an id from another centre is forgotten without
    /// stopping its callback.</remarks>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static void UnsubscribeNotification(int id) => Unsubscribe(MacNotificationCenter.Distributed, id, "unsubscribeNotification");

    /// <summary>Removes the <see cref="SubscribeLocalNotification"/> subscription <paramref name="id"/>. Electron's
    /// <c>systemPreferences.unsubscribeLocalNotification()</c>.</summary>
    /// <inheritdoc cref="UnsubscribeNotification" path="/remarks|/exception"/>
    public static void UnsubscribeLocalNotification(int id) => Unsubscribe(MacNotificationCenter.Local, id, "unsubscribeLocalNotification");

    /// <summary>Removes the <see cref="SubscribeWorkspaceNotification"/> subscription <paramref name="id"/>. Electron's
    /// <c>systemPreferences.unsubscribeWorkspaceNotification()</c>.</summary>
    /// <inheritdoc cref="UnsubscribeNotification" path="/remarks|/exception"/>
    public static void UnsubscribeWorkspaceNotification(int id) => Unsubscribe(MacNotificationCenter.Workspace, id, "unsubscribeWorkspaceNotification");

    /// <summary>Posts <paramref name="event"/> on <c>NSDistributedNotificationCenter</c>. Electron's
    /// <c>systemPreferences.postNotification()</c>.</summary>
    /// <param name="event">The notification name.</param>
    /// <param name="userInfo">The user info dictionary, of JSON-like values.</param>
    /// <param name="deliverImmediately">Deliver even to suspended observers in inactive apps.</param>
    /// <exception cref="ArgumentException"><paramref name="userInfo"/> holds a value that cannot be converted.</exception>
    /// <exception cref="PlatformNotSupportedException">Not macOS.</exception>
    public static void PostNotification(string @event, IReadOnlyDictionary<string, object?> userInfo, bool deliverImmediately = false)
        => Post(MacNotificationCenter.Distributed, @event, userInfo, deliverImmediately, "postNotification");

    /// <summary>Posts <paramref name="event"/> on <c>NSNotificationCenter.defaultCenter</c>; observers in this process run
    /// before it returns. Electron's <c>systemPreferences.postLocalNotification()</c>.</summary>
    /// <inheritdoc cref="PostNotification" path="/param[@name='event']|/param[@name='userInfo']|/exception"/>
    public static void PostLocalNotification(string @event, IReadOnlyDictionary<string, object?> userInfo)
        => Post(MacNotificationCenter.Local, @event, userInfo, false, "postLocalNotification");

    /// <summary>Posts <paramref name="event"/> on <c>NSWorkspace.sharedWorkspace.notificationCenter</c>. Electron's
    /// <c>systemPreferences.postWorkspaceNotification()</c>.</summary>
    /// <inheritdoc cref="PostNotification" path="/param[@name='event']|/param[@name='userInfo']|/exception"/>
    public static void PostWorkspaceNotification(string @event, IReadOnlyDictionary<string, object?> userInfo)
        => Post(MacNotificationCenter.Workspace, @event, userInfo, false, "postWorkspaceNotification");

    private static int Subscribe(MacNotificationCenter center, string? name, Action<SubscribedNotification> callback, string method)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!OperatingSystem.IsMacOS()) throw Unsupported(method);
        int id;
        lock (s_gate) id = s_nextSubscription++;
        var observer = MacSystemPreferences.AddObserver(center, name, (n, info, sender) => callback(new SubscribedNotification(n, info, sender)));
        lock (s_gate) s_subscriptions[id] = (center, observer);
        return id;
    }

    // DoUnsubscribeNotification erases the id whichever centre it names, and removes the observer from that centre only.
    private static void Unsubscribe(MacNotificationCenter center, int id, string method)
    {
        if (!OperatingSystem.IsMacOS()) throw Unsupported(method);
        (MacNotificationCenter Center, nint Observer) entry;
        lock (s_gate)
            if (!s_subscriptions.Remove(id, out entry)) return;
        MacSystemPreferences.RemoveObserver(center, entry.Observer);
        if (entry.Center == center) MacSystemPreferences.ReleaseObserver(entry.Observer);
    }

    private static void Post(MacNotificationCenter center, string name, IReadOnlyDictionary<string, object?> userInfo, bool immediately, string method)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(userInfo);
        if (!OperatingSystem.IsMacOS()) throw Unsupported(method);
        MacPost(center, name, ToElectronJson(ToValueNode(userInfo)), immediately);
    }

    [SupportedOSPlatform("macos")]
    private static void MacPost(MacNotificationCenter center, string name, string? json, bool immediately) => OnUi(() =>
    {
        MacSystemPreferences.Post(center, name, json, immediately);
        return true;
    });

    [SupportedOSPlatform("macos")]
    private static string MacAccentColor()
        => OnUi(() => MacSystemPreferences.NamedColor("controlAccentColor")) is { } argb ? ToRgbaHex(argb, includeHash: false) : "";

    [SupportedOSPlatform("macos")]
    private static string MacColor(string color)
    {
        if (MacColorSelector(color) is not { } selector) throw new ArgumentException("Unknown color: " + color, nameof(color));
        return OnUi(() => MacSystemPreferences.NamedColor(selector)) is { } argb ? ToRgbaHex(argb, includeHash: true) : "";
    }

    [SupportedOSPlatform("macos")]
    private static MediaAccessStatus MacMediaAccessStatus(MediaAccessType mediaType) => mediaType switch
    {
        MediaAccessType.Camera or MediaAccessType.Microphone when HasSwitch(CurrentSwitches(), "use-fake-device-for-media-stream", windows: false)
            => MediaAccessStatus.Granted,
        MediaAccessType.Camera => ToMacMediaAccessStatus(MacSystemPreferences.MediaAuthorizationStatus(video: true)),
        MediaAccessType.Microphone => ToMacMediaAccessStatus(MacSystemPreferences.MediaAuthorizationStatus(video: false)),
        MediaAccessType.Screen => MacSystemPreferences.ScreenCaptureAllowed() ? MediaAccessStatus.Granted : MediaAccessStatus.Denied,
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), "Invalid media type"),
    };

    [SupportedOSPlatform("macos")]
    private static AnimationSettings MacAnimationSettings()
        => MacAnimationSettings(CurrentSwitches(), () => OnUi(() => MacSystemPreferences.ReduceMotion),
            MacSystemPreferences.DefaultsFlag("NSScrollAnimationEnabled"));

    /// <summary>gfx::Animation on macOS: rich animation is the inverse of <c>PrefersReducedMotion</c> (switches, then "Reduce
    /// motion"); scroll animation is the <c>NSScrollAnimationEnabled</c> default, off when unset.</summary>
    internal static AnimationSettings MacAnimationSettings(IReadOnlyList<string> args, Func<bool> reduceMotion, bool? scrollAnimationDefault)
    {
        var reduced = ForcedReducedMotion(args, windows: false) ?? reduceMotion();
        return new AnimationSettings(!reduced, scrollAnimationDefault ?? false, reduced);
    }

    /// <summary>Chromium's <c>CheckSystemMediaCapturePermission</c> then Electron's <c>ConvertSystemPermission</c>, from an
    /// <c>AVAuthorizationStatus</c>.</summary>
    internal static MediaAccessStatus ToMacMediaAccessStatus(long status) => status switch
    {
        0 => MediaAccessStatus.NotDetermined,
        1 => MediaAccessStatus.Restricted,
        2 => MediaAccessStatus.Denied,
        3 => MediaAccessStatus.Granted,
        _ => MediaAccessStatus.Unknown,
    };

    /// <summary>Electron's <c>NSAppearance</c> converter: Aqua is light, Dark Aqua dark, anything else unknown.</summary>
    internal static SystemAppearance ToSystemAppearance(string? name) => name switch
    {
        "NSAppearanceNameAqua" => SystemAppearance.Light,
        "NSAppearanceNameDarkAqua" => SystemAppearance.Dark,
        _ => SystemAppearance.Unknown,
    };

    /// <summary>Electron's <c>GetSystemColor</c> names to <c>NSColor</c> class selectors, or null.</summary>
    internal static string? MacSystemColorSelector(string name) => name switch
    {
        "blue" => "systemBlueColor",
        "brown" => "systemBrownColor",
        "gray" => "systemGrayColor",
        "green" => "systemGreenColor",
        "orange" => "systemOrangeColor",
        "pink" => "systemPinkColor",
        "purple" => "systemPurpleColor",
        "red" => "systemRedColor",
        "yellow" => "systemYellowColor",
        _ => null,
    };

    /// <summary>Electron's macOS <c>GetColor</c> names to <c>NSColor</c> class selectors, or null.</summary>
    internal static string? MacColorSelector(string name) => name switch
    {
        "control-background" => "controlBackgroundColor",
        "control" => "controlColor",
        "control-text" => "controlTextColor",
        "disabled-control-text" => "disabledControlTextColor",
        "find-highlight" => "findHighlightColor",
        "grid" => "gridColor",
        "header-text" => "headerTextColor",
        "highlight" => "highlightColor",
        "keyboard-focus-indicator" => "keyboardFocusIndicatorColor",
        "label" => "labelColor",
        "link" => "linkColor",
        "placeholder-text" => "placeholderTextColor",
        "quaternary-label" => "quaternaryLabelColor",
        "scrubber-textured-background" => "scrubberTexturedBackgroundColor",
        "secondary-label" => "secondaryLabelColor",
        "selected-content-background" => "selectedContentBackgroundColor",
        "selected-control" => "selectedControlColor",
        "selected-control-text" => "selectedControlTextColor",
        "selected-menu-item-text" => "selectedMenuItemTextColor",
        "selected-text-background" => "selectedTextBackgroundColor",
        "selected-text" => "selectedTextColor",
        "separator" => "separatorColor",
        "shadow" => "shadowColor",
        "tertiary-label" => "tertiaryLabelColor",
        "text-background" => "textBackgroundColor",
        "text" => "textColor",
        "under-page-background" => "underPageBackgroundColor",
        "unemphasized-selected-content-background" => "unemphasizedSelectedContentBackgroundColor",
        "unemphasized-selected-text-background" => "unemphasizedSelectedTextBackgroundColor",
        "unemphasized-selected-text" => "unemphasizedSelectedTextColor",
        "window-background" => "windowBackgroundColor",
        "window-frame-text" => "windowFrameTextColor",
        _ => null,
    };

    /// <summary>Electron's <c>type</c> string.</summary>
    internal static string UserDefaultTypeName(UserDefaultType type) => type switch
    {
        UserDefaultType.String => "string",
        UserDefaultType.Boolean => "boolean",
        UserDefaultType.Integer => "integer",
        UserDefaultType.Float => "float",
        UserDefaultType.Double => "double",
        UserDefaultType.Url => "url",
        UserDefaultType.Array => "array",
        UserDefaultType.Dictionary => "dictionary",
        _ => ((int)type).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Electron's <c>SetUserDefault</c> argument conversion: the string, bool, float, int or double to store, the
    /// URL's canonical spec, or the JSON of an array or dictionary. gin takes only a JS string as a string and a JS
    /// boolean as a bool, any number as a float or double, and an int32-valued number as an integer.</summary>
    /// <exception cref="ArgumentOutOfRangeException">An undefined type.</exception>
    /// <exception cref="ArgumentException">The value does not fit the type.</exception>
    internal static object CoerceUserDefault(UserDefaultType type, object? value)
    {
        object? result = type switch
        {
            UserDefaultType.String => value as string,
            UserDefaultType.Boolean => value as bool?,
            UserDefaultType.Float => AsNumber(value) is { } f ? (float)f : null,
            UserDefaultType.Integer => AsNumber(value) is { } i && IsInt32(i) ? (int)i : null,
            UserDefaultType.Double => AsNumber(value),
            UserDefaultType.Url => value is string url && Gurl.Spec(url) is { Length: > 0 } spec ? spec : null,
            UserDefaultType.Array => TryValueNode(value) is JsonArray array ? ToElectronJson(array) : null,
            UserDefaultType.Dictionary => TryValueNode(value) is JsonObject dict ? ToElectronJson(dict) : null,
            _ => throw new ArgumentOutOfRangeException(nameof(type), "Invalid type: " + UserDefaultTypeName(type)),
        };
        return result ?? throw UnableToConvert(type);
    }

    private static ArgumentException UnableToConvert(UserDefaultType type) => new("Unable to convert value to: " + UserDefaultTypeName(type));

    // A .NET number as the JS number it would be; anything else is not a number.
    private static double? AsNumber(object? value) => value switch
    {
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        JsonValue node when node.GetValueKind() == JsonValueKind.Number
            => node.TryGetValue<double>(out var d) ? d : double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>V8's <c>IsInt32</c>: integral, within int32 and not negative zero.</summary>
    internal static bool IsInt32(double value)
        => value is >= int.MinValue and <= int.MaxValue && Math.Floor(value) == value && !(value == 0 && double.IsNegative(value));

    private static JsonNode? TryValueNode(object? value)
    {
        try { return ToValueNode(value); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>A JSON-like .NET value as the tree Electron's V8 converter would make of the JS value: numbers as doubles,
    /// lists and string-keyed dictionaries recursively.</summary>
    /// <exception cref="ArgumentException">A value of another kind, or a dictionary key that is not a string.</exception>
    internal static JsonNode? ToValueNode(object? value)
    {
        switch (value)
        {
            case null: return null;
            case JsonNode node: return node.DeepClone();
            case string s: return JsonValue.Create(s);
            case bool b: return JsonValue.Create(b);
            case JsonElement element: return JsonNode.Parse(element.GetRawText());
        }
        if (AsNumber(value) is { } number) return JsonValue.Create(number);
        if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            var obj = new JsonObject();
            foreach (var (k, v) in pairs) obj[k] = ToValueNode(v);
            return obj;
        }
        if (value is IDictionary dictionary)
        {
            var obj = new JsonObject();
            foreach (DictionaryEntry entry in dictionary)
                obj[entry.Key as string ?? throw new ArgumentException("Dictionary keys must be strings.", nameof(value))] = ToValueNode(entry.Value);
            return obj;
        }
        if (value is IEnumerable list)
        {
            var array = new JsonArray();
            foreach (var item in list) array.Add(ToValueNode(item));
            return array;
        }
        throw new ArgumentException($"A {value.GetType()} has no JSON-like value.", nameof(value));
    }

    /// <summary>base::WriteJson of the value Electron's converter makes: int32-valued numbers as integers, other numbers
    /// with a <c>.0</c> when they would read as integers (so <c>NSJSONSerialization</c> keeps them doubles); null when a
    /// number is not finite.</summary>
    internal static string? ToElectronJson(JsonNode? node)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (!Write(writer, node)) return null;
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);

        static bool Write(Utf8JsonWriter writer, JsonNode? node)
        {
            switch (node)
            {
                case null:
                    writer.WriteNullValue();
                    return true;
                case JsonObject obj:
                    writer.WriteStartObject();
                    foreach (var (key, child) in obj)
                    {
                        writer.WritePropertyName(key);
                        if (!Write(writer, child)) return false;
                    }
                    writer.WriteEndObject();
                    return true;
                case JsonArray array:
                    writer.WriteStartArray();
                    foreach (var child in array)
                        if (!Write(writer, child)) return false;
                    writer.WriteEndArray();
                    return true;
            }
            var value = (JsonValue)node;
            switch (value.GetValueKind())
            {
                case JsonValueKind.String:
                    writer.WriteStringValue(value.GetValue<string>());
                    return true;
                case JsonValueKind.True or JsonValueKind.False:
                    writer.WriteBooleanValue(value.GetValueKind() == JsonValueKind.True);
                    return true;
                case JsonValueKind.Number when FormatJsonNumber(AsNumber(value)!.Value) is { } text:
                    writer.WriteRawValue(text, skipInputValidation: true);
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>One number as base::JSONWriter writes Electron's value: an int32 as an integer, another finite double in
    /// round-trip form with <c>.0</c> added when it has no point or exponent; null for NaN and infinities.</summary>
    internal static string? FormatJsonNumber(double value)
    {
        if (!double.IsFinite(value)) return null;
        if (IsInt32(value)) return ((int)value).ToString(CultureInfo.InvariantCulture);
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.AsSpan().IndexOfAny('.', 'e', 'E') < 0 ? text + ".0" : text;
    }
}
