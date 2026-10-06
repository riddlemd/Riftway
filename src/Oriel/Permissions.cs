using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>What a page asked for; Electron's permission request <c>details</c>.</summary>
/// <param name="RequestingUrl">The last URL the requesting frame loaded. For a prompt CEF gives only the origin, so this is
/// the URL of the page's frame from that origin (the main frame first), else the origin itself.</param>
/// <param name="IsMainFrame">Whether the main frame asked.</param>
public sealed record PermissionRequestDetails(string RequestingUrl, bool IsMainFrame)
{
    /// <summary>For <c>media</c>: <c>audio</c> and/or <c>video</c> devices, in that order; empty for a screen capture.
    /// Electron's <c>mediaTypes</c>.</summary>
    public IReadOnlyList<string> MediaTypes { get; init; } = [];

    /// <summary>For <c>media</c>: the origin asking, as a URL (<c>https://host/</c>). Electron's <c>securityOrigin</c>.</summary>
    public string? SecurityOrigin { get; init; }
}

/// <summary>What a page checked; Electron's permission check <c>details</c>.</summary>
/// <param name="RequestingUrl">The last URL the checking frame loaded.</param>
/// <param name="IsMainFrame">Whether the main frame checked.</param>
public sealed record PermissionCheckDetails(string? RequestingUrl, bool IsMainFrame)
{
    /// <summary>The origin of the page's main frame, as a URL. Electron's <c>embeddingOrigin</c>.</summary>
    public string? EmbeddingOrigin { get; init; }

    /// <summary>For <c>media</c>: <c>video</c> (camera) or <c>audio</c> (microphone). Electron's <c>mediaType</c>.</summary>
    public string? MediaType { get; init; }
}

/// <summary>A page's <c>getDisplayMedia()</c>. Electron's display media <c>request</c>.</summary>
/// <param name="Frame">The frame asking, or null when it is not known (gone, or in another process).</param>
/// <param name="SecurityOrigin">The origin asking, as a URL.</param>
/// <param name="VideoRequested">Whether the page asked for video.</param>
/// <param name="AudioRequested">Whether the page asked for audio.</param>
/// <param name="UserGesture">Always false: CEF does not say whether a gesture was active (drift E30).</param>
public sealed record DisplayMediaRequest(WebFrameMain? Frame, string SecurityOrigin, bool VideoRequested, bool AudioRequested, bool UserGesture);

/// <summary>What a display media request handler grants. Electron's <c>streams</c>.</summary>
public sealed record DisplayMediaStreams
{
    /// <summary>The video source granted. Required when video was requested.</summary>
    /// <remarks>CEF captures the whole desktop whatever the source names (drift E30).</remarks>
    public DisplayMediaSource? Video { get; init; }

    /// <summary><c>loopback</c> or <c>loopbackWithMute</c> for the system's audio; null for none.</summary>
    public string? Audio { get; init; }
}

/// <summary>A capture source: Electron's <c>DesktopCapturerSource</c> <c>id</c> and <c>name</c>.</summary>
public sealed record DisplayMediaSource(string Id, string Name);

