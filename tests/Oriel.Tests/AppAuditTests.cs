using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oriel.Tests;

public sealed class AppAuditTests
{
    private static string Dotnet => OperatingSystem.IsWindows() ? @"C:\dotnet\dotnet.exe" : "/usr/local/share/dotnet/dotnet";

    [Fact]
    public void IsPackaged_IsAnyExecutableButTheDotnetHostUnlessForced()
    {
        Assert.False(OrielApp.IsPackagedExecutable(Dotnet, forced: false));
        Assert.True(OrielApp.IsPackagedExecutable(Dotnet, forced: true));
        Assert.True(OrielApp.IsPackagedExecutable("/Applications/My.app/Contents/MacOS/My", forced: false));
        Assert.False(OrielApp.IsPackagedExecutable(OperatingSystem.IsWindows() ? @"C:\x\DOTNET.EXE" : "/x/DOTNET", forced: false));
    }

    [Fact]
    public void RelaunchArgv_IsTheOriginalCommandLineByDefault()
    {
        Assert.Equal(["/app/My", "--a", "b"], OrielApp.RelaunchArgv("/app/My", ["/app/My.dll", "--a", "b"], null));
        // Under the dotnet host, argv[0] is the entry assembly the host needs again.
        Assert.Equal([Dotnet, "/app/My.dll", "--a"], OrielApp.RelaunchArgv(Dotnet, ["/app/My.dll", "--a"], null));
        Assert.Equal(["/app/My", "--a"], OrielApp.RelaunchArgv("/app/My", ["/app/My.dll", "--a"], new RelaunchOptions()));
    }

    [Fact]
    public void RelaunchArgv_WithEitherOptionIsTheExecutableAndArgsAlone()
    {
        Assert.Equal(["/app/My", "--x"], OrielApp.RelaunchArgv("/app/My", ["/app/My.dll", "--a"], new RelaunchOptions { Args = ["--x"] }));
        Assert.Equal(["/other"], OrielApp.RelaunchArgv("/app/My", ["/app/My.dll", "--a"], new RelaunchOptions { ExecPath = "/other" }));
        Assert.Equal(["/other", "1"], OrielApp.RelaunchArgv("/app/My", ["/app/My.dll"], new RelaunchOptions { ExecPath = "/other", Args = ["1"] }));
        Assert.Equal(["/app/My"], OrielApp.RelaunchArgv("/app/My", ["/app/My.dll", "--a"], new RelaunchOptions { ExecPath = "", Args = [] }));
    }

    [Fact]
    public void ProtocolScheme_IsRfc3986AndPosixUrlsGiveItLowerCased()
    {
        Assert.True(OrielApp.IsValidProtocolScheme("my-app+x.1"));
        Assert.False(OrielApp.IsValidProtocolScheme("1app"));
        Assert.False(OrielApp.IsValidProtocolScheme(""));
        Assert.False(OrielApp.IsValidProtocolScheme("a_b"));
        Assert.Equal("myapp", OrielApp.PosixSchemeOf("  MyApp://x"));
        Assert.Equal("", OrielApp.PosixSchemeOf("no scheme"));
        Assert.Equal("", OrielApp.PosixSchemeOf("1x:y"));
    }

    [Fact]
    public void MacFallbackHandler_IsTheFirstOtherAppElseNone()
    {
        Assert.Equal("com.other", OrielApp.MacFallbackHandler("com.me", ["com.me", null, "com.other", "com.third"]));
        Assert.Equal("None", OrielApp.MacFallbackHandler("com.me", ["com.me", null]));
        // isEqualToString: a case variant of this app counts as another app.
        Assert.Equal("COM.ME", OrielApp.MacFallbackHandler("com.me", ["COM.ME"]));
    }

