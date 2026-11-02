using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Riftway.Cef;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway.Tests;

// Shares static fake state with CefPatches0007To0009AsyncTests, so the two never run at once.
[Collection(nameof(CefPatches0007To0009Tests))]
public sealed unsafe class CefPatches0007To0009Tests
{
    [Fact]
    public void NextBrowserProcessHandler_AppendsPatches0007And0009AfterTheStockStruct()
    {
        Assert.Equal(sizeof(cef_browser_process_handler_t) + 4 * sizeof(nint), sizeof(cef_browser_process_handler_next_t));
        cef_browser_process_handler_next_t value = default;
        Assert.Equal(sizeof(cef_browser_process_handler_t), (int)((byte*)&value.on_child_process_gone - (byte*)&value));
        Assert.Equal(sizeof(cef_browser_process_handler_t) + sizeof(nint), (int)((byte*)&value.on_accessibility_mode_changed - (byte*)&value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrowserProcessHandler_SizesItsStructForTheApiInUse(bool patched)
    {
        var native = new BrowserProcessHandler(patched).Native;
        var expected = patched ? sizeof(cef_browser_process_handler_next_t) : sizeof(cef_browser_process_handler_t);

        Assert.Equal((nuint)expected, native->@base.size);

        NativeObject.Release(native);
    }

    [Fact]
    public void BrowserProcessHandler_PatchedReportsAGoneChildProcess()
    {
        ChildProcessGoneInfo? seen = null;
        var native = new BrowserProcessHandler(patched: true) { ChildProcessGone = info => seen = info }.Native;
        var next = (cef_browser_process_handler_next_t*)native;
        fixed (char* type = "Utility")
        fixed (char* service = "network.mojom.NetworkService")
        fixed (char* name = "Network Service")
        {
            var t = new cef_string_utf16_t { str = type, length = 7 };
            var s = new cef_string_utf16_t { str = service, length = 28 };
            var n = new cef_string_utf16_t { str = name, length = 15 };
            next->on_child_process_gone(native, &t, cef_termination_status_t.TS_PROCESS_CRASHED, 11, &s, &n, 5);
        }

        Assert.Equal(new ChildProcessGoneInfo("Utility", cef_termination_status_t.TS_PROCESS_CRASHED, 11,
            "network.mojom.NetworkService", "Network Service", 5), seen);
        NativeObject.Release(native);
    }

    [Fact]
    public void BrowserProcessHandler_PatchedReportsTheAccessibilityMode()
    {
        var modes = new List<int>();
        var native = new BrowserProcessHandler(patched: true) { AccessibilityModeChanged = modes.Add }.Native;

        ((cef_browser_process_handler_next_t*)native)->on_accessibility_mode_changed(native, 15);

        Assert.Equal([15], modes);
        NativeObject.Release(native);
    }

    [Fact]
    public void ResolveProxyCallback_StructIsBasePlusOneSlot() =>
        Assert.Equal(sizeof(cef_base_ref_counted_t) + sizeof(nint), sizeof(cef_resolve_proxy_callback_t));

    [Fact]
    public void NextRequestContext_AppendsResolveProxyAfterPatch0006()
    {
        cef_request_context_next_t value = default;
        Assert.Equal(sizeof(cef_request_context_t) + 3 * sizeof(nint), (int)((byte*)&value.resolve_proxy - (byte*)&value));
    }

    private static readonly List<string> s_calls = [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FakeResolveProxy(cef_request_context_t* context, cef_string_utf16_t* url, cef_resolve_proxy_callback_t* callback)
    {
        s_calls.Add("context " + CefString.Read(url));
        Complete(callback, cef_errorcode_t.ERR_NONE, "PROXY 127.0.0.1:8080");
    }

    private static void FakeResolveSystemProxy(cef_string_utf16_t* url, cef_resolve_proxy_callback_t* callback)
    {
        s_calls.Add("system " + CefString.Read(url));
        Complete(callback, cef_errorcode_t.ERR_NONE, "DIRECT");
    }

    private static void Complete(cef_resolve_proxy_callback_t* callback, cef_errorcode_t error, string proxy)
    {
        fixed (char* chars = proxy)
        {
            var value = new cef_string_utf16_t { str = chars, length = (nuint)proxy.Length };
            callback->on_resolve_proxy_completed(callback, error, &value);
        }
        NativeObject.Release(callback);
    }

    private static cef_request_context_next_t* FakeContext()
    {
        var context = (cef_request_context_next_t*)NativeMemory.AllocZeroed((nuint)sizeof(cef_request_context_next_t));
        context->resolve_proxy = &FakeResolveProxy;
        s_calls.Clear();
        return context;
    }

    // Await is not allowed in this unsafe class; CefPatches0007To0009AsyncTests awaits the task.
    internal static Task<(cef_errorcode_t, string)> ResolveThroughThePatchedSlot()
    {
        var context = FakeContext();
        var callback = new ResolveProxyCallback();
        try
        {
            Assert.True(CefPatchedApi.ResolveProxy(&context->@base, "http://riftway.test/", callback.Native, patched: true));
            Assert.Equal(["context http://riftway.test/"], s_calls);
            return callback.Task;
        }
        finally { NativeMemory.Free(context); }
    }

    [Fact]
    public void ResolveProxy_StockMakesNoCallAndReleasesTheCallback()
    {
        var context = FakeContext();
        var callback = new ResolveProxyCallback();
        try
        {
            Assert.False(CefPatchedApi.ResolveProxy(&context->@base, "http://riftway.test/", callback.Native, patched: false));
            Assert.Empty(s_calls);
            Assert.True(callback.Task.IsFaulted);
        }
        finally { NativeMemory.Free(context); }
    }

    internal static Task<(cef_errorcode_t, string)> ResolveThroughTheSystemExport()
    {
        s_calls.Clear();
        var callback = new ResolveProxyCallback();

        Assert.True(CefPatchedApi.ResolveSystemProxy("https://riftway.test/", callback.Native, patched: true, &FakeResolveSystemProxy));

        Assert.Equal(["system https://riftway.test/"], s_calls);
        return callback.Task;
    }

    [Fact]
    public void ResolveSystemProxy_StockMakesNoCallAndReleasesTheCallback()
    {
        s_calls.Clear();
        var callback = new ResolveProxyCallback();

        Assert.False(CefPatchedApi.ResolveSystemProxy("https://riftway.test/", callback.Native, patched: false, &FakeResolveSystemProxy));

        Assert.Empty(s_calls);
        Assert.True(callback.Task.IsFaulted);
    }

    private static int s_mode;
    private static int s_modeCalls;

    private static int FakeGetMode()
    {
        s_modeCalls++;
        return 1039;
    }

    private static void FakeSetMode(int mode)
    {
        s_modeCalls++;
        s_mode = mode;
    }

    [Fact]
    public void GetProcessAccessibilityMode_PatchedAsksTheExport()
    {
        s_modeCalls = 0;
        Assert.Equal(1039, CefPatchedApi.GetProcessAccessibilityMode(patched: true, &FakeGetMode));
        Assert.Equal(1, s_modeCalls);
    }

    [Fact]
    public void GetProcessAccessibilityMode_StockIsNullWithoutACall()
    {
        s_modeCalls = 0;
        Assert.Null(CefPatchedApi.GetProcessAccessibilityMode(patched: false, &FakeGetMode));
        Assert.Equal(0, s_modeCalls);
    }

    [Fact]
    public void SetProcessAccessibilityMode_PatchedPassesTheMode()
    {
        s_modeCalls = 0;
        s_mode = -1;
        Assert.True(CefPatchedApi.SetProcessAccessibilityMode(15, patched: true, &FakeSetMode));
        Assert.Equal((1, 15), (s_modeCalls, s_mode));
    }

    [Fact]
    public void SetProcessAccessibilityMode_StockMakesNoCall()
    {
        s_modeCalls = 0;
        Assert.False(CefPatchedApi.SetProcessAccessibilityMode(15, patched: false, &FakeSetMode));
        Assert.Equal(0, s_modeCalls);
    }
}

[Collection(nameof(CefPatches0007To0009Tests))]
public sealed class CefPatches0007To0009AsyncTests
{
    [Fact]
    public async Task ResolveProxy_PatchedPassesTheUrlAndCompletes()
        => Assert.Equal((cef_errorcode_t.ERR_NONE, "PROXY 127.0.0.1:8080"), await CefPatches0007To0009Tests.ResolveThroughThePatchedSlot());

    [Fact]
    public async Task ResolveSystemProxy_PatchedCallsTheExport()
        => Assert.Equal((cef_errorcode_t.ERR_NONE, "DIRECT"), await CefPatches0007To0009Tests.ResolveThroughTheSystemExport());
}

public sealed class ChildProcessGoneTests
{
    [Theory]
    [InlineData(cef_termination_status_t.TS_ABNORMAL_TERMINATION, "abnormal-exit")]
    [InlineData(cef_termination_status_t.TS_PROCESS_WAS_KILLED, "killed")]
    [InlineData(cef_termination_status_t.TS_PROCESS_CRASHED, "crashed")]
    [InlineData(cef_termination_status_t.TS_PROCESS_OOM, "oom")]
    [InlineData(cef_termination_status_t.TS_LAUNCH_FAILED, "launch-failed")]
    [InlineData(cef_termination_status_t.TS_INTEGRITY_FAILURE, "integrity-failure")]
    public void Reason_IsElectrons(cef_termination_status_t status, string reason)
        => Assert.Equal(reason, RiftwayApp.ChildProcessGoneFrom(new ChildProcessGoneInfo("GPU", status, 1, "", "", 0), windows: false).Reason);

    [Fact]
    public void Details_CarryTypeExitCodeServiceAndName()
    {
        var details = RiftwayApp.ChildProcessGoneFrom(
            new ChildProcessGoneInfo("Utility", cef_termination_status_t.TS_PROCESS_CRASHED, -1073741819, "network.mojom.NetworkService", "Network Service", 0),
            windows: false);
        Assert.Equal(new ChildProcessGoneDetails("Utility", "crashed", -1073741819, "network.mojom.NetworkService", "Network Service", null), details);
    }

    [Fact]
    public void Name_IsLeftOutWhenEmpty()
        => Assert.Null(RiftwayApp.ChildProcessGoneFrom(new ChildProcessGoneInfo("GPU", cef_termination_status_t.TS_PROCESS_CRASHED, 1, "", "", 0), false).Name);

    [Theory]
    [InlineData(true, cef_termination_status_t.TS_LAUNCH_FAILED, 5u)]
    [InlineData(false, cef_termination_status_t.TS_LAUNCH_FAILED, null)]
    [InlineData(true, cef_termination_status_t.TS_PROCESS_CRASHED, null)]
    public void SystemErrorCode_OnlyForAWindowsLaunchFailure(bool windows, cef_termination_status_t status, uint? expected)
        => Assert.Equal(expected, RiftwayApp.ChildProcessGoneFrom(new ChildProcessGoneInfo("GPU", status, 1, "", "", 5), windows).SystemErrorCode);
}

public sealed class ProxyPreferenceTests
{
    private static string Describe(ProxyConfig config)
        => string.Join(";", Session.ProxyPreference(config).Select(e => $"{e.Key}={(e.Value is bool b ? (b ? "true" : "false") : e.Value)}"));

    [Fact]
    public void Empty_IsDirect() => Assert.Equal("mode=direct", Describe(new ProxyConfig()));

    [Fact]
    public void RulesWithoutMode_AreFixedServersWithBypass()
        => Assert.Equal("mode=fixed_servers;server=http=foopy:80;bypass_list=<local>",
            Describe(new ProxyConfig { ProxyRules = "http=foopy:80", ProxyBypassRules = "<local>" }));

    [Fact]
    public void EmptyBypass_IsLeftOut()
        => Assert.Equal("mode=fixed_servers;server=foopy:80", Describe(new ProxyConfig { ProxyRules = "foopy:80", ProxyBypassRules = "" }));

    [Fact]
    public void PacWithoutMode_TakesPrecedenceAndIsMandatory()
        => Assert.Equal("mode=pac_script;pac_url=http://wpad/proxy.pac;pac_mandatory=true",
            Describe(new ProxyConfig { PacScript = "http://wpad/proxy.pac", ProxyRules = "foopy:80" }));

    [Fact]
    public void EmptyPacWithoutMode_FallsBackToTheRules()
        => Assert.Equal("mode=fixed_servers;server=foopy:80", Describe(new ProxyConfig { PacScript = "", ProxyRules = "foopy:80" }));

    [Fact]
    public void FixedServersWithoutRules_IsDirect()
        => Assert.Equal("mode=direct", Describe(new ProxyConfig { Mode = ProxyMode.FixedServers, ProxyBypassRules = "<local>" }));

    [Fact]
    public void PacModeWithoutUrl_HasNoUrl()
        => Assert.Equal("mode=pac_script", Describe(new ProxyConfig { Mode = ProxyMode.PacScript }));

    [Theory]
    [InlineData(ProxyMode.Direct, "mode=direct")]
    [InlineData(ProxyMode.System, "mode=system")]
    [InlineData(ProxyMode.AutoDetect, "mode=auto_detect")]
    public void OtherModes_IgnoreTheRest(ProxyMode mode, string expected)
        => Assert.Equal(expected, Describe(new ProxyConfig { Mode = mode, PacScript = "http://wpad/p.pac", ProxyRules = "foopy:80" }));
}

public sealed class AccessibilityModesTests
{
    [Fact]
    public void Names_FollowElectronsOrder()
        => Assert.Equal(["nativeAPIs", "webContents", "inlineTextBoxes", "extendedProperties", "html", "labelImages", "pdfPrinting", "screenReader"],
            AccessibilityModes.Names(1 | 2 | 4 | 8 | 16 | 32 | 64 | 128 | 256 | 512 | 1024 | 2048));

    [Fact]
    public void Names_OfNoModeAreEmpty() => Assert.Empty(AccessibilityModes.Names(0));

    [Fact]
    public void Parse_IsTheInverseOfNames()
        => Assert.Equal(1 | 4 | 16 | 64 | 128 | 1024,
            AccessibilityModes.Parse(["nativeAPIs", "inlineTextBoxes", "html", "labelImages", "pdfPrinting", "screenReader"]));

    [Fact]
    public void Parse_RejectsAnUnknownName()
        => Assert.Equal("Unknown accessibility feature: htmlMetadata (Parameter 'features')",
            Assert.Throws<ArgumentException>(() => AccessibilityModes.Parse(["webContents", "htmlMetadata"])).Message);

    [Theory]
    [InlineData(15, true)]
    [InlineData(15 | 1024, true)]
    [InlineData(7, false)]
    [InlineData(14, false)]
    [InlineData(0, false)]
    public void IsComplete_NeedsAllFourFlags(int mode, bool expected) => Assert.Equal(expected, AccessibilityModes.IsComplete(mode));

    [Theory]
    [InlineData(0, true, 15)]
    [InlineData(1024, true, 1024)]
    [InlineData(15, false, 0)]
    [InlineData(0, false, 0)]
    public void ScopedAfterEnable_IsElectrons(int current, bool enabled, int expected)
        => Assert.Equal(expected, AccessibilityModes.ScopedAfterEnable(current, enabled));
}
