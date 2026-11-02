using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class SandboxTests
{
    private static readonly string[] s_noArgs = ["app.dll"];

    [Fact]
    public void NoSandboxSwitch_WinsOverTheBootstrapDefault()
        => Assert.False(RiftwayApp.SandboxChildren(requested: false, bootstrapSandbox: true, canSandbox: false, ["app.dll", "--no-sandbox"]));

    [Fact]
    public void NoSandboxSwitch_WinsOverEnableSandbox()
        => Assert.False(RiftwayApp.SandboxChildren(requested: true, bootstrapSandbox: false, canSandbox: true, ["app.dll", "--no-sandbox"]));

    [Fact]
    public void Bootstrap_SandboxesWithoutEnableSandbox()
        => Assert.True(RiftwayApp.SandboxChildren(requested: false, bootstrapSandbox: true, canSandbox: false, s_noArgs));

    [Fact]
    public void ElsewhereWithoutEnableSandbox_ChildrenAreNotSandboxed()
        => Assert.False(RiftwayApp.SandboxChildren(requested: false, bootstrapSandbox: false, canSandbox: true, s_noArgs));

    [Fact]
    public void EnableSandbox_WhereTheLayoutAllows_Sandboxes()
        => Assert.True(RiftwayApp.SandboxChildren(requested: true, bootstrapSandbox: false, canSandbox: true, s_noArgs));

    [Fact]
    public void EnableSandbox_WhereTheLayoutCannot_Throws()
        => Assert.Throws<PlatformNotSupportedException>(() => RiftwayApp.SandboxChildren(requested: true, bootstrapSandbox: false, canSandbox: false, s_noArgs));

    [Fact]
    public void ClientDll_IsNamedAfterTheExe()
        => Assert.Equal(Path.Combine("C:", "apps", "My App.dll"), BootstrapHost.ClientDllPath(Path.Combine("C:", "apps", "My App.exe")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ClientDll_NeedsAProcessPath(string? processPath) => Assert.Null(BootstrapHost.ClientDllPath(processPath));

    [WindowsFact]
    public void ClientDll_IsNotFoundOutsideTheBootstrapLayout()
    {
        // The test host is a .NET apphost: nothing exports the sandbox info.
        if (!OperatingSystem.IsWindows()) return;
        unsafe
        {
            Assert.False(BootstrapHost.TryGetSandboxInfo(out var info));
            Assert.True(info is null);
        }
    }
}
