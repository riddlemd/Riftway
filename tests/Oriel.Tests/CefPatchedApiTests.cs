using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel.Tests;

public sealed unsafe class CefPatchedApiTests
{
    private const string Stock = "5c8344ae7d7f6421bc801401e35d4dbf6d38e101";
    private const string Patched = "2d137e8cc7d079f5f2b310a17f487df99370c039";

    [Fact]
    public void Classify_NoNextVersionFallsBackTo15400() =>
        Assert.Equal(CefPatchedApi.Kind.Unavailable, CefPatchedApi.Classify(null, Stock, Patched));

    [Fact]
    public void Classify_NextHashedAs15400IsStock() =>
        Assert.Equal(CefPatchedApi.Kind.Stock, CefPatchedApi.Classify(Stock, Stock, Patched));

    [Fact]
    public void Classify_NextHashedAsThePatchesIsPatched() =>
        Assert.Equal(CefPatchedApi.Kind.Patched, CefPatchedApi.Classify(Patched, Stock, Patched));

    [Theory]
    [InlineData("488548509981031433af22af05c9b59e6f0788a9")]
    [InlineData("5C8344AE7D7F6421BC801401E35D4DBF6D38E101")]
    [InlineData("")]
    public void Classify_AnyOtherHashIsUnknown(string next) =>
        Assert.Equal(CefPatchedApi.Kind.Unknown, CefPatchedApi.Classify(next, Stock, Patched));

    [Fact]
    public void PatchedHashes_DifferFromStockOnEveryOs()
    {
        Assert.NotEqual(CefApiVersion.HashLinux, CefPatchedApi.HashLinux);
        Assert.NotEqual(CefApiVersion.HashMac, CefPatchedApi.HashMac);
        Assert.NotEqual(CefApiVersion.HashWindows, CefPatchedApi.HashWindows);
        Assert.Equal(CefApiVersion.HashLinux, Stock);
    }

    [Fact]
    public void NextWindowDelegate_AppendsIsTranslucentAfterTheStockStruct()
    {
        Assert.Equal(sizeof(cef_window_delegate_t) + sizeof(nint), sizeof(cef_window_delegate_next_t));
        cef_window_delegate_next_t value = default;
        var offset = (byte*)&value.is_translucent - (byte*)&value;
        Assert.Equal(sizeof(cef_window_delegate_t), (int)offset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowDelegate_SizesItsStructForTheApiInUse(bool patched)
    {
        var native = new WindowDelegate(patched).Native;
        var expected = patched ? sizeof(cef_window_delegate_next_t) : sizeof(cef_window_delegate_t);

        Assert.Equal((nuint)expected, native->@base.@base.@base.size);

        NativeObject.Release(native);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowDelegate_PatchedAnswersIsTranslucent(bool translucent)
    {
        var native = new WindowDelegate(patched: true) { Translucent = translucent }.Native;
        var next = (cef_window_delegate_next_t*)native;

        Assert.True(next->is_translucent != null);
        Assert.Equal(translucent ? 1 : 0, next->is_translucent(native, null));

        NativeObject.Release(native);
    }

    [Fact]
    public void NextWindow_AppendsSetEventListeningEnabledAfterTheStockStruct()
    {
        Assert.Equal(sizeof(cef_window_t) + sizeof(nint), sizeof(cef_window_next_t));
        cef_window_next_t value = default;
        var offset = (byte*)&value.set_event_listening_enabled - (byte*)&value;
        Assert.Equal(sizeof(cef_window_t), (int)offset);
    }

    private static int s_listeningCalls;
    private static nint s_listeningWindow;
    private static int s_listeningEnabled;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RecordListening(cef_window_t* window, int enabled)
    {
        s_listeningCalls++;
        s_listeningWindow = (nint)window;
        s_listeningEnabled = enabled;
    }

    private static cef_window_next_t* FakeNextWindow()
    {
        var window = (cef_window_next_t*)NativeMemory.AllocZeroed((nuint)sizeof(cef_window_next_t));
        window->set_event_listening_enabled = &RecordListening;
        s_listeningCalls = 0;
        s_listeningWindow = 0;
        s_listeningEnabled = -1;
        return window;
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void SetEventListeningEnabled_PatchedCallsTheAppendedSlot(bool enabled, int expected)
    {
        var window = FakeNextWindow();
        try
        {
            Assert.True(CefPatchedApi.SetEventListeningEnabled(&window->@base, enabled, patched: true));
            Assert.Equal(1, s_listeningCalls);
            Assert.Equal((nint)window, s_listeningWindow);
            Assert.Equal(expected, s_listeningEnabled);
        }
        finally { NativeMemory.Free(window); }
    }

    [Fact]
    public void SetEventListeningEnabled_StockNeverTouchesTheSlot()
    {
        var window = FakeNextWindow();
        try
        {
            Assert.False(CefPatchedApi.SetEventListeningEnabled(&window->@base, false, patched: false));
            Assert.Equal(0, s_listeningCalls);
        }
        finally { NativeMemory.Free(window); }
    }

    [Fact]
    public void SetEventListeningEnabled_NoWindowIsNotTaken() =>
        Assert.False(CefPatchedApi.SetEventListeningEnabled(null, false, patched: true));

    private static int s_tokenCalls;
    private static string? s_token;

    private static void RecordToken(cef_string_utf16_t* token)
    {
        s_tokenCalls++;
        s_token = CefString.Read(token);
    }

    [Theory]
    [InlineData("gnome-shell/Oriel/1234-5-host_TIME42")]
    [InlineData("")]
    public void SetXdgActivationToken_PatchedPassesTheToken(string token)
    {
        s_tokenCalls = 0;
        s_token = null;

        Assert.True(CefPatchedApi.SetXdgActivationToken(token, patched: true, &RecordToken));

        Assert.Equal(1, s_tokenCalls);
        Assert.Equal(token, s_token);
    }

    [Fact]
    public void SetXdgActivationToken_StockMakesNoCall()
    {
        s_tokenCalls = 0;

        Assert.False(CefPatchedApi.SetXdgActivationToken("token", patched: false, &RecordToken));

        Assert.Equal(0, s_tokenCalls);
    }
}
