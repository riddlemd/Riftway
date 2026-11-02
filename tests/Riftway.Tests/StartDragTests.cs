using System.Buffers.Binary;
using System.Text;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class StartDragTests
{
    private static NativeImage Icon() => NativeImage.CreateFromBitmap(new byte[4 * 4 * 4], new NativeImageBitmapOptions { Width = 4, Height = 4 });

    private static NativeImage NoLoad(string path) => throw new InvalidOperationException("the loader must not run");

    [Fact]
    public void DropFiles_IsDropFilesHeaderThenWideNulTerminatedNames()
    {
        var bytes = WindowsDrag.DropFiles([@"C:\a b.txt", "D:\\é"]);

        Assert.Equal(20 + (10 + 1) * 2 + (4 + 1) * 2 + 2, bytes.Length);
        Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(bytes)); // pFiles
        Assert.Equal(0, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(4))); // pt
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12))); // fNC
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16))); // fWide
        Assert.Equal("C:\\a b.txt\0D:\\é\0\0", Encoding.Unicode.GetString(bytes, 20, bytes.Length - 20));
    }

    [Fact]
    public void DropFiles_OfOneEmptyPath_IsHeaderAndTwoNuls()
    {
        var bytes = WindowsDrag.DropFiles([""]);

        Assert.Equal(24, bytes.Length);
        Assert.All(bytes.AsSpan(20).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Layout_LongTitle_ClampsTo150AndCentresTheCursor()
        => Assert.Equal(new WindowsDragLayout(150, 42, 6, 5, 50, 94, 75, 21), WindowsDrag.Layout(32, 32, 500, 16));

    [Fact]
    public void Layout_ShortTitle_IsInsetsIconSpacingAndText()
        => Assert.Equal(new WindowsDragLayout(80, 26, 6, 5, 34, 40, 40, 13), WindowsDrag.Layout(16, 16, 40, 16));

    [Fact]
    public void Layout_ExactlyMaxWidth_IsNotClamped()
        => Assert.Equal(new WindowsDragLayout(150, 42, 6, 5, 50, 94, 75, 21), WindowsDrag.Layout(32, 32, 94, 16));

    [Fact]
    public void Layout_TallerText_SetsTheHeightAndCentresTheIcon()
        => Assert.Equal(new WindowsDragLayout(80, 30, 6, 11, 34, 40, 40, 15), WindowsDrag.Layout(16, 8, 40, 20));

    [Fact]
    public void Layout_NoIcon_PutsTheTextAtTheInsetWithoutSpacing()
        => Assert.Equal(new WindowsDragLayout(52, 26, 6, 13, 6, 40, 26, 13), WindowsDrag.Layout(0, 0, 40, 16));

    [Fact]
    public void Layout_NoText_LeavesOutTheSpacing()
        => Assert.Equal(44, WindowsDrag.Layout(32, 32, 0, 16).Width);

    [Fact]
    public void Resolve_FilesWinOverFile()
        => Assert.Equal(["a", "b"], WebContents.ResolveDragItem(new StartDragItem { Files = ["a", "b"], File = "c", Icon = Icon() }, NoLoad)!.Value.Files);

    [Fact]
    public void Resolve_FileAlone_IsOneFile()
        => Assert.Equal(["c"], WebContents.ResolveDragItem(new StartDragItem { File = "c", Icon = Icon() }, NoLoad)!.Value.Files);

    [Fact]
    public void Resolve_EmptyFiles_IgnoresFileAndThrows()
        => Assert.Equal("Must specify either 'file' or 'files' option",
            Assert.Throws<ArgumentException>(() => WebContents.ResolveDragItem(new StartDragItem { Files = [], File = "c", Icon = Icon() }, NoLoad)).Message);

    [Fact]
    public void Resolve_NoIcon_ThrowsBeforeCheckingFiles()
        => Assert.Equal("'icon' parameter is required",
            Assert.Throws<ArgumentException>(() => WebContents.ResolveDragItem(new StartDragItem(), NoLoad)).Message);

    [Fact]
    public void Resolve_EmptyIcon_ReturnsNullBeforeCheckingFiles()
        => Assert.Null(WebContents.ResolveDragItem(new StartDragItem { Icon = NativeImage.CreateEmpty() }, NoLoad));

    [Fact]
    public void Resolve_EmptyIconWithFiles_ReturnsNull()
        => Assert.Null(WebContents.ResolveDragItem(new StartDragItem { File = "c", Icon = NativeImage.CreateEmpty() }, NoLoad));

    [Fact]
    public void Resolve_IconPath_IsLoaded()
    {
        var icon = Icon();
        string? asked = null;
        var resolved = WebContents.ResolveDragItem(new StartDragItem { File = "c", IconPath = "i.png" }, p => { asked = p; return icon; });
        Assert.Equal("i.png", asked);
        Assert.Same(icon, resolved!.Value.Icon);
    }

    [Fact]
    public void Resolve_IconPathThatDoesNotLoad_ThrowsElectronsMessage()
        => Assert.Equal("Failed to load image from path 'i.png'",
            Assert.Throws<ArgumentException>(() => WebContents.ResolveDragItem(new StartDragItem { File = "c", IconPath = "i.png" }, _ => NativeImage.CreateEmpty())).Message);

    [Fact]
    public void Resolve_IconWinsOverIconPath()
    {
        var icon = Icon();
        Assert.Same(icon, WebContents.ResolveDragItem(new StartDragItem { File = "c", Icon = icon, IconPath = "i.png" }, NoLoad)!.Value.Icon);
    }
}
