using Riftway.Cef;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway;

/// <summary>A child process that is not a renderer went away; Electron's <c>child-process-gone</c> details.</summary>
/// <param name="Type">The process type's English name: <c>GPU</c>, <c>Utility</c>, <c>Zygote</c>, <c>Sandbox helper</c>,
/// <c>Pepper Plugin</c>, <c>Pepper Plugin Broker</c> or <c>Unknown</c>. Electron's <c>type</c>.</param>
/// <param name="Reason"><c>abnormal-exit</c>, <c>killed</c>, <c>crashed</c>, <c>oom</c>, <c>launch-failed</c> or
/// <c>integrity-failure</c>, as <see cref="RenderProcessGoneEventArgs.Reason"/>. Electron's <c>reason</c>.</param>
/// <param name="ExitCode">The exit code, or the platform's launch error. Electron's <c>exitCode</c>.</param>
/// <param name="ServiceName">The process's metrics name, e.g. <c>network.mojom.NetworkService</c>. Electron's
/// <c>serviceName</c>.</param>
/// <param name="Name">Its display name, e.g. <c>Audio Service</c>; null when it has none. Electron's <c>name</c>.</param>
/// <param name="SystemErrorCode">Windows, a failed launch only: the system error. Electron's <c>systemErrorCode</c>.</param>
public sealed record ChildProcessGoneDetails(string Type, string Reason, int ExitCode, string ServiceName, string? Name,
    uint? SystemErrorCode);

public sealed partial class RiftwayApp
{
    /// <summary>A GPU, utility or other child process that is not a renderer crashed, was killed or failed to launch.
    /// Electron's <c>child-process-gone</c>.</summary>
    /// <remarks>Needs Riftway's CEF patch 0007 (<see cref="CefRuntime.IsPatched"/>); with stock CEF it is never raised
    /// (drift A31).</remarks>
    public event EventHandler<ChildProcessGoneDetails>? ChildProcessGone;

    // Electron's EmitChildProcessGone: name only when there is one, systemErrorCode only for a Windows launch failure.
    internal static ChildProcessGoneDetails ChildProcessGoneFrom(ChildProcessGoneInfo info, bool windows) => new(
        info.Type,
        RenderProcessGoneReason.Of(info.Status),
        info.ExitCode,
        info.ServiceName,
        info.Name.Length == 0 ? null : info.Name,
        windows && info.Status == cef_termination_status_t.TS_LAUNCH_FAILED ? info.SystemErrorCode : null);

    private void OnChildProcessGone(ChildProcessGoneInfo info)
        => RaiseApp(ChildProcessGone, ChildProcessGoneFrom(info, OperatingSystem.IsWindows()), "child-process-gone");
}
