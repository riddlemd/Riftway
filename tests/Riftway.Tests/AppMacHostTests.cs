using System.Runtime.InteropServices;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

// Host-side macOS reads behind app's LaunchServices, bundle and ServiceManagement calls: no NSApp, no writes.
public sealed class AppMacHostTests
{
    [Fact]
    public void HttpsHasADefaultAppWithAPathNameAndIcon()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var id = MacApp.DefaultHandlerBundleId("https");
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Contains(id, MacApp.HandlerBundleIds("https")!);
        var app = MacApp.AppPathForUrl("https://example.com/");
        Assert.NotNull(app);
        Assert.EndsWith(".app", app);
        Assert.False(string.IsNullOrEmpty(MacApp.DisplayName(app)));
        var icon = MacApp.IconForFile(app, [1.0, 2.0]);
        Assert.Equal([1.0, 2.0], icon.Select(i => i.Scale));
        Assert.All(icon, i => Assert.Equal((byte)0x89, i.Png[0]));
    }

    [Fact]
    public async Task AnUnknownSchemeHasNoApp()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var scheme = "riftway-none-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Null(MacApp.DefaultHandlerBundleId(scheme));
        Assert.Null(MacApp.AppPathForUrl(scheme + "://x"));
        Assert.Equal("", RiftwayApp.ProtocolNameFor(scheme + "://x"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RiftwayApp.ProtocolInfoFor(scheme + "://x"));
        Assert.Equal("Unable to retrieve installation path to app", error.Message);
    }

    [Fact]
    public void ProtocolClientChecksRefuseAnInvalidSchemeWithoutAsking()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.False(RiftwayApp.IsProtocolClient("1bad", null, null));
        Assert.False(RiftwayApp.SetProtocolClient("1bad", null, null));
        // The test host has no bundle identifier, so nothing can be registered or removed.
        Assert.Null(MacApp.BundleIdentifier());
        Assert.False(RiftwayApp.IsProtocolClient("https", null, null));
        Assert.False(RiftwayApp.RemoveProtocolClient("https", null, null));
    }

    [Fact]
    public void ApplicationDirectoriesIncludeTheSystemOne()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Contains("/Applications", MacApp.ApplicationDirectories());
        Assert.False(string.IsNullOrEmpty(MacApp.BundlePath()));
    }

    [Fact]
    public void TranslationIsOffForANativeProcess()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) return;
        Assert.False(MacApp.RunningUnderTranslation());
        Assert.False(RiftwayApp.RunningUnderArm64Translation);
    }

    [Fact]
    public void LoginStatusName_MapsSMAppServiceStatus()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal("not-registered", MacApp.LoginStatusName(0));
        Assert.Equal("enabled", MacApp.LoginStatusName(1));
        Assert.Equal("requires-approval", MacApp.LoginStatusName(2));
        Assert.Equal("not-found", MacApp.LoginStatusName(3));
        Assert.Equal("not-registered", MacApp.LoginStatusName(9));
    }

    [Fact]
    public void MainAppLoginItemStatusIsOneOfServiceManagements()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Contains(MacApp.LoginItemStatus("mainAppService", ""), new[] { "not-registered", "enabled", "requires-approval", "not-found" });
        var status = RiftwayApp.ReadLoginItem(null);
        Assert.Equal(status.Status == "enabled", status.OpenAtLogin);
        Assert.False(status.WasOpenedAtLogin);
    }
}
