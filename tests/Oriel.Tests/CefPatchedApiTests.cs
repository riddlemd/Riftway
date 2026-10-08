using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel.Tests;

public sealed unsafe class CefPatchedApiTests
{
    private const string Stock = "5c8344ae7d7f6421bc801401e35d4dbf6d38e101";
    private const string Patched = "a00027a57b67e129a7aefc08e6028c21fd94021a";

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
}
