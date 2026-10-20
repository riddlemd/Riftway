namespace Oriel;

/// <summary>When Oriel raises <see cref="OrielApp.Ready"/>: at once off macOS; on macOS once AppKit has finished launching,
/// which comes after CEF's context is ready, as Electron's <c>ready</c> comes from <c>applicationDidFinishLaunching:</c>.</summary>
internal sealed class ReadyGate(Action<System.Text.Json.Nodes.JsonObject> raise)
{
    private bool _contextReady;
    private bool _raised;

    /// <summary>CEF's context is ready; <paramref name="waitForLaunch"/> on macOS while AppKit has not finished launching.</summary>
    internal void ContextReady(bool waitForLaunch)
    {
        _contextReady = true;
        if (!waitForLaunch) Raise([]);
    }

    /// <summary>AppKit finished launching, with the launch info; raises the waiting <c>Ready</c>.</summary>
    internal void FinishedLaunching(System.Text.Json.Nodes.JsonObject launchInfo)
    {
        if (_contextReady) Raise(launchInfo);
    }

    private void Raise(System.Text.Json.Nodes.JsonObject launchInfo)
    {
        if (_raised) return;
        _raised = true;
        raise(launchInfo);
    }
}
