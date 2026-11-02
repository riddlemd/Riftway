using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Riftway.Tests;

public sealed class AppAuditTests
{
    private static string Dotnet => OperatingSystem.IsWindows() ? @"C:\dotnet\dotnet.exe" : "/usr/local/share/dotnet/dotnet";

    [Fact]
    public void IsPackaged_IsAnyExecutableButTheDotnetHostUnlessForced()
    {
        Assert.False(RiftwayApp.IsPackagedExecutable(Dotnet, forced: false));
        Assert.True(RiftwayApp.IsPackagedExecutable(Dotnet, forced: true));
        Assert.True(RiftwayApp.IsPackagedExecutable("/Applications/My.app/Contents/MacOS/My", forced: false));
        Assert.False(RiftwayApp.IsPackagedExecutable(OperatingSystem.IsWindows() ? @"C:\x\DOTNET.EXE" : "/x/DOTNET", forced: false));
    }

    [Fact]
    public void RelaunchArgv_IsTheOriginalCommandLineByDefault()
    {
        Assert.Equal(["/app/My", "--a", "b"], RiftwayApp.RelaunchArgv("/app/My", ["/app/My.dll", "--a", "b"], null));
        // Under the dotnet host, argv[0] is the entry assembly the host needs again.
        Assert.Equal([Dotnet, "/app/My.dll", "--a"], RiftwayApp.RelaunchArgv(Dotnet, ["/app/My.dll", "--a"], null));
        Assert.Equal(["/app/My", "--a"], RiftwayApp.RelaunchArgv("/app/My", ["/app/My.dll", "--a"], new RelaunchOptions()));
    }

    [Fact]
    public void RelaunchArgv_WithEitherOptionIsTheExecutableAndArgsAlone()
    {
        Assert.Equal(["/app/My", "--x"], RiftwayApp.RelaunchArgv("/app/My", ["/app/My.dll", "--a"], new RelaunchOptions { Args = ["--x"] }));
        Assert.Equal(["/other"], RiftwayApp.RelaunchArgv("/app/My", ["/app/My.dll", "--a"], new RelaunchOptions { ExecPath = "/other" }));
        Assert.Equal(["/other", "1"], RiftwayApp.RelaunchArgv("/app/My", ["/app/My.dll"], new RelaunchOptions { ExecPath = "/other", Args = ["1"] }));
        Assert.Equal(["/app/My"], RiftwayApp.RelaunchArgv("/app/My", ["/app/My.dll", "--a"], new RelaunchOptions { ExecPath = "", Args = [] }));
    }

    [Fact]
    public void ProtocolScheme_IsRfc3986AndPosixUrlsGiveItLowerCased()
    {
        Assert.True(RiftwayApp.IsValidProtocolScheme("my-app+x.1"));
        Assert.False(RiftwayApp.IsValidProtocolScheme("1app"));
        Assert.False(RiftwayApp.IsValidProtocolScheme(""));
        Assert.False(RiftwayApp.IsValidProtocolScheme("a_b"));
        Assert.Equal("myapp", RiftwayApp.PosixSchemeOf("  MyApp://x"));
        Assert.Equal("", RiftwayApp.PosixSchemeOf("no scheme"));
        Assert.Equal("", RiftwayApp.PosixSchemeOf("1x:y"));
    }

    [Fact]
    public void MacFallbackHandler_IsTheFirstOtherAppElseNone()
    {
        Assert.Equal("com.other", RiftwayApp.MacFallbackHandler("com.me", ["com.me", null, "com.other", "com.third"]));
        Assert.Equal("None", RiftwayApp.MacFallbackHandler("com.me", ["com.me", null]));
        // isEqualToString: a case variant of this app counts as another app.
        Assert.Equal("COM.ME", RiftwayApp.MacFallbackHandler("com.me", ["COM.ME"]));
    }

    [Fact]
    public void MacLoginService_NamesTheTypeAndRequiresANameBeyondTheApp()
    {
        Assert.Equal(("mainAppService", ""), RiftwayApp.MacLoginService(LoginItemType.MainAppService, null));
        Assert.Equal(("agentService", "a.plist"), RiftwayApp.MacLoginService(LoginItemType.AgentService, "a.plist"));
        Assert.Equal(("daemonService", "d"), RiftwayApp.MacLoginService(LoginItemType.DaemonService, "d"));
        Assert.Equal(("loginItemService", "l"), RiftwayApp.MacLoginService(LoginItemType.LoginItemService, "l"));
        Assert.StartsWith("'name' is required when type is not mainAppService",
            Assert.Throws<ArgumentException>(() => RiftwayApp.MacLoginService(LoginItemType.AgentService, "")).Message);
    }

