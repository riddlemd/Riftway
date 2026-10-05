using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>What a page asked for; Electron's permission request <c>details</c>.</summary>
/// <param name="RequestingUrl">The URL asking: the frame's for media, the origin otherwise (CEF gives no more).</param>
/// <param name="IsMainFrame">Whether the main frame asked; true for prompts, whose frame CEF does not give.</param>
public sealed record PermissionRequestDetails(string RequestingUrl, bool IsMainFrame)
{
    /// <summary>For <c>media</c>: <c>audio</c> and/or <c>video</c>. Electron's <c>mediaTypes</c>.</summary>
    public IReadOnlyList<string> MediaTypes { get; init; } = [];

    /// <summary>For <c>media</c>: the origin asking. Electron's <c>securityOrigin</c>.</summary>
    public string? SecurityOrigin { get; init; }
}

/// <summary>Electron's permission names for CEF's.</summary>
internal static class PermissionNames
{
    private static readonly (cef_permission_request_types_t Type, string Name)[] s_names =
    [
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_CLIPBOARD, "clipboard-read"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_GEOLOCATION, "geolocation"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_IDLE_DETECTION, "idle-detection"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAMERA_STREAM, "media"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIC_STREAM, "media"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAMERA_PAN_TILT_ZOOM, "media"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_PROTECTED_MEDIA_IDENTIFIER, "mediaKeySystem"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIDI_SYSEX, "midiSysex"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_NOTIFICATIONS, "notifications"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_POINTER_LOCK, "pointerLock"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_KEYBOARD_LOCK, "keyboardLock"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_STORAGE_ACCESS, "storage-access"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_TOP_LEVEL_STORAGE_ACCESS, "top-level-storage-access"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_WINDOW_MANAGEMENT, "window-management"),
        (cef_permission_request_types_t.CEF_PERMISSION_TYPE_FILE_SYSTEM_ACCESS, "fileSystem"),
    ];

    /// <summary>The distinct names in a prompt's mask; bits Electron has no name for are <c>unknown</c>.</summary>
    internal static IReadOnlyList<string> Of(uint mask)
    {
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

    /// <summary>Electron's media types for CEF's media mask.</summary>
    internal static IReadOnlyList<string> MediaTypesOf(uint mask)
    {
        var types = new List<string>();
        const uint audio = (uint)(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_AUDIO_CAPTURE | cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_AUDIO_CAPTURE);
        const uint video = (uint)(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE | cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE);
        if ((mask & video) != 0) types.Add("video");
        if ((mask & audio) != 0) types.Add("audio");
        return types;
    }
}
