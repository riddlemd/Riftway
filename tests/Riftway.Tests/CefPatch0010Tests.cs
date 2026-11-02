using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Riftway.Cef;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway.Tests;

// The fakes count references and record calls in static state.
[Collection(nameof(CefPatch0010Tests))]
public sealed unsafe class CefPatch0010Tests
{
    private static readonly Dictionary<nint, int> s_refs = [];
    private static readonly HashSet<(nint, nint)> s_sharing = [];
    private static readonly List<string> s_switches = [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AddRef(cef_base_ref_counted_t* self) => s_refs[(nint)self]++;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Release(cef_base_ref_counted_t* self) => --s_refs[(nint)self] == 0 ? 1 : 0;

    // As CEF's CppToC unwrap: the argument's reference passes to the callee.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int IsSharingWith(cef_request_context_t* self, cef_request_context_t* that)
    {
        s_refs[(nint)that]--;
        return s_sharing.Contains(((nint)self, (nint)that)) ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AppendSwitch(cef_command_line_t* self, cef_string_utf16_t* name) => s_switches.Add(CefString.Read(name));

    private static T* Fake<T>() where T : unmanaged
    {
        var native = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T));
        var counted = (cef_base_ref_counted_t*)native;
        counted->size = (nuint)sizeof(T);
        counted->add_ref = &AddRef;
        counted->release = &Release;
        s_refs[(nint)native] = 1;
        return native;
    }

    private static cef_request_context_t* FakeContext()
    {
        var context = Fake<cef_request_context_t>();
        context->is_sharing_with = &IsSharingWith;
        return context;
    }

    private static cef_command_line_t* FakeCommandLine()
    {
        var line = Fake<cef_command_line_t>();
        line->append_switch = &AppendSwitch;
        return line;
    }

    private static void Reset()
    {
        s_refs.Clear();
        s_sharing.Clear();
        s_switches.Clear();
    }

    [Fact]
    public void NextBrowserProcessHandler_AppendsPatch0010AfterPatch0009()
    {
        cef_browser_process_handler_next_t value = default;
        Assert.Equal(sizeof(cef_browser_process_handler_t) + 2 * sizeof(nint), (int)((byte*)&value.on_before_render_process_launch - (byte*)&value));
    }

    [Fact]
    public void BrowserProcessHandler_PatchedPassesTheRendererLineAndContextAndReleasesBoth()
    {
        Reset();
        var line = FakeCommandLine();
        var context = FakeContext();
        (nint, nint) seen = default;
        var native = new BrowserProcessHandler(patched: true) { BeforeRenderProcessLaunch = (l, c) => seen = ((nint)l, (nint)c) }.Native;
        try
        {
            ((cef_browser_process_handler_next_t*)native)->on_before_render_process_launch(native, line, context);

            Assert.Equal(((nint)line, (nint)context), seen);
            Assert.Equal((0, 0), (s_refs[(nint)line], s_refs[(nint)context]));
        }
        finally
        {
            NativeObject.Release(native);
            NativeMemory.Free(line);
            NativeMemory.Free(context);
        }
    }

    [Fact]
    public void IsUnsandboxed_FindsTheSessionSharingTheContextAndKeepsItsReference()
    {
        Reset();
        var sandboxed = FakeContext();
        var unsandboxed = FakeContext();
        var launched = FakeContext();
        s_sharing.Add(((nint)unsandboxed, (nint)launched));
        try
        {
            Assert.True(Session.IsUnsandboxed(launched, [(nint)sandboxed, (nint)unsandboxed]));
            Assert.False(Session.IsUnsandboxed(launched, [(nint)sandboxed]));
            Assert.False(Session.IsUnsandboxed(launched, []));
            Assert.Equal(1, s_refs[(nint)launched]);
        }
        finally
        {
            NativeMemory.Free(sandboxed);
            NativeMemory.Free(unsandboxed);
            NativeMemory.Free(launched);
        }
    }

