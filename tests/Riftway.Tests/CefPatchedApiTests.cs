using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Riftway.Cef;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway.Tests;

public sealed unsafe class CefPatchedApiTests
{
    private const string Stock = "5c8344ae7d7f6421bc801401e35d4dbf6d38e101";
    private const string Patched = "41328e768ef24db112c839e118ea0783d3d8543a";

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
    [InlineData("gnome-shell/Riftway/1234-5-host_TIME42")]
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

    // Fake userfree strings: allocated here, read and freed by ReadTestString in place of cef_string_userfree_free.
    private static cef_string_utf16_t* TestString(string? value)
    {
        if (value is null) return null;
        var native = (cef_string_utf16_t*)NativeMemory.AllocZeroed((nuint)sizeof(cef_string_utf16_t));
        native->str = (char*)NativeMemory.Alloc((nuint)(Math.Max(1, value.Length) * sizeof(char)));
        value.AsSpan().CopyTo(new Span<char>(native->str, value.Length));
        native->length = (nuint)value.Length;
        return native;
    }

    private static string ReadTestString(cef_string_utf16_t* value)
    {
        s_stringsRead++;
        if (value == null) return "";
        var result = CefString.Read(value);
        NativeMemory.Free(value->str);
        NativeMemory.Free(value);
        return result;
    }

