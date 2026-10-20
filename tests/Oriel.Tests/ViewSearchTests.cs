using Oriel.Cef.Platform;

namespace Oriel.Tests;

// Finding Chromium's RenderWidgetHostViewCocoa under the view CEF hands out.
public sealed class ViewSearchTests
{
    // 1 -> [2 -> [4], 3 -> [5]].
    private static readonly Dictionary<nint, nint[]> Tree = new() { [1] = [2, 3], [2] = [4], [3] = [5], [4] = [], [5] = [] };

    private static nint Find(nint root, Func<nint, bool> match) => ViewSearch.First(root, v => Tree[v], match);

    [Fact]
    public void TheRootItselfCanMatch() => Assert.Equal(1, Find(1, _ => true));

    [Fact]
    public void TheSearchIsDepthFirst() => Assert.Equal(4, Find(1, v => v is 4 or 3));

    [Fact]
    public void ADeeperViewUnderALaterChildIsFound() => Assert.Equal(5, Find(1, v => v == 5));

    [Fact]
    public void NoMatchOrNoRootIsZero()
    {
        Assert.Equal(0, Find(1, _ => false));
        // No root, no calls: nothing is asked of a nil view.
        Assert.Equal(0, ViewSearch.First(0, _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException()));
    }
}