    [Fact]
    public void MacLoginService_NamesTheTypeAndRequiresANameBeyondTheApp()
    {
        Assert.Equal(("mainAppService", ""), OrielApp.MacLoginService(LoginItemType.MainAppService, null));
        Assert.Equal(("agentService", "a.plist"), OrielApp.MacLoginService(LoginItemType.AgentService, "a.plist"));
        Assert.Equal(("daemonService", "d"), OrielApp.MacLoginService(LoginItemType.DaemonService, "d"));
        Assert.Equal(("loginItemService", "l"), OrielApp.MacLoginService(LoginItemType.LoginItemService, "l"));
        Assert.StartsWith("'name' is required when type is not mainAppService",
            Assert.Throws<ArgumentException>(() => OrielApp.MacLoginService(LoginItemType.AgentService, "")).Message);
    }

    [Fact]
    public void InApplicationsFolder_IsUnderASystemFolderOrAnyApplicationsDirectory()
    {
        string[] dirs = ["/Applications", "/Users/u/Applications"];
        Assert.True(OrielApp.InApplicationsFolder("/Applications/My.app", dirs));
        Assert.True(OrielApp.InApplicationsFolder("/Users/u/Applications/My.app", dirs));
        Assert.True(OrielApp.InApplicationsFolder("/Volumes/Data/Applications/My.app", dirs));
        Assert.False(OrielApp.InApplicationsFolder("/Users/u/Downloads/My.app", dirs));
        Assert.False(OrielApp.InApplicationsFolder("/Users/u/MyApplications/My.app", dirs));
    }

    [Fact]
    public void MacAboutPanelOptions_AreTheStringOptionsCapitalised()
    {
        var panel = OrielApp.MacAboutPanelOptions(new AboutPanelOptions { ApplicationName = "N", Version = "7", Credits = "C", Authors = ["a"], IconPath = "/i.png" });
        Assert.Equal(new Dictionary<string, string> { ["ApplicationName"] = "N", ["Version"] = "7", ["Credits"] = "C", ["IconPath"] = "/i.png" }, panel);
        Assert.Empty(OrielApp.MacAboutPanelOptions(new AboutPanelOptions()));
    }

    [Fact]
    public void WindowsAboutMessage_TakesTheExecutablesDefaultsUnlessSet()
    {
        Assert.Equal("Desc\r\n1.2\r\n", OrielApp.WindowsAboutMessage(new AboutPanelOptions(), "Desc", "1.2"));
        Assert.Equal("N\r\nV\r\n(c)\r\nthanks\r\n", OrielApp.WindowsAboutMessage(new AboutPanelOptions { ApplicationName = "N", ApplicationVersion = "V", Copyright = "(c)", Credits = "thanks" }, "Desc", "1.2"));
        Assert.Equal("\r\n\r\n", OrielApp.WindowsAboutMessage(new AboutPanelOptions(), null, null));
    }

    [Fact]
    public void AccessibilityTransition_AppliesOnlyAChange()
    {
        Assert.Equal(true, OrielApp.AccessibilityTransition(false, true));
        Assert.Equal(false, OrielApp.AccessibilityTransition(true, false));
        Assert.Null(OrielApp.AccessibilityTransition(true, true));
        Assert.Null(OrielApp.AccessibilityTransition(false, false));
    }

    private sealed class FakeLocalState : ILocalState
    {
        public Dictionary<string, object> Values { get; } = [];
        public string? GetString(string name) => Values.TryGetValue(name, out var v) ? v as string : null;
        public bool GetBoolean(string name) => Values.TryGetValue(name, out var v) && v is true;
        public bool Set(string name, string value) => Values.TryAdd(name, value) || (Values[name] = value) is not null;
        public bool Set(string name, bool value) => Values.TryAdd(name, value) || (Values[name] = value) is not null;
    }

    [Fact]
    public void HostResolver_UnsetOptionsTakeElectronsDefaults()
    {
        var state = new FakeLocalState();
        OrielApp.ApplyHostResolver(state, new HostResolverOptions());
        Assert.Equal(new Dictionary<string, object>
        {
            ["async_dns.enabled"] = true,
            ["net.happy_eyeballs_v3_enabled"] = false,
            ["dns_over_https.mode"] = "off",
            ["dns_over_https.templates"] = "",
            ["async_dns.additional_dns_query_types_enabled"] = true,
        }, state.Values);
    }

