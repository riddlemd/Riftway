using Riftway.Cef;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway.Tests;

[Collection(nameof(CefPatch0013Tests))]
public sealed unsafe class CefPatch0013Tests
{
    [Fact]
    public void NextBrowserProcessHandler_AppendsPatch0013AfterPatch0010()
    {
        cef_browser_process_handler_next_t value = default;
        Assert.Equal(sizeof(cef_browser_process_handler_t) + 3 * sizeof(nint), (int)((byte*)&value.on_gpu_info_update - (byte*)&value));
    }

    [Fact]
    public void BrowserProcessHandler_PatchedReportsGpuInfoUpdates()
    {
        var updates = 0;
        var native = new BrowserProcessHandler(patched: true) { GpuInfoUpdate = () => updates++ }.Native;

        ((cef_browser_process_handler_next_t*)native)->on_gpu_info_update(native);

        Assert.Equal(1, updates);
        NativeObject.Release(native);
    }

    [Fact]
    public void GpuInfoCallback_StructIsBasePlusOneSlot() =>
        Assert.Equal(sizeof(cef_base_ref_counted_t) + sizeof(nint), sizeof(cef_gpu_info_callback_t));

    private static nint s_converted;

    private static string FakeToJson(cef_dictionary_value_t* dictionary)
    {
        s_converted = (nint)dictionary;
        return """{"gpuDevice":[]}""";
    }

    // Await is not allowed in this unsafe class; CefPatch0013AsyncTests awaits the task.
    internal static Task<string> CompleteWithInfo()
    {
        s_converted = 0;
        var info = (cef_dictionary_value_t*)0x1234;
        var callback = new GpuInfoCallback(&FakeToJson);

        callback.Native->on_gpu_info(callback.Native, info, null);

        Assert.Equal((nint)info, s_converted);
        NativeObject.Release(callback.Native);
        return callback.Task;
    }

    [Fact]
    public void GpuInfoCallback_FailsWithCefsErrorWithoutInfo()
    {
        s_converted = 0;
        var callback = new GpuInfoCallback(&FakeToJson);
        const string Error = "GPU access not allowed. Reason: GPU access is disabled through commandline switch --disable-gpu.";

        fixed (char* chars = Error)
        {
            var value = new cef_string_utf16_t { str = chars, length = (nuint)Error.Length };
            callback.Native->on_gpu_info(callback.Native, null, &value);
        }

        Assert.Equal(0, s_converted);
        Assert.True(callback.Task.IsFaulted);
        Assert.Equal(Error, callback.Task.Exception!.InnerException!.Message);
        NativeObject.Release(callback.Native);
    }

    private static readonly List<(int Complete, nint Callback)> s_requests = [];

    private static void FakeGetGpuInfo(int complete, cef_gpu_info_callback_t* callback)
    {
        s_requests.Add((complete, (nint)callback));
        NativeObject.Release(callback);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void GetGpuInfo_PatchedPassesTheTypeAndTheCallback(bool complete, int expected)
    {
        s_requests.Clear();
        var callback = new GpuInfoCallback(&FakeToJson);
        var native = (nint)callback.Native;

        Assert.True(CefPatchedApi.GetGpuInfo(complete, callback.Native, patched: true, &FakeGetGpuInfo));

        Assert.Equal([(expected, native)], s_requests);
    }

    [Fact]
    public void GetGpuInfo_StockMakesNoCallAndReleasesTheCallback()
    {
        s_requests.Clear();
        var callback = new GpuInfoCallback(&FakeToJson);

        Assert.False(CefPatchedApi.GetGpuInfo(true, callback.Native, patched: false, &FakeGetGpuInfo));

        Assert.Empty(s_requests);
        Assert.True(callback.Task.IsFaulted);
    }

    private static int s_accelerationCalls;
    private static int s_acceleration;

    private static int FakeIsHardwareAccelerationEnabled()
    {
        s_accelerationCalls++;
        return s_acceleration;
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void IsHardwareAccelerationEnabled_PatchedAsksTheExport(int answer, bool expected)
    {
        s_accelerationCalls = 0;
        s_acceleration = answer;
        Assert.Equal(expected, CefPatchedApi.IsHardwareAccelerationEnabled(patched: true, &FakeIsHardwareAccelerationEnabled));
        Assert.Equal(1, s_accelerationCalls);
    }

    [Fact]
    public void IsHardwareAccelerationEnabled_StockIsNullWithoutACall()
    {
        s_accelerationCalls = 0;
        Assert.Null(CefPatchedApi.IsHardwareAccelerationEnabled(patched: false, &FakeIsHardwareAccelerationEnabled));
        Assert.Equal(0, s_accelerationCalls);
    }

    [Fact]
    public void DisableDomainBlockingFor3DAPIs_BeforeReadyAddsTheSwitch()
    {
        var line = new CommandLine([]);

        RiftwayApp.DisableDomainBlockingFor3DAPIs(ready: false, line);

        Assert.True(line.HasSwitch("disable-domain-blocking-for-3d-apis"));
    }

    [Fact]
    public void DisableDomainBlockingFor3DAPIs_OnceReadyThrowsElectronsErrorAndAddsNothing()
    {
        var line = new CommandLine([]);

        var e = Assert.Throws<InvalidOperationException>(() => RiftwayApp.DisableDomainBlockingFor3DAPIs(ready: true, line));

        Assert.Equal("app.disableDomainBlockingFor3DAPIs() can only be called before app is ready", e.Message);
        Assert.False(line.HasSwitch("disable-domain-blocking-for-3d-apis"));
    }
}

[Collection(nameof(CefPatch0013Tests))]
public sealed class CefPatch0013AsyncTests
{
    [Fact]
    public async Task GpuInfoCallback_CompletesWithTheInfoAsJson()
        => Assert.Equal("""{"gpuDevice":[]}""", await CefPatch0013Tests.CompleteWithInfo());
}
