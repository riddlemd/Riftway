using Riftway;
using Xunit;

namespace Riftway.Tests;

// Electron's SystemNetworkContextManager::CreateHttpAuthDynamicParams reads its switches into the network service's
// auth params; Chrome style keeps the same params in Local State, under these preference names.
public sealed class AuthSwitchTests
{
    private static IReadOnlyList<(string Name, object Value)> For(params string[] args)
        => Session.ElectronAuthPreferences(new CommandLine(args), posix: false);

    [Fact]
    public void NoSwitches_SetNothing() => Assert.Empty(For());

    [Fact]
    public void ServerWhitelist_IsTheServerAllowlist()
        => Assert.Equal([("auth.server_allowlist", (object)"*.example.com,intranet")], For("--auth-server-whitelist=*.example.com,intranet"));

    [Fact]
    public void DelegateWhitelist_IsTheDelegateAllowlist()
        => Assert.Equal([("auth.negotiate_delegate_whitelist", (object)"kdc.example.com")], For("--auth-negotiate-delegate-whitelist=kdc.example.com"));

    [Fact]
    public void NegotiatePort_IsOn() => Assert.Equal([("auth.enable_negotiate_port", (object)true)], For("--enable-auth-negotiate-port"));

    [Fact]
    public void DisableNtlmV2_OnlyWherePosixRegistersIt()
    {
        Assert.Empty(For("--disable-ntlm-v2"));
        Assert.Equal([("auth.ntlm_v2_enabled", (object)false)], Session.ElectronAuthPreferences(new CommandLine(["--disable-ntlm-v2"]), posix: true));
    }

    [Fact]
    public void AnEmptyWhitelist_IsSetEmpty()
        => Assert.Equal([("auth.server_allowlist", (object)"")], For("--auth-server-whitelist="));

    [Fact]
    public void All_InOrder()
        => Assert.Equal(["auth.server_allowlist", "auth.negotiate_delegate_whitelist", "auth.enable_negotiate_port"],
            For("--enable-auth-negotiate-port", "--auth-negotiate-delegate-whitelist=k", "--auth-server-whitelist=s").Select(p => p.Name));
}
