using Oriel.Cef.Interop;

namespace Oriel.Tests;

public sealed class PermissionNamesTests
{
    [Theory]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_NOTIFICATIONS, "notifications")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_GEOLOCATION, "geolocation")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_CLIPBOARD, "clipboard-read")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_PROTECTED_MEDIA_IDENTIFIER, "mediaKeySystem")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIDI_SYSEX, "midiSysex")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_POINTER_LOCK, "pointerLock")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_KEYBOARD_LOCK, "keyboardLock")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_STORAGE_ACCESS, "storage-access")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_TOP_LEVEL_STORAGE_ACCESS, "top-level-storage-access")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_WINDOW_MANAGEMENT, "window-management")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_FILE_SYSTEM_ACCESS, "fileSystem")]
    [InlineData(cef_permission_request_types_t.CEF_PERMISSION_TYPE_IDLE_DETECTION, "idle-detection")]
    public void Of_NamesEachPermissionAsElectron(cef_permission_request_types_t type, string name)
        => Assert.Equal([name], PermissionNames.Of((uint)type));

    [Fact]
    public void Of_CameraAndMicrophoneAreOneMediaRequest()
        => Assert.Equal(["media"], PermissionNames.Of((uint)(cef_permission_request_types_t.CEF_PERMISSION_TYPE_CAMERA_STREAM | cef_permission_request_types_t.CEF_PERMISSION_TYPE_MIC_STREAM)));

    [Fact]
    public void Of_KeepsEveryDistinctPermission()
        => Assert.Equal(["geolocation", "notifications"], PermissionNames.Of((uint)(cef_permission_request_types_t.CEF_PERMISSION_TYPE_GEOLOCATION | cef_permission_request_types_t.CEF_PERMISSION_TYPE_NOTIFICATIONS)));

    [Fact]
    public void Of_UnnamedBitsAreUnknown()
    {
        Assert.Equal(["unknown"], PermissionNames.Of((uint)cef_permission_request_types_t.CEF_PERMISSION_TYPE_AR_SESSION));
        Assert.Equal(["notifications", "unknown"], PermissionNames.Of((uint)(cef_permission_request_types_t.CEF_PERMISSION_TYPE_NOTIFICATIONS | cef_permission_request_types_t.CEF_PERMISSION_TYPE_VR_SESSION)));
    }

    [Theory]
    [InlineData(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_AUDIO_CAPTURE, "audio")]
    [InlineData(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_AUDIO_CAPTURE, "audio")]
    [InlineData(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE, "video")]
    [InlineData(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DESKTOP_VIDEO_CAPTURE, "video")]
    public void MediaTypesOf_NamesTheMedia(cef_media_access_permission_types_t type, string name)
        => Assert.Equal([name], PermissionNames.MediaTypesOf((uint)type));

    [Fact]
    public void MediaTypesOf_Both() => Assert.Equal(["video", "audio"], PermissionNames.MediaTypesOf(
        (uint)(cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_AUDIO_CAPTURE | cef_media_access_permission_types_t.CEF_MEDIA_PERMISSION_DEVICE_VIDEO_CAPTURE)));
}