/// <summary>Electron's permission names for CEF's, and the content settings Chromium remembers them in.</summary>
internal static class PermissionNames
{
    private static readonly (cef_permission_request_types_t Type, string Name)[] s_names =
    [
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_AR_SESSION, "ar"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAMERA_PAN_TILT_ZOOM, "media"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAMERA_STREAM, "media"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAPTURED_SURFACE_CONTROL, "captured-surface-control"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_CLIPBOARD, "clipboard-read"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_TOP_LEVEL_STORAGE_ACCESS, "top-level-storage-access"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOCAL_FONTS, "local-fonts"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_GEOLOCATION, "geolocation"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_HAND_TRACKING, "hand-tracking"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_IDLE_DETECTION, "idle-detection"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIC_STREAM, "media"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIDI_SYSEX, "midiSysex"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_NOTIFICATIONS, "notifications"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_KEYBOARD_LOCK, "keyboardLock"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_POINTER_LOCK, "pointerLock"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_PROTECTED_MEDIA_IDENTIFIER, "mediaKeySystem"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_STORAGE_ACCESS, "storage-access"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_VR_SESSION, "vr"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_WEB_APP_INSTALLATION, "web-app-installation"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_WINDOW_MANAGEMENT, "window-management"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_FILE_SYSTEM_ACCESS, "fileSystem"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOCAL_NETWORK_ACCESS_DEPRECATED, "local-network-access"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOCAL_NETWORK, "local-network"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOOPBACK_NETWORK, "loopback-network"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_SENSORS, "sensors"),
    ];

    /// <summary>Chrome-only prompts with no Electron permission: Electron never gates them, so they are granted unasked.</summary>
    internal const uint Ungated = (uint)cef_permission_request_types_t.CEF_PERMISSION_TYPE_MULTIPLE_DOWNLOADS;

    /// <summary>The distinct names in a prompt's mask; bits Electron has no name for are <c>unknown</c>.</summary>
    internal static IReadOnlyList<string> Of(uint mask)
    {
        mask &= ~Ungated;
        var names = new List<string>();
        var known = 0u;
        foreach (var (type, name) in s_names)
        {
            if ((mask & (uint)type) == 0) continue;
            known |= (uint)type;
            if (!names.Contains(name)) names.Add(name);
        }
        if ((mask & ~known) != 0) names.Add("unknown");
        return names;
    }

    private const uint DesktopMedia = (uint)(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_AUDIO_CAPTURE
        | cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE);

    /// <summary>Electron's <c>mediaTypes</c> for CEF's media mask: devices only, audio before video, as Electron lists them.</summary>
    internal static IReadOnlyList<string> MediaTypesOf(uint mask)
    {
        var types = new List<string>();
        if ((mask & (uint)cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_AUDIO_CAPTURE) != 0) types.Add("audio");
        if ((mask & (uint)cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE) != 0) types.Add("video");
        return types;
    }

    /// <summary>The screen-capture part of CEF's media mask: <c>getDisplayMedia()</c> (and legacy desktop <c>getUserMedia</c>).</summary>
    internal static uint DesktopOf(uint mask) => mask & DesktopMedia;

    /// <summary>The CEF media mask a display media handler's answer allows, or null with Electron's error when it refuses.</summary>
    internal static (uint Allowed, string? Error) DisplayMediaAllowed(uint requested, DisplayMediaStreams? streams)
    {
        const uint video = (uint)cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE;
        const uint audio = (uint)cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_AUDIO_CAPTURE;
        if (streams is null) return (0, null);
        var allowed = 0u;
        if ((requested & video) != 0 && streams.Video is not null) allowed |= video;
        if ((requested & audio) != 0 && streams.Audio is { } device)
        {
            if (device is not ("loopback" or "loopbackWithMute")) return (0, "audio must be a WebFrameMain, \"loopback\" or \"loopbackWithMute\"");
            allowed |= audio;
        }
        if ((requested & video) != 0 && (allowed & video) == 0) return (0, "Video was requested, but no video stream was provided");
        return (allowed, null);
    }

    private static readonly (cef_permission_request_types_t Type, cef_content_setting_types_t Setting)[] s_settings =
    [
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_AR_SESSION, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_AR),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAPTURED_SURFACE_CONTROL, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_CAPTURED_SURFACE_CONTROL),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_CLIPBOARD, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_CLIPBOARD_READ_WRITE),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_TOP_LEVEL_STORAGE_ACCESS, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_TOP_LEVEL_STORAGE_ACCESS),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOCAL_FONTS, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_LOCAL_FONTS),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_GEOLOCATION, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_GEOLOCATION),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_HAND_TRACKING, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_HAND_TRACKING),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_IDLE_DETECTION, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_IDLE_DETECTION),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIDI_SYSEX, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_MIDI_SYSEX),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_NOTIFICATIONS, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_NOTIFICATIONS),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_KEYBOARD_LOCK, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_KEYBOARD_LOCK),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_POINTER_LOCK, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_POINTER_LOCK),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_PROTECTED_MEDIA_IDENTIFIER, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_PROTECTED_MEDIA_IDENTIFIER),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_STORAGE_ACCESS, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_STORAGE_ACCESS),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_VR_SESSION, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_VR),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_WEB_APP_INSTALLATION, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_WEB_APP_INSTALLATION),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_WINDOW_MANAGEMENT, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_WINDOW_MANAGEMENT),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOCAL_NETWORK_ACCESS_DEPRECATED, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_LOCAL_NETWORK_ACCESS),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOCAL_NETWORK, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_LOCAL_NETWORK),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOOPBACK_NETWORK, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_LOOPBACK_NETWORK),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_SENSORS, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_SENSORS),
    ];

    /// <summary>The content settings Chromium stores a prompt's answer in.</summary>
    internal static IReadOnlyList<cef_content_setting_types_t> SettingsOf(uint mask)
        => [.. s_settings.Where(s => (mask & (uint)s.Type) != 0).Select(s => s.Setting)];

    /// <summary>The preference names (<c>profile.content_settings.exceptions.*</c>) holding remembered prompt answers.</summary>
    internal static readonly string[] RememberedPreferences =
    [
        "ar", "captured_surface_control", "clipboard", "geolocation", "hand_tracking", "idle_detection", "keyboard_lock",
        "local_fonts", "local_network", "local_network_access", "loopback_network", "midi_sysex", "notifications",
        "pointer_lock", "protected_media_identifier", "sensors", "storage_access", "top_level_storage_access", "vr",
        "web_app_installation", "window_placement",
    ];

    /// <summary>Electron's name, and <c>mediaType</c>, for a <c>navigator.permissions.query()</c> descriptor; null for a
    /// name Oriel does not map, whose answer stays Chromium's.</summary>
    internal static (string Name, string? MediaType)? ForQuery(PermissionQuery query) => query.Name switch
    {
        "geolocation" => ("geolocation", null),
        "notifications" or "push" => ("notifications", null),
        "midi" => (query.Sysex ? "midiSysex" : "midi", null),
        "camera" => ("media", "video"),
        "microphone" => ("media", "audio"),
        "background-fetch" => ("background-fetch", null),
        "background-sync" => ("background-sync", null),
        "persistent-storage" => ("persistent-storage", null),
        "accelerometer" or "gyroscope" or "magnetometer" or "ambient-light-sensor" => ("sensors", null),
        "clipboard-read" => ("clipboard-read", null),
        "clipboard-write" => (query.AllowWithoutSanitization ? "clipboard-read" : "clipboard-sanitized-write", null),
        "payment-handler" => ("payment-handler", null),
        "periodic-background-sync" => ("periodic-background-sync", null),
        "screen-wake-lock" => ("screen-wake-lock", null),
        "system-wake-lock" => ("system-wake-lock", null),
        "nfc" => ("nfc", null),
        "window-management" => ("window-management", null),
        "local-fonts" => ("local-fonts", null),
        "display-capture" => ("display-capture", null),
        "storage-access" => ("storage-access", null),
        "top-level-storage-access" => ("top-level-storage-access", null),
        "captured-surface-control" => ("captured-surface-control", null),
        "speaker-selection" => ("speaker-selection", null),
        "keyboard-lock" => ("keyboardLock", null),
        "pointer-lock" => ("pointerLock", null),
        "fullscreen" => ("automatic-fullscreen", null),
        "idle-detection" => ("idle-detection", null),
        "web-app-installation" => ("web-app-installation", null),
        "web-printing" => ("web-printing", null),
        "local-network-access" => ("local-network-access", null),
        "local-network" => ("local-network", null),
        "loopback-network" => ("loopback-network", null),
        "ar" => ("ar", null),
        "vr" => ("vr", null),
        "hand-tracking" => ("hand-tracking", null),
        "smart-card" => ("smart-card", null),
        _ => null,
    };

    /// <summary>An origin as Electron reports it: the URL of the origin, with its trailing slash.</summary>
    internal static string OriginUrl(string origin)
        => origin.Length == 0 || origin == "null" || origin.EndsWith('/') ? origin : origin + "/";
}

/// <summary>A page's <c>navigator.permissions.query()</c> descriptor: its name and the flags that change the permission.</summary>
internal sealed record PermissionQuery(string Name, bool Sysex = false, bool AllowWithoutSanitization = false);
