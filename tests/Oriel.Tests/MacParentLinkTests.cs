namespace Oriel.Tests;

// Electron v44's NativeWindowMac parent links (shell/browser/native_window_mac.mm): InternalSetParentWindow,
// AttachChildren, DetachChildren, RemoveChildFromParentWindow, Show and Hide.
public sealed class MacParentLinkTests
{
    private sealed class FakeWindows : IMacChildWindows
    {
        internal Dictionary<nint, nint> ParentOf { get; } = [];
        internal Dictionary<nint, long> Levels { get; } = [];
        internal List<string> Log { get; } = [];

        public bool IsChildOf(nint child, nint parent) => ParentOf.TryGetValue(child, out var p) && p == parent;

        public void AddChildWindow(nint parent, nint child)
        {
            ParentOf[child] = parent;
            // AppKit resets the parent's level when it adds a child.
            Levels[parent] = 0;
            Log.Add($"add {parent}<-{child}");
        }

        public void RemoveChildWindow(nint parent, nint child)
        {
            if (IsChildOf(child, parent)) ParentOf.Remove(child);
            Log.Add($"remove {parent}<-{child}");
        }

        public long GetLevel(nint window) => Levels.GetValueOrDefault(window);

        public void SetLevel(nint window, long level) => Levels[window] = level;

        public void OrderOut(nint window) => Log.Add($"orderOut {window}");
    }

    private static (FakeWindows Native, Func<nint, bool, MacParentLink> Make) Setup()
    {
        var native = new FakeWindows();
        return (native, (handle, modal) => new MacParentLink(native, () => handle, modal));
    }

    [Fact]
    public void SetParent_Attaching_AddsAVisibleChildAbove_AndKeepsTheParentsLevel()
    {
        var (native, make) = Setup();
        var parent = make(1, false);
        var child = make(2, false);
        child.Show();
        native.Levels[1] = 3;

        child.SetParent(parent, attach: true);

        Assert.Same(parent, child.Parent);
        Assert.Equal([child], parent.Children);
        Assert.True(native.IsChildOf(2, 1));
        Assert.Equal(3, native.Levels[1]);
    }

    [Fact]
    public void SetParent_NotAttaching_OnlyLinks_AndShowAttaches()
    {
        var (native, make) = Setup();
        var parent = make(1, false);
        var child = make(2, false);

        child.SetParent(parent, attach: false);
        Assert.Same(parent, child.Parent);
        Assert.False(native.IsChildOf(2, 1));

        child.Show();
        Assert.True(native.IsChildOf(2, 1));
    }

    [Fact]
    public void SetParent_ToTheParentItIsAttachedTo_DoesNothing()
    {
        var (native, make) = Setup();
        var parent = make(1, false);
        var child = make(2, false);
        child.Show();
        child.SetParent(parent, attach: true);
        native.Log.Clear();

        child.SetParent(parent, attach: true);

        Assert.Empty(native.Log);
    }

    [Fact]
    public void SetParent_Null_RemovesTheChildWindowFromTheOldParent()
    {
        var (native, make) = Setup();
        var parent = make(1, false);
        var child = make(2, false);
        child.Show();
        child.SetParent(parent, attach: true);

        child.SetParent(null, attach: true);

        Assert.Null(child.Parent);
        Assert.Empty(parent.Children);
        Assert.False(native.IsChildOf(2, 1));
    }

    [Fact]
    public void SetParent_ToAnotherParent_MovesTheChildWindow()
    {
        var (native, make) = Setup();
        var first = make(1, false);
        var second = make(3, false);
        var child = make(2, false);
        child.Show();
        child.SetParent(first, attach: true);

        child.SetParent(second, attach: true);

        Assert.Empty(first.Children);
        Assert.Equal([child], second.Children);
        Assert.Equal(["add 1<-2", "remove 1<-2", "add 3<-2"], native.Log);
    }

    [Fact]
    public void SetParent_OnAModalWindow_DoesNothing()
    {
        var (native, make) = Setup();
        var parent = make(1, false);
        var sheet = make(2, true);
        sheet.Show();

        sheet.SetParent(parent, attach: true);

        Assert.Null(sheet.Parent);
        Assert.Empty(native.Log);
    }

    [Fact]
    public void Attaching_SkipsSiblingsThatAreHidden()
    {
        var (native, make) = Setup();
        var parent = make(1, false);
        var hidden = make(2, false);
        var shown = make(3, false);
        hidden.SetParent(parent, attach: false);
        shown.Show();

        shown.SetParent(parent, attach: true);

        Assert.False(native.IsChildOf(2, 1));
        Assert.True(native.IsChildOf(3, 1));
    }

    [Fact]
    public void Attaching_LeavesSiblingsAlreadyAttached()
    {
        var (native, make) = Setup();
        var parent = make(1, false);
        var first = make(2, false);
        var second = make(3, false);
        first.Show();
        second.Show();
        first.SetParent(parent, attach: true);
        native.Log.Clear();

        second.SetParent(parent, attach: true);

        Assert.Equal(["add 1<-3"], native.Log);
    }

    [Fact]
    public void Hide_OrdersChildrenOut_AndDetachesFromTheParent_KeepingTheLink()
    {
        var (native, make) = Setup();
        var parent = make(1, false);
        var window = make(2, false);
        var child = make(3, false);
        window.Show();
        child.Show();
        window.SetParent(parent, attach: true);
        child.SetParent(window, attach: true);
        native.Log.Clear();

        window.Hide();

        Assert.Equal(["orderOut 3", "remove 1<-2"], native.Log);
        Assert.False(window.WantsToBeVisible);
        Assert.Same(parent, window.Parent);
        Assert.False(native.IsChildOf(2, 1));
    }

    [Fact]
    public void Forget_DropsBothDirectionsWithoutAppKitCalls()
    {
        var (native, make) = Setup();
        var parent = make(1, false);
        var window = make(2, false);
        var child = make(3, false);
        window.SetParent(parent, attach: false);
        child.SetParent(window, attach: false);

        window.Forget();

        Assert.Empty(parent.Children);
        Assert.Null(window.Parent);
        Assert.Null(child.Parent);
        Assert.Empty(native.Log);
    }
}