    [Fact]
    public void InApplicationsFolder_IsUnderASystemFolderOrAnyApplicationsDirectory()
    {
        string[] dirs = ["/Applications", "/Users/u/Applications"];
        Assert.True(RiftwayApp.InApplicationsFolder("/Applications/My.app", dirs));
        Assert.True(RiftwayApp.InApplicationsFolder("/Users/u/Applications/My.app", dirs));
        Assert.True(RiftwayApp.InApplicationsFolder("/Volumes/Data/Applications/My.app", dirs));
        Assert.False(RiftwayApp.InApplicationsFolder("/Users/u/Downloads/My.app", dirs));
        Assert.False(RiftwayApp.InApplicationsFolder("/Users/u/MyApplications/My.app", dirs));
    }

    [Fact]
    public void MacAboutPanelOptions_AreTheStringOptionsCapitalised()
    {
        var panel = RiftwayApp.MacAboutPanelOptions(new AboutPanelOptions { ApplicationName = "N", Version = "7", Credits = "C", Authors = ["a"], IconPath = "/i.png" });
        Assert.Equal(new Dictionary<string, string> { ["ApplicationName"] = "N", ["Version"] = "7", ["Credits"] = "C", ["IconPath"] = "/i.png" }, panel);
        Assert.Empty(RiftwayApp.MacAboutPanelOptions(new AboutPanelOptions()));
    }

    [Fact]
    public void WindowsAboutMessage_TakesTheExecutablesDefaultsUnlessSet()
    {
        Assert.Equal("Desc\r\n1.2\r\n", RiftwayApp.WindowsAboutMessage(new AboutPanelOptions(), "Desc", "1.2"));
        Assert.Equal("N\r\nV\r\n(c)\r\nthanks\r\n", RiftwayApp.WindowsAboutMessage(new AboutPanelOptions { ApplicationName = "N", ApplicationVersion = "V", Copyright = "(c)", Credits = "thanks" }, "Desc", "1.2"));
        Assert.Equal("\r\n\r\n", RiftwayApp.WindowsAboutMessage(new AboutPanelOptions(), null, null));
    }