    [Fact]
    public void HostResolver_SetOptionsBecomeChromesPreferences()
    {
        var state = new FakeLocalState();
        OrielApp.ApplyHostResolver(state, new HostResolverOptions
        {
            EnableBuiltInResolver = false,
            EnableHappyEyeballs = true,
            SecureDnsMode = SecureDnsMode.Secure,
            SecureDnsServers = ["https://a.example/dns-query", "https://b.example/q{?dns}"],
            EnableAdditionalDnsQueryTypes = false,
        });
        Assert.Equal(false, state.Values["async_dns.enabled"]);
        Assert.Equal(true, state.Values["net.happy_eyeballs_v3_enabled"]);
        Assert.Equal("secure", state.Values["dns_over_https.mode"]);
        Assert.Equal("https://a.example/dns-query https://b.example/q{?dns}", state.Values["dns_over_https.templates"]);
        Assert.Equal(false, state.Values["async_dns.additional_dns_query_types_enabled"]);
        var automatic = new FakeLocalState();
        OrielApp.ApplyHostResolver(automatic, new HostResolverOptions { SecureDnsMode = SecureDnsMode.Automatic });
        Assert.Equal("automatic", automatic.Values["dns_over_https.mode"]);
    }

    [Fact]
    public void HostResolver_AnInvalidTemplateChangesNothing()
    {
        var state = new FakeLocalState();
        Assert.StartsWith("not a valid DoH template: http://plain",
            Assert.Throws<ArgumentException>(() => OrielApp.ApplyHostResolver(state, new HostResolverOptions { SecureDnsServers = ["https://ok", "http://plain"] })).Message);
        Assert.Empty(state.Values);
    }

    [Theory]
    [InlineData("https://dns.example/dns-query", true)]
    [InlineData("https://dns.example/q{?dns}", true)]
    [InlineData("https://dns.example/{dns}", true)]
    [InlineData("http://dns.example/dns-query", false)]
    [InlineData("https://{dns}.example/", false)]
    [InlineData("https://dns.example/{?dns", false)]
    [InlineData("https://dns.example/}", false)]
    [InlineData("not a url", false)]
    public void IsValidDohTemplate_FollowsNet(string template, bool valid)
        => Assert.Equal(valid, OrielApp.IsValidDohTemplate(template));

    [Fact]
    public void ExpandUriTemplate_ExpandsTheDefinedVariableByOperator()
    {
        Assert.Equal("x?dns=v&y", OrielApp.ExpandUriTemplate("x{?dns}&y", "dns", "v"));
        Assert.Equal("x/v", OrielApp.ExpandUriTemplate("x{/dns}", "dns", "v"));
        Assert.Equal("x;dns=v", OrielApp.ExpandUriTemplate("x{;dns}", "dns", "v"));
        Assert.Equal("x#v", OrielApp.ExpandUriTemplate("x{#dns}", "dns", "v"));
        Assert.Equal("x.v", OrielApp.ExpandUriTemplate("x{.dns}", "dns", "v"));
        Assert.Equal("x&dns=v", OrielApp.ExpandUriTemplate("x{&dns}", "dns", "v"));
        Assert.Equal("a%20b", OrielApp.ExpandUriTemplate("{dns}", "dns", "a b"));
        Assert.Equal("a b", OrielApp.ExpandUriTemplate("{+dns}", "dns", "a b"));
        Assert.Equal("x", OrielApp.ExpandUriTemplate("x{?other}", "dns", "v"));
        Assert.Equal("?dns=v", OrielApp.ExpandUriTemplate("{?other,dns}", "dns", "v"));
        Assert.Null(OrielApp.ExpandUriTemplate("{}", "dns", "v"));
        Assert.Null(OrielApp.ExpandUriTemplate("{a{b}", "dns", "v"));
    }

