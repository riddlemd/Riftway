using Riftway.Cef.Platform;

namespace Riftway.Tests;

// Electron v44's drag_util_mac.mm: DragDownloadItemSource and each item's dragging frame.
public sealed class MacDragTests
{
    [Fact]
    public void OutOfTheAppADragCopies_InsideItCanDoAnything()
    {
        Assert.Equal(MacDragRules.Copy, MacDragRules.SourceOperationMask(0));
        Assert.Equal(MacDragRules.Every, MacDragRules.SourceOperationMask(1));
        Assert.Equal((nuint)1, MacDragRules.Copy);
        Assert.Equal(nuint.MaxValue, MacDragRules.Every);
    }

    [Fact]
    public void TheIconIsCentredOnThePointer()
        => Assert.Equal((90.0, 185.0, 20.0, 30.0), MacDragRules.ItemFrame(100, 200, 20, 30));
}
