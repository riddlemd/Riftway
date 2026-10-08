using Oriel.Cef.Interop;

namespace Oriel.Tests;

public sealed class PermissionNamesTests
{
    [Theory]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_AR_SESSION, "ar")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAMERA_PAN_TILT_ZOOM, "media")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAMERA_STREAM, "media")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAPTURED_SURFACE_CONTROL, "captured-surface-control")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_CLIPBOARD, "clipboard-read")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_TOP_LEVEL_STORAGE_ACCESS, "top-level-storage-access")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOCAL_FONTS, "local-fonts")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_GEOLOCATION, "geolocation")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_HAND_TRACKING, "hand-tracking")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_IDLE_DETECTION, "idle-detection")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIC_STREAM, "media")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIDI_SYSEX, "midiSysex")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_NOTIFICATIONS, "notifications")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_KEYBOARD_LOCK, "keyboardLock")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_POINTER_LOCK, "pointerLock")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_PROTECTED_MEDIA_IDENTIFIER, "mediaKeySystem")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_STORAGE_ACCESS, "storage-access")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_VR_SESSION, "vr")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_WEB_APP_INSTALLATION, "web-app-installation")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_WINDOW_MANAGEMENT, "window-management")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_FILE_SYSTEM_ACCESS, "fileSystem")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOCAL_NETWORK_ACCESS_DEPRECATED, "local-network-access")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOCAL_NETWORK, "local-network")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_LOOPBACK_NETWORK, "loopback-network")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_SENSORS, "sensors")]
    public void Of_NamesEachPermissionAsElectron(cef_permission_request_types_t type, string name)
        => Assert.Equal([name], PermissionNames.Of((uint)type));

    [Fact]
    public void Of_CameraAndMicrophoneAreOneMediaRequest()
        => Assert.Equal(["media"], PermissionNames.Of((uint)(cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAMERA_STREAM | cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIC_STREAM)));

    [Fact]
    public void Of_KeepsEveryDistinctPermission()
        => Assert.Equal(["geolocation", "notifications"], PermissionNames.Of((uint)(cef_permission_request_types_t.CEF_PERMISSION_TYPE_GEOLOCATION | cef_permission_request_types_t.CEF_PERMISSION_TYPE_NOTIFICATIONS)));

    [Theory]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_DISK_QUOTA)]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_IDENTITY_PROVIDER)]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_REGISTER_PROTOCOL_HANDLER)]
    public void Of_BitsElectronHasNoNameForAreUnknown(cef_permission_request_types_t type)
        => Assert.Equal(["unknown"], PermissionNames.Of((uint)type));

    [Fact]
    public void Of_UnknownFollowsTheNamedOnes()
        => Assert.Equal(["notifications", "unknown"], PermissionNames.Of((uint)(cef_permission_request_types_t.CEF_PERMISSION_TYPE_NOTIFICATIONS | cef_permission_request_types_t.CEF_PERMISSION_TYPE_DISK_QUOTA)));

    [Fact]
    public void Of_MultipleDownloadsIsNotAsked()
    {
        Assert.Empty(PermissionNames.Of((uint)cef_permission_request_types_t.CEF_PERMISSION_TYPE_MULTIPLE_DOWNLOADS));
        Assert.Equal(["geolocation"], PermissionNames.Of((uint)(cef_permission_request_types_t.CEF_PERMISSION_TYPE_MULTIPLE_DOWNLOADS | cef_permission_request_types_t.CEF_PERMISSION_TYPE_GEOLOCATION)));
    }

    [Theory]
    [InlineData(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_AUDIO_CAPTURE, new[] { "audio" })]
    [InlineData(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE, new[] { "video" })]
    [InlineData(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_AUDIO_CAPTURE | cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE, new[] { "audio", "video" })]
    [InlineData(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_AUDIO_CAPTURE | cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE, new string[0])]
    public void MediaTypesOf_ListsDevicesAudioFirst(cef_media_access_permission_types_t mask, string[] types)
        => Assert.Equal(types, PermissionNames.MediaTypesOf((uint)mask));

    [Fact]
    public void DesktopOf_KeepsOnlyScreenCapture()
    {
        const uint all = (uint)(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_AUDIO_CAPTURE | cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE
            | cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_AUDIO_CAPTURE | cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE);
        Assert.Equal((uint)(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_AUDIO_CAPTURE | cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE), PermissionNames.DesktopOf(all));
        Assert.Equal(0u, PermissionNames.DesktopOf((uint)cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE));
    }

    private const uint DesktopVideo = (uint)cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE;
    private const uint DesktopAudio = (uint)cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_AUDIO_CAPTURE;
    private static readonly DisplayMediaSource s_screen = new("screen:0:0", "Entire screen");

    [Fact]
    public void DisplayMediaAllowed_NullRefusesWithoutError()
        => Assert.Equal((0u, (string?)null), PermissionNames.DisplayMediaAllowed(DesktopVideo, null, loopbackSupported: true));

    [Fact]
    public void DisplayMediaAllowed_VideoAndLoopbackAudio()
        => Assert.Equal((DesktopVideo | DesktopAudio, (string?)null), PermissionNames.DisplayMediaAllowed(DesktopVideo | DesktopAudio, new DisplayMediaStreams { Video = s_screen, Audio = "loopback" }, loopbackSupported: true));

    [Fact]
    public void DisplayMediaAllowed_LoopbackWithMuteIsSystemAudio()
        => Assert.Equal((DesktopVideo | DesktopAudio, (string?)null), PermissionNames.DisplayMediaAllowed(DesktopVideo | DesktopAudio, new DisplayMediaStreams { Video = s_screen, Audio = "loopbackWithMute" }, loopbackSupported: true));

    [Fact]
    public void DisplayMediaAllowed_AudioLeftOutGivesVideoOnly()
        => Assert.Equal((DesktopVideo, (string?)null), PermissionNames.DisplayMediaAllowed(DesktopVideo | DesktopAudio, new DisplayMediaStreams { Video = s_screen }, loopbackSupported: true));

    [Fact]
    public void DisplayMediaAllowed_AudioNotRequestedIsNotGranted()
        => Assert.Equal((DesktopVideo, (string?)null), PermissionNames.DisplayMediaAllowed(DesktopVideo, new DisplayMediaStreams { Video = s_screen, Audio = "loopback" }, loopbackSupported: true));

    [Fact]
    public void DisplayMediaAllowed_RequestedVideoMissingIsElectronsError()
        => Assert.Equal((0u, "Video was requested, but no video stream was provided"),
            PermissionNames.DisplayMediaAllowed(DesktopVideo | DesktopAudio, new DisplayMediaStreams { Audio = "loopback" }, loopbackSupported: true));

    [Fact]
    public void DisplayMediaAllowed_OtherAudioIsElectronsError()
        => Assert.Equal((0u, "audio must be a WebFrameMain, \"loopback\" or \"loopbackWithMute\""),
            PermissionNames.DisplayMediaAllowed(DesktopVideo | DesktopAudio, new DisplayMediaStreams { Video = s_screen, Audio = "speakers" }, loopbackSupported: true));

    [Fact]
    public void DisplayMediaAllowed_LoopbackLeftOutWhereUnsupported()
        => Assert.Equal((DesktopVideo, (string?)null), PermissionNames.DisplayMediaAllowed(DesktopVideo | DesktopAudio, new DisplayMediaStreams { Video = s_screen, Audio = "loopback" }, loopbackSupported: false));

    [Fact]
    public void DisplayMediaAllowed_OtherAudioIsAnErrorEvenWhereLoopbackIsUnsupported()
        => Assert.Equal(0u, PermissionNames.DisplayMediaAllowed(DesktopVideo | DesktopAudio, new DisplayMediaStreams { Video = s_screen, Audio = "speakers" }, loopbackSupported: false).Allowed);

    [Fact]
    public void SettingsOf_NamesWhereChromiumStoresTheAnswer()
    {
        Assert.Equal([cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_CLIPBOARD_READ_WRITE, cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_GEOLOCATION],
            PermissionNames.SettingsOf((uint)(cef_permission_request_types_t.CEF_PERMISSION_TYPE_GEOLOCATION | cef_permission_request_types_t.CEF_PERMISSION_TYPE_CLIPBOARD)));
        Assert.Equal([cef_content_setting_types_t.CEF_CONTENT_SETTING_TYPE_NOTIFICATIONS], PermissionNames.SettingsOf((uint)cef_permission_request_types_t.CEF_PERMISSION_TYPE_NOTIFICATIONS));
        Assert.Empty(PermissionNames.SettingsOf((uint)cef_permission_request_types_t.CEF_PERMISSION_TYPE_MULTIPLE_DOWNLOADS));
    }

    [Theory]
    [InlineData("geolocation", false, false, "geolocation", null)]
    [InlineData("notifications", false, false, "notifications", null)]
    [InlineData("push", false, false, "notifications", null)]
    [InlineData("camera", false, false, "media", "video")]
    [InlineData("microphone", false, false, "media", "audio")]
    [InlineData("midi", false, false, "midi", null)]
    [InlineData("midi", true, false, "midiSysex", null)]
    [InlineData("clipboard-read", false, false, "clipboard-read", null)]
    [InlineData("clipboard-write", false, false, "clipboard-sanitized-write", null)]
    [InlineData("clipboard-write", false, true, "clipboard-read", null)]
    [InlineData("gyroscope", false, false, "sensors", null)]
    [InlineData("keyboard-lock", false, false, "keyboardLock", null)]
    [InlineData("pointer-lock", false, false, "pointerLock", null)]
    [InlineData("fullscreen", false, false, "automatic-fullscreen", null)]
    [InlineData("persistent-storage", false, false, "persistent-storage", null)]
    [InlineData("window-management", false, false, "window-management", null)]
    [InlineData("screen-wake-lock", false, false, "screen-wake-lock", null)]
    public void ForQuery_NamesTheCheckAsElectron(string name, bool sysex, bool unsanitized, string permission, string? mediaType)
        => Assert.Equal((permission, mediaType), PermissionNames.ForQuery(new PermissionQuery(name, sysex, unsanitized)));

    [Fact]
    public void ForQuery_AnUnknownNameKeepsChromiumsAnswer() => Assert.Null(PermissionNames.ForQuery(new PermissionQuery("teleportation")));

    [Theory]
    [InlineData("https://oriel.test", "https://oriel.test/")]
    [InlineData("https://oriel.test:8443/", "https://oriel.test:8443/")]
    [InlineData("null", "null")]
    [InlineData("", "")]
    public void OriginUrl_IsTheOriginsUrl(string origin, string url) => Assert.Equal(url, PermissionNames.OriginUrl(origin));
}