    [Fact]
    public void GpuInfoFromDevTools_IsElectronsShape()
    {
        using var doc = JsonDocument.Parse("""
            {"gpu":{"devices":[{"vendorId":4098,"deviceId":1,"vendorString":"","driverVersion":"9"},{"vendorId":32902,"deviceId":2}],
             "auxAttributes":{"initializationTime":0.25,"optimus":false,"glVendor":"","glRenderer":"R","processCrashCount":3,"visibilityCallbackCallCount":1},
             "featureStatus":{"webgl":"enabled"}},"modelName":"MacBookPro","modelVersion":"","commandLine":"x"}
            """);
        var info = OrielApp.GpuInfoFromDevTools(doc.RootElement);
        Assert.Equal("""{"auxAttributes":{"initializationTime":250,"optimus":false,"glRenderer":"R","visibilityCallbackCallCount":1},"gpuDevice":[{"active":true,"vendorId":4098,"deviceId":1,"driverVersion":"9"},{"active":false,"vendorId":32902,"deviceId":2}],"machineModelName":"MacBookPro"}""",
            info.ToJsonString());
    }

    [Fact]
    public void AdditionalData_RoundTripsThroughTheForwardedCommandLine()
    {
        var data = new JsonObject { ["a"] = 1, ["s"] = "x y/+?" };
        var arg = OrielApp.EncodeAdditionalData(data);
        Assert.StartsWith("--oriel-second-instance-data=", arg);
        Assert.DoesNotContain(' ', arg);
        var (line, decoded) = OrielApp.TakeAdditionalData($"/app/My --flag {arg} file.txt");
        Assert.Equal("/app/My --flag file.txt", line);
        Assert.Equal(data.ToJsonString(), decoded!.ToJsonString());
        Assert.Equal(("/app/My --x", (JsonNode?)null), OrielApp.TakeAdditionalData("/app/My --x"));
    }

    [Fact]
    public void AdditionalData_ThatDoesNotDecodeIsDroppedWithItsSwitch()
    {
        var (line, data) = OrielApp.TakeAdditionalData("/app/My --oriel-second-instance-data=bm90IGpzb24 --x");
        Assert.Equal("/app/My --x", line);
        Assert.Null(data);
    }

    [Fact]
    public void SingletonMessage_IsStartTheDirectoryAndArgvNulSeparated()
        => Assert.Equal(Encoding.UTF8.GetBytes("START\0/cwd\0/app/My\0--x"), OrielApp.SingletonMessage("/cwd", ["/app/My", "--x"]));

    [Fact]
    public void SingletonSocketTarget_NeedsMatchingCookies()
    {
        if (OperatingSystem.IsWindows()) return;
        var profile = Directory.CreateTempSubdirectory("oriel-singleton-").FullName;
        var socketDir = Directory.CreateTempSubdirectory("oriel-sock-").FullName;
        try
        {
            Assert.Null(OrielApp.SingletonSocketTarget(profile));
            var target = Path.Combine(socketDir, "SingletonSocket");
            File.CreateSymbolicLink(Path.Combine(profile, "SingletonSocket"), target);
            Assert.Null(OrielApp.SingletonSocketTarget(profile));
            File.CreateSymbolicLink(Path.Combine(profile, "SingletonCookie"), "123");
            File.CreateSymbolicLink(Path.Combine(socketDir, "SingletonCookie"), "456");
            Assert.Null(OrielApp.SingletonSocketTarget(profile));
            File.Delete(Path.Combine(socketDir, "SingletonCookie"));
            File.CreateSymbolicLink(Path.Combine(socketDir, "SingletonCookie"), "123");
            Assert.Equal(target, OrielApp.SingletonSocketTarget(profile));
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
            Directory.Delete(socketDir, recursive: true);
        }
    }

