namespace Oriel.Tests;

public sealed class WindowSizeStateTests
{
    private const int Restored = 0, Minimized = 1, Maximized = 2, MaxShow = 3, MaxHide = 4;

    private static List<WindowSizeEvent> Run(WindowSizeState state, params int[] sizes)
    {
        var events = new List<WindowSizeEvent>();
        foreach (var size in sizes) events.AddRange(state.OnSize(size, isFullScreen: false));
        return events;
    }

    [Fact]
    public void MaximizeThenRestore_RaisesMaximizeThenUnmaximize()
    {
        Assert.Equal([WindowSizeEvent.Maximize, WindowSizeEvent.Unmaximize], Run(new WindowSizeState(), Maximized, Restored));
    }

    [Fact]
    public void MinimizeThenRestore_RaisesMinimizeThenRestore()
    {
        Assert.Equal([WindowSizeEvent.Minimize, WindowSizeEvent.Restore], Run(new WindowSizeState(), Minimized, Restored));
    }

    [Fact]
    public void RepeatedSizeMessages_RaiseOnce()
    {
        Assert.Equal([WindowSizeEvent.Maximize, WindowSizeEvent.Minimize], Run(new WindowSizeState(), Maximized, Maximized, Minimized, Minimized));
        Assert.Equal([WindowSizeEvent.Maximize, WindowSizeEvent.Unmaximize], Run(new WindowSizeState(), Maximized, Restored, Restored));
    }

    [Fact]
    public void RestoredWhileNormal_RaisesNothing()
    {
        Assert.Empty(Run(new WindowSizeState(), Restored, MaxShow, MaxHide));
    }

    [Fact]
    public void MinimizedToMaximized_RaisesRestoreThenMaximize()
    {
        // A maximized window minimized and brought back gets SIZE_MAXIMIZED, and Electron raises maximize again.
        Assert.Equal([WindowSizeEvent.Maximize, WindowSizeEvent.Minimize, WindowSizeEvent.Restore, WindowSizeEvent.Maximize],
            Run(new WindowSizeState(), Maximized, Minimized, Maximized));
    }

    [Fact]
    public void RestoredFromMinimizedWhileFullScreen_RaisesEnterFullScreen()
    {
        var state = new WindowSizeState();
        Assert.Equal([WindowSizeEvent.Minimize], state.OnSize(Minimized, isFullScreen: true));
        Assert.Equal([WindowSizeEvent.EnterFullScreen], state.OnSize(Restored, isFullScreen: true));
        Assert.Empty(state.OnSize(Restored, isFullScreen: true));
    }

    [Fact]
    public void FullScreenState_SwallowsRestored_AsElectronsLastWindowState()
    {
        var maximized = new WindowSizeState();
        Run(maximized, Maximized);
        maximized.SetFullScreen(true);
        Assert.Empty(Run(maximized, Restored));
        Assert.Equal([WindowSizeEvent.Maximize], Run(maximized, Maximized));
        // SetFullScreen(false) resets the state even from maximized, so the next restore is silent, as in Electron.
        maximized.SetFullScreen(false);
        Assert.Empty(Run(maximized, Restored));
    }

    [Fact]
    public void FullScreenFromMinimized_SwallowsTheRestore()
    {
        var state = new WindowSizeState();
        Run(state, Minimized);
        state.SetFullScreen(true);
        Assert.Empty(Run(state, Restored));
    }
}