    [Fact]
    public void AccessibilityTransition_AppliesOnlyAChange()
    {
        Assert.Equal(true, RiftwayApp.AccessibilityTransition(false, true));
        Assert.Equal(false, RiftwayApp.AccessibilityTransition(true, false));
        Assert.Null(RiftwayApp.AccessibilityTransition(true, true));
        Assert.Null(RiftwayApp.AccessibilityTransition(false, false));
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
        RiftwayApp.ApplyHostResolver(state, new HostResolverOptions());
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
        RiftwayApp.ApplyHostResolver(state, new HostResolverOptions
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
        RiftwayApp.ApplyHostResolver(automatic, new HostResolverOptions { SecureDnsMode = SecureDnsMode.Automatic });
        Assert.Equal("automatic", automatic.Values["dns_over_https.mode"]);
    }

    [Fact]
    public void HostResolver_AnInvalidTemplateChangesNothing()
    {
        var state = new FakeLocalState();
        Assert.StartsWith("not a valid DoH template: http://plain",
            Assert.Throws<ArgumentException>(() => RiftwayApp.ApplyHostResolver(state, new HostResolverOptions { SecureDnsServers = ["https://ok", "http://plain"] })).Message);
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
        => Assert.Equal(valid, RiftwayApp.IsValidDohTemplate(template));

    [Fact]
    public void ExpandUriTemplate_ExpandsTheDefinedVariableByOperator()
    {
        Assert.Equal("x?dns=v&y", RiftwayApp.ExpandUriTemplate("x{?dns}&y", "dns", "v"));
        Assert.Equal("x/v", RiftwayApp.ExpandUriTemplate("x{/dns}", "dns", "v"));
        Assert.Equal("x;dns=v", RiftwayApp.ExpandUriTemplate("x{;dns}", "dns", "v"));
        Assert.Equal("x#v", RiftwayApp.ExpandUriTemplate("x{#dns}", "dns", "v"));
        Assert.Equal("x.v", RiftwayApp.ExpandUriTemplate("x{.dns}", "dns", "v"));
        Assert.Equal("x&dns=v", RiftwayApp.ExpandUriTemplate("x{&dns}", "dns", "v"));
        Assert.Equal("a%20b", RiftwayApp.ExpandUriTemplate("{dns}", "dns", "a b"));
        Assert.Equal("a b", RiftwayApp.ExpandUriTemplate("{+dns}", "dns", "a b"));
        Assert.Equal("x", RiftwayApp.ExpandUriTemplate("x{?other}", "dns", "v"));
        Assert.Equal("?dns=v", RiftwayApp.ExpandUriTemplate("{?other,dns}", "dns", "v"));
        Assert.Null(RiftwayApp.ExpandUriTemplate("{}", "dns", "v"));
        Assert.Null(RiftwayApp.ExpandUriTemplate("{a{b}", "dns", "v"));
    }

    [Fact]
    public void GpuInfoFromChromeGpu_IsElectronsShape()
    {
        using var doc = JsonDocument.Parse("""
            [{"description":"Initialization time","value":"314"},{"description":"In-process GPU","value":false},
             {"description":"GPU0","value":"VENDOR= 0x10de, DEVICE=0x2203 [NVIDIA GeForce RTX 3090 Ti], SUBSYS=0x49833842, REV=161, LUID={0,59666}, DRIVER_VENDOR=NVIDIA, DRIVER_VERSION=32.0.16.1656 *ACTIVE*"},
             {"description":"GPU1","value":"VENDOR= 0x8086, DEVICE=0x4680, LUID={0,64362}, DRIVER_VERSION=31.0"},
             {"description":"Optimus","value":false},{"description":"Desktop compositing","value":"Aero Glass"},
             {"description":"Machine model name","value":"MacBookPro"},{"description":"Machine model version","value":""},
             {"description":"GL_VENDOR","value":""},{"description":"GL_RENDERER","value":"R"},
             {"description":"Reset notification strategy","value":"0x8252"},{"description":"GPU process crash count","value":0}]
            """);
        var info = RiftwayApp.GpuInfoFromChromeGpu(doc.RootElement);
        Assert.Equal("""{"auxAttributes":{"initializationTime":314,"inProcessGpu":false,"optimus":false,"glRenderer":"R","glResetNotificationStrategy":33362},"gpuDevice":[{"vendorId":4318,"deviceId":8707,"revision":161,"subSysId":1233336386,"active":true,"deviceString":"NVIDIA GeForce RTX 3090 Ti","driverVendor":"NVIDIA","driverVersion":"32.0.16.1656"},{"vendorId":32902,"deviceId":18048,"active":false,"driverVersion":"31.0"}],"machineModelName":"MacBookPro"}""",
            info.ToJsonString());
    }

    [Fact]
    public void GpuDeviceFromRow_KeepsCommasInsideNamesAndWrapsTheHighBit()
    {
        var device = RiftwayApp.GpuDeviceFromRow("VENDOR= 0x106b [Google Inc. (Apple)], DEVICE=0x0000 [ANGLE (Apple, ANGLE Metal Renderer: Apple M1, Unspecified Version)], SUBSYS=0x80001028, REV=0, DRIVER_VERSION=1");
        Assert.Equal("""{"vendorId":4203,"deviceId":0,"revision":0,"subSysId":-2147479512,"active":false,"vendorString":"Google Inc. (Apple)","deviceString":"ANGLE (Apple, ANGLE Metal Renderer: Apple M1, Unspecified Version)","driverVersion":"1"}""",
            device.ToJsonString());
    }

    [Fact]
    public void AdditionalData_RoundTripsThroughTheForwardedCommandLine()
    {
        var data = new JsonObject { ["a"] = 1, ["s"] = "x y/+?" };
        var arg = RiftwayApp.EncodeAdditionalData(data);
        Assert.StartsWith("--riftway-second-instance-data=", arg);
        Assert.DoesNotContain(' ', arg);
        var (line, decoded) = RiftwayApp.TakeAdditionalData($"/app/My --flag {arg} file.txt");
        Assert.Equal("/app/My --flag file.txt", line);
        Assert.Equal(data.ToJsonString(), decoded!.ToJsonString());
        Assert.Equal(("/app/My --x", (JsonNode?)null), RiftwayApp.TakeAdditionalData("/app/My --x"));
    }

    [Fact]
    public void AdditionalData_ThatDoesNotDecodeIsDroppedWithItsSwitch()
    {
        var (line, data) = RiftwayApp.TakeAdditionalData("/app/My --riftway-second-instance-data=bm90IGpzb24 --x");
        Assert.Equal("/app/My --x", line);
        Assert.Null(data);
    }

    [Fact]
    public void SingletonMessage_IsStartTheDirectoryAndArgvNulSeparated()
        => Assert.Equal(Encoding.UTF8.GetBytes("START\0/cwd\0/app/My\0--x"), RiftwayApp.SingletonMessage("/cwd", ["/app/My", "--x"]));

    [Fact]
    public void SingletonSocketTarget_NeedsMatchingCookies()
    {
        if (OperatingSystem.IsWindows()) return;
        var profile = Directory.CreateTempSubdirectory("riftway-singleton-").FullName;
        var socketDir = Directory.CreateTempSubdirectory("riftway-sock-").FullName;
        try
        {
            Assert.Null(RiftwayApp.SingletonSocketTarget(profile));
            var target = Path.Combine(socketDir, "SingletonSocket");
            File.CreateSymbolicLink(Path.Combine(profile, "SingletonSocket"), target);
            Assert.Null(RiftwayApp.SingletonSocketTarget(profile));
            File.CreateSymbolicLink(Path.Combine(profile, "SingletonCookie"), "123");
            File.CreateSymbolicLink(Path.Combine(socketDir, "SingletonCookie"), "456");
            Assert.Null(RiftwayApp.SingletonSocketTarget(profile));
            File.Delete(Path.Combine(socketDir, "SingletonCookie"));
            File.CreateSymbolicLink(Path.Combine(socketDir, "SingletonCookie"), "123");
            Assert.Equal(target, RiftwayApp.SingletonSocketTarget(profile));
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
        Assert.Equal(RiftwayApp.SingletonNotify.Notified, Serve("ACK", out var received));
        Assert.Equal(RiftwayApp.SingletonMessage("/cwd", ["/app/My", "--x"]), received);
        Assert.Equal(RiftwayApp.SingletonNotify.ShuttingDown, Serve("SHUTDOWN", out _));
        Assert.Equal(RiftwayApp.SingletonNotify.None, Serve("??", out _));
        Assert.Equal(RiftwayApp.SingletonNotify.None, RiftwayApp.NotifySingleton(Path.Combine(Path.GetTempPath(), "riftway-none-" + Guid.NewGuid()), "/cwd", ["/app/My"]));
    }

    // A one-shot singleton server in a short temp path, as Chromium's socket directory is.
    private static RiftwayApp.SingletonNotify Serve(string answer, out byte[] received)
    {
        var profile = Directory.CreateTempSubdirectory("riftway-p-").FullName;
        var socketDir = Path.Combine("/tmp", "riftway-s-" + Guid.NewGuid().ToString("N")[..8]);
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
            var result = RiftwayApp.NotifySingleton(profile, "/cwd", ["/app/My", "--x"]);
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
        Assert.Equal("en-US", RiftwayApp.ResolveApplicationLocale("en-US", [], Ships));
        Assert.Equal("en-GB", RiftwayApp.ResolveApplicationLocale("en-AU", [], Ships));
        Assert.Equal("es-419", RiftwayApp.ResolveApplicationLocale("es-MX", [], Ships));
        Assert.Equal("es", RiftwayApp.ResolveApplicationLocale("es-ES", [], Ships));
        Assert.Equal("pt-PT", RiftwayApp.ResolveApplicationLocale("pt-AO", [], Ships));
        Assert.Equal("pt-BR", RiftwayApp.ResolveApplicationLocale("pt", [], Ships));
        Assert.Equal("zh-TW", RiftwayApp.ResolveApplicationLocale("zh-HK", [], Ships));
        Assert.Equal("zh-CN", RiftwayApp.ResolveApplicationLocale("zh-SG", [], Ships));
        Assert.Equal("he", RiftwayApp.ResolveApplicationLocale("iw", [], Ships));
        Assert.Equal("nb", RiftwayApp.ResolveApplicationLocale("no", [], Ships));
        Assert.Equal("de", RiftwayApp.ResolveApplicationLocale("de-AT", [], Ships));
        Assert.Equal("en-US", RiftwayApp.ResolveApplicationLocale("xx", [], Ships));
    }

    [Fact]
    public void ResolveApplicationLocale_TakesAnExactCandidateBeforeAnEarlierMatch()
    {
        static bool Ships(string l) => l is "en-US" or "en-GB" or "fr" or "zh-CN" or "zh-TW";
        Assert.Equal("fr", RiftwayApp.ResolveApplicationLocale("", ["xx", "fr_CA"], Ships));
        Assert.Equal("en-GB", RiftwayApp.ResolveApplicationLocale("", ["xx", "yy", "en_gb.utf-8@quot"], Ships));
        Assert.Equal("zh-CN", RiftwayApp.ResolveApplicationLocale("", ["fr-CA", "zh_CN"], Ships));
        Assert.Equal("en-US", RiftwayApp.ResolveApplicationLocale("", ["xx", "C"], Ships));
        Assert.Equal("", RiftwayApp.ResolveApplicationLocale("", ["xx"], _ => false));
    }
}