    private static int s_stringsRead;
    private static int s_downloadCalls;
    private static string? s_frame;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeGesture(cef_download_item_t* item) { s_downloadCalls++; return 1; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_string_utf16_t* FakeLastModified(cef_download_item_t* item) { s_downloadCalls++; return TestString("Wed, 21 Oct 2015 07:28:00 GMT"); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_string_utf16_t* FakeETag(cef_download_item_t* item) { s_downloadCalls++; return TestString("\"33a64df5\""); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_string_utf16_t* FakeInitiator(cef_download_item_t* item) { s_downloadCalls++; return TestString("https://riftway.test"); }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static cef_string_utf16_t* FakeFrame(cef_download_item_t* item) { s_downloadCalls++; return TestString(s_frame); }

    private static cef_download_item_next_t* FakeNextDownloadItem(string? frame)
    {
        var item = (cef_download_item_next_t*)NativeMemory.AllocZeroed((nuint)sizeof(cef_download_item_next_t));
        item->has_user_gesture = &FakeGesture;
        item->get_last_modified_time = &FakeLastModified;
        item->get_etag = &FakeETag;
        item->get_request_initiator = &FakeInitiator;
        item->get_frame_identifier = &FakeFrame;
        s_frame = frame;
        s_downloadCalls = 0;
        s_stringsRead = 0;
        return item;
    }

    [Fact]
    public void NextDownloadItem_AppendsPatch0005AfterTheStockStruct()
    {
        Assert.Equal(sizeof(cef_download_item_t) + 5 * sizeof(nint), sizeof(cef_download_item_next_t));
        cef_download_item_next_t value = default;
        Assert.Equal(sizeof(cef_download_item_t), (int)((byte*)&value.has_user_gesture - (byte*)&value));
        Assert.Equal(sizeof(cef_download_item_t) + 4 * sizeof(nint), (int)((byte*)&value.get_frame_identifier - (byte*)&value));
    }

    [Fact]
    public void ReadDownloadExtras_PatchedReadsEverySlot()
    {
        var item = FakeNextDownloadItem("A-0123456789ABCDEF0123456789ABCDEF");
        try
        {
            var extras = CefPatchedApi.ReadDownloadExtras(&item->@base, patched: true, &ReadTestString);

            Assert.Equal(new DownloadItemExtras(true, "Wed, 21 Oct 2015 07:28:00 GMT", "\"33a64df5\"", "https://riftway.test",
                "A-0123456789ABCDEF0123456789ABCDEF"), extras);
            Assert.Equal(5, s_downloadCalls);
            Assert.Equal(4, s_stringsRead);
        }
        finally { NativeMemory.Free(item); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ReadDownloadExtras_NoFrameIsNull(string? frame)
    {
        var item = FakeNextDownloadItem(frame);
        try
        {
            Assert.Null(CefPatchedApi.ReadDownloadExtras(&item->@base, patched: true, &ReadTestString)!.FrameIdentifier);
        }
        finally { NativeMemory.Free(item); }
    }

    [Fact]
    public void ReadDownloadExtras_StockReadsNothing()
    {
        var item = FakeNextDownloadItem("A-0123456789ABCDEF0123456789ABCDEF");
        try
        {
            Assert.Null(CefPatchedApi.ReadDownloadExtras(&item->@base, patched: false, &ReadTestString));
            Assert.Equal(0, s_downloadCalls);
        }
        finally { NativeMemory.Free(item); }
    }

    [Fact]
    public void NextRequestContext_AppendsPatch0006AfterTheStockStruct()
    {
        Assert.Equal(sizeof(cef_request_context_t) + 4 * sizeof(nint), sizeof(cef_request_context_next_t));
        cef_request_context_next_t value = default;
        Assert.Equal(sizeof(cef_request_context_t), (int)((byte*)&value.start_net_log - (byte*)&value));
        Assert.Equal(sizeof(cef_request_context_t) + 2 * sizeof(nint), (int)((byte*)&value.is_net_logging - (byte*)&value));
        Assert.Equal(sizeof(cef_base_ref_counted_t) + sizeof(nint), sizeof(cef_net_log_callback_t));
    }

    private static readonly List<string> s_netLogCalls = [];
    private static int s_netLogging;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FakeStartNetLog(cef_request_context_t* context, cef_string_utf16_t* path, int mode, long maxFileSize,
        cef_net_log_callback_t* callback)
    {
        s_netLogCalls.Add($"start {CefString.Read(path)} {mode} {maxFileSize}");
        NativeObject.Release(callback);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FakeStopNetLog(cef_request_context_t* context, cef_net_log_callback_t* callback)
    {
        s_netLogCalls.Add("stop");
        var error = default(cef_string_utf16_t);
        callback->on_net_log_complete(callback, &error);
        NativeObject.Release(callback);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeIsNetLogging(cef_request_context_t* context)
    {
        s_netLogCalls.Add("is");
        return s_netLogging;
    }

    private static cef_request_context_next_t* FakeNextContext()
    {
        var context = (cef_request_context_next_t*)NativeMemory.AllocZeroed((nuint)sizeof(cef_request_context_next_t));
        context->start_net_log = &FakeStartNetLog;
        context->stop_net_log = &FakeStopNetLog;
        context->is_net_logging = &FakeIsNetLogging;
        s_netLogCalls.Clear();
        return context;
    }

    [Fact]
    public void StartNetLog_PatchedPassesPathModeSizeAndTheCallback()
    {
        var context = FakeNextContext();
        var callback = new NetLogCallback();
        try
        {
            Assert.True(CefPatchedApi.StartNetLog(&context->@base, "/tmp/net.json", 2, -1, callback.Native, patched: true));
            Assert.Equal(["start /tmp/net.json 2 -1"], s_netLogCalls);
            // The fake released the reference it was given, as libcef would once done.
            Assert.True(callback.Task.IsFaulted);
        }
        finally { NativeMemory.Free(context); }
    }

    [Fact]
    public void StartNetLog_StockMakesNoCallAndReleasesTheCallback()
    {
        var context = FakeNextContext();
        var callback = new NetLogCallback();
        try
        {
            Assert.False(CefPatchedApi.StartNetLog(&context->@base, "/tmp/net.json", 0, 10, callback.Native, patched: false));
            Assert.Empty(s_netLogCalls);
            Assert.True(callback.Task.IsFaulted);
        }
        finally { NativeMemory.Free(context); }
    }

    // Await is not allowed in this unsafe class; CefPatchedApiAsyncTests awaits the task.
    internal static Task<string> StopThroughThePatchedSlot()
    {
        var context = FakeNextContext();
        var callback = new NetLogCallback();
        try
        {
            Assert.True(CefPatchedApi.StopNetLog(&context->@base, callback.Native, patched: true));
            Assert.Equal(["stop"], s_netLogCalls);
            return callback.Task;
        }
        finally { NativeMemory.Free(context); }
    }

    [Fact]
    public void StopNetLog_StockMakesNoCallAndReleasesTheCallback()
    {
        var context = FakeNextContext();
        var callback = new NetLogCallback();
        try
        {
            Assert.False(CefPatchedApi.StopNetLog(&context->@base, callback.Native, patched: false));
            Assert.Empty(s_netLogCalls);
            Assert.True(callback.Task.IsFaulted);
        }
        finally { NativeMemory.Free(context); }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void IsNetLogging_PatchedAsksTheSlot(int answer, bool expected)
    {
        var context = FakeNextContext();
        s_netLogging = answer;
        try
        {
            Assert.Equal(expected, CefPatchedApi.IsNetLogging(&context->@base, patched: true));
            Assert.Equal(["is"], s_netLogCalls);
        }
        finally { NativeMemory.Free(context); }
    }

    [Fact]
    public void IsNetLogging_StockMakesNoCall()
    {
        var context = FakeNextContext();
        s_netLogging = 1;
        try
        {
            Assert.False(CefPatchedApi.IsNetLogging(&context->@base, patched: false));
            Assert.Empty(s_netLogCalls);
        }
        finally { NativeMemory.Free(context); }
    }

    internal static Task<string> CompleteWith(string error)
    {
        var callback = new NetLogCallback();
        fixed (char* chars = error)
        {
            var value = new cef_string_utf16_t { str = chars, length = (nuint)error.Length };
            callback.Native->on_net_log_complete(callback.Native, &value);
        }
        NativeObject.Release(callback.Native);
        return callback.Task;
    }
}

public sealed class CefPatchedApiAsyncTests
{
    [Fact]
    public async Task StopNetLog_PatchedCallsTheSlotAndCompletes() =>
        Assert.Equal("", await CefPatchedApiTests.StopThroughThePatchedSlot());

    [Fact]
    public async Task NetLogCallback_CompletesWithTheErrorText() =>
        Assert.Equal("net::ERR_FAILED", await CefPatchedApiTests.CompleteWith("net::ERR_FAILED"));
}