    [Fact]
    public void NotifySingleton_SendsTheLaunchAndReadsTheAnswer()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(OrielApp.SingletonNotify.Notified, Serve("ACK", out var received));
        Assert.Equal(OrielApp.SingletonMessage("/cwd", ["/app/My", "--x"]), received);
        Assert.Equal(OrielApp.SingletonNotify.ShuttingDown, Serve("SHUTDOWN", out _));
        Assert.Equal(OrielApp.SingletonNotify.None, Serve("??", out _));
        Assert.Equal(OrielApp.SingletonNotify.None, OrielApp.NotifySingleton(Path.Combine(Path.GetTempPath(), "oriel-none-" + Guid.NewGuid()), "/cwd", ["/app/My"]));
    }

    // A one-shot singleton server in a short temp path, as Chromium's socket directory is.
    private static OrielApp.SingletonNotify Serve(string answer, out byte[] received)
    {
        var profile = Directory.CreateTempSubdirectory("oriel-p-").FullName;
        var socketDir = Path.Combine("/tmp", "oriel-s-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(socketDir);
        try
        {
            var socketPath = Path.Combine(socketDir, "SingletonSocket");
            File.CreateSymbolicLink(Path.Combine(profile, "SingletonSocket"), socketPath);
            File.CreateSymbolicLink(Path.Combine(profile, "SingletonCookie"), "c");
            File.CreateSymbolicLink(Path.Combine(socketDir, "SingletonCookie"), "c");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            var got = new MemoryStream();
            var server = Task.Run(() =>
            {
                using var client = listener.Accept();
                var buffer = new byte[256];
                int n;
                while ((n = client.Receive(buffer)) > 0) got.Write(buffer, 0, n);
                client.Send(Encoding.ASCII.GetBytes(answer));
            });
            var result = OrielApp.NotifySingleton(profile, "/cwd", ["/app/My", "--x"]);
            server.Wait(TimeSpan.FromSeconds(10));
            received = got.ToArray();
            return result;
        }
        finally
        {
            Directory.Delete(profile, recursive: true);
            Directory.Delete(socketDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveApplicationLocale_PrefersTheResolvedPreference()
    {
        static bool Ships(string l) => l is "en-US" or "en-GB" or "fr" or "es" or "es-419" or "pt-BR" or "pt-PT" or "zh-TW" or "zh-CN" or "he" or "nb" or "de";
        Assert.Equal("en-US", OrielApp.ResolveApplicationLocale("en-US", [], Ships));
        Assert.Equal("en-GB", OrielApp.ResolveApplicationLocale("en-AU", [], Ships));
        Assert.Equal("es-419", OrielApp.ResolveApplicationLocale("es-MX", [], Ships));
        Assert.Equal("es", OrielApp.ResolveApplicationLocale("es-ES", [], Ships));
        Assert.Equal("pt-PT", OrielApp.ResolveApplicationLocale("pt-AO", [], Ships));
        Assert.Equal("pt-BR", OrielApp.ResolveApplicationLocale("pt", [], Ships));
        Assert.Equal("zh-TW", OrielApp.ResolveApplicationLocale("zh-HK", [], Ships));
        Assert.Equal("zh-CN", OrielApp.ResolveApplicationLocale("zh-SG", [], Ships));
        Assert.Equal("he", OrielApp.ResolveApplicationLocale("iw", [], Ships));
        Assert.Equal("nb", OrielApp.ResolveApplicationLocale("no", [], Ships));
        Assert.Equal("de", OrielApp.ResolveApplicationLocale("de-AT", [], Ships));
        Assert.Equal("en-US", OrielApp.ResolveApplicationLocale("xx", [], Ships));
    }

    [Fact]
    public void ResolveApplicationLocale_TakesAnExactCandidateBeforeAnEarlierMatch()
    {
        static bool Ships(string l) => l is "en-US" or "en-GB" or "fr" or "zh-CN" or "zh-TW";
        Assert.Equal("fr", OrielApp.ResolveApplicationLocale("", ["xx", "fr_CA"], Ships));
        Assert.Equal("en-GB", OrielApp.ResolveApplicationLocale("", ["xx", "yy", "en_gb.utf-8@quot"], Ships));
        Assert.Equal("zh-CN", OrielApp.ResolveApplicationLocale("", ["fr-CA", "zh_CN"], Ships));
        Assert.Equal("en-US", OrielApp.ResolveApplicationLocale("", ["xx", "C"], Ships));
        Assert.Equal("", OrielApp.ResolveApplicationLocale("", ["xx"], _ => false));
    }
}