    [Theory]
    [InlineData(true, false, true, "no-sandbox")]
    [InlineData(true, true, true, "no-sandbox,no-zygote")]
    [InlineData(false, true, true, "")]
    [InlineData(true, true, false, "")]
    public void OnBeforeRenderProcessLaunch_UnsandboxesOnlyAnUnsandboxedSessionsRenderer(bool sharing, bool linux, bool listed, string expected)
    {
        Reset();
        var session = FakeContext();
        var launched = FakeContext();
        var line = FakeCommandLine();
        if (sharing) s_sharing.Add(((nint)session, (nint)launched));
        try
        {
            Session.OnBeforeRenderProcessLaunch(line, launched, listed ? [(nint)session] : [], linux);

            Assert.Equal(expected, string.Join(",", s_switches));
        }
        finally
        {
            NativeMemory.Free(session);
            NativeMemory.Free(launched);
            NativeMemory.Free(line);
        }
    }

    [Theory]
    [InlineData(true, true, true, false, "scroll-bounce")]
    [InlineData(true, true, false, false, "")]
    [InlineData(true, false, true, false, "")]
    [InlineData(false, true, true, false, "")]
    [InlineData(true, true, true, true, "no-sandbox,scroll-bounce")]
    public void OnBeforeRenderProcessLaunch_AddsScrollBounceForASessionThatAsks_OnMacOnly(bool sharing, bool listed, bool mac, bool unsandboxed, string expected)
    {
        Reset();
        var session = FakeContext();
        var launched = FakeContext();
        var line = FakeCommandLine();
        if (sharing) s_sharing.Add(((nint)session, (nint)launched));
        try
        {
            Session.OnBeforeRenderProcessLaunch(line, launched, unsandboxed ? [(nint)session] : [], listed ? [(nint)session] : [], linux: false, mac);

            Assert.Equal(expected, string.Join(",", s_switches));
        }
        finally
        {
            NativeMemory.Free(session);
            NativeMemory.Free(launched);
            NativeMemory.Free(line);
        }
    }
}

public sealed class SessionSandboxTests
{
    [Fact]
    public void CheckSandboxOption_StockCefCannotUnsandboxOneSessionOfASandboxedApp()
        => Assert.Equal("SessionOptions.Sandbox = false needs Riftway's CEF patch 0010: stock CEF sandboxes every renderer or none (drift W13).",
            Assert.Throws<PlatformNotSupportedException>(() => Session.CheckSandboxOption(sandbox: false, patched: false, appSandboxed: true)).Message);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    public void CheckSandboxOption_AllowsWhatTheLoadedCefCanGive(bool sandbox, bool patched, bool appSandboxed)
        => Session.CheckSandboxOption(sandbox, patched, appSandboxed);

    [Theory]
    [InlineData(null, true, null)]
    [InlineData(null, false, null)]
    [InlineData(true, true, null)]
    [InlineData(false, false, null)]
    [InlineData(false, true, "webPreferences.sandbox false needs a session created with SessionOptions.Sandbox = false: Riftway decides the sandbox per session (drift W13).")]
    [InlineData(true, false, "webPreferences.sandbox true cannot use a session created with SessionOptions.Sandbox = false.")]
    public void SandboxMismatch_OnlyWhenThePageContradictsItsSession(bool? requested, bool sessionSandbox, string? expected)
        => Assert.Equal(expected, Session.SandboxMismatch(requested, sessionSandbox));

    [Fact]
    public void OptionsMismatch_NoOptionsTakeTheExistingSession() => Assert.Null(Session.OptionsMismatch(false, null));

    [Fact]
    public void OptionsMismatch_SameSandboxTakesTheExistingSession() => Assert.Null(Session.OptionsMismatch(false, new SessionOptions { Sandbox = false }));

    [Fact]
    public void OptionsMismatch_OtherSandboxNamesTheExistingOne()
        => Assert.Equal("The session already exists with SessionOptions.Sandbox = true.", Session.OptionsMismatch(true, new SessionOptions { Sandbox = false }));

    [Fact]
    public void SessionOptions_SandboxByDefault() => Assert.True(new SessionOptions().Sandbox);
}
