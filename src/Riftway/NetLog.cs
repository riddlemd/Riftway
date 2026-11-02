using Riftway.Cef;
using Riftway.Cef.Handlers;

namespace Riftway;

/// <summary>What a net log records; Electron's <c>captureMode</c> values.</summary>
public enum NetLogCaptureMode
{
    /// <summary>Request metadata only. Electron's <c>default</c>.</summary>
    Default,
    /// <summary>Also cookies and authentication data. Electron's <c>includeSensitive</c>.</summary>
    IncludeSensitive,
    /// <summary>Also every byte sent and received. Electron's <c>everything</c>.</summary>
    Everything,
}

/// <summary>Options for <see cref="SessionNetLog.StartLoggingAsync"/>.</summary>
public sealed record NetLogOptions
{
    /// <summary>What to record. Electron's <c>captureMode</c>.</summary>
    public NetLogCaptureMode CaptureMode { get; init; } = NetLogCaptureMode.Default;

    /// <summary>The size in bytes at which logging stops by itself; null for no limit. Electron's <c>maxFileSize</c>.</summary>
    public long? MaxFileSize { get; init; }
}

/// <summary>Records a session's network events to a file. Electron's <c>ses.netLog</c>.</summary>
/// <remarks>Needs Riftway's CEF patch 0006 (<see cref="CefRuntime.IsPatched"/>). With stock CEF the methods throw
/// <see cref="PlatformNotSupportedException"/> and <see cref="CurrentlyLogging"/> is false; Chromium's
/// <c>--log-net-log=path</c> switch still records every session for the app's whole run (DRIFT E42).</remarks>
public sealed class SessionNetLog
{
    internal const string StockMessage =
        "netLog needs Riftway's CEF patch 0006. With stock CEF, start the app with --log-net-log=<path> to record the " +
        "whole run's network events.";

    private readonly bool _patched;
    private readonly INetLogBackend _backend;

    internal SessionNetLog(Session session) : this(CefRuntime.IsPatched, new CefNetLogBackend(session)) { }

    internal SessionNetLog(bool patched, INetLogBackend backend)
    {
        _patched = patched;
        _backend = backend;
    }

    /// <summary>Whether a net log is being recorded, from the start call until the stop call. Electron's
    /// <c>netLog.currentlyLogging</c>.</summary>
    public bool CurrentlyLogging => _patched && _backend.IsLogging();

    /// <summary>Starts recording network events to <paramref name="path"/>; completes once recording has begun.
    /// Electron's <c>netLog.startLogging()</c>.</summary>
    /// <exception cref="ArgumentException">The path is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The capture mode or maximum size is invalid.</exception>
    /// <exception cref="InvalidOperationException">A net log is already running. The task faults with it, carrying
    /// Chromium's error text, when the file cannot be opened or the log cannot start.</exception>
    /// <exception cref="PlatformNotSupportedException">The loaded libcef lacks Riftway's CEF patches.</exception>
    public Task StartLoggingAsync(string path, NetLogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0) throw new ArgumentException("The first parameter must be a valid string", nameof(path));
        var mode = CaptureModeValue(options?.CaptureMode ?? NetLogCaptureMode.Default);
        var maxFileSize = MaxFileSizeValue(options?.MaxFileSize);
        if (!_patched) throw new PlatformNotSupportedException(StockMessage);
        return Completed(_backend.Start(path, mode, maxFileSize));
    }

    /// <summary>Stops recording; completes once the file is complete. Electron's <c>netLog.stopLogging()</c>.</summary>
    /// <exception cref="InvalidOperationException">The task faults with it when no net log is in progress.</exception>
    /// <exception cref="PlatformNotSupportedException">The loaded libcef lacks Riftway's CEF patches.</exception>
    public Task StopLoggingAsync()
    {
        if (!_patched) throw new PlatformNotSupportedException(StockMessage);
        return Completed(_backend.Stop());
    }

    internal static int CaptureModeValue(NetLogCaptureMode mode) => mode switch
    {
        NetLogCaptureMode.Default => 0,
        NetLogCaptureMode.IncludeSensitive => 1,
        NetLogCaptureMode.Everything => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Invalid value for captureMode"),
    };

    // CEF's patch reads a negative size as no limit.
    internal static long MaxFileSizeValue(long? maxFileSize) => maxFileSize switch
    {
        null => -1,
        < 0 => throw new ArgumentOutOfRangeException(nameof(maxFileSize), maxFileSize, "Invalid value for maxFileSize"),
        { } size => size,
    };

    // libcef answers with Electron's rejection text, empty on success.
    private static async Task Completed(Task<string> error)
    {
        var text = await error.ConfigureAwait(false);
        if (text.Length > 0) throw new InvalidOperationException(text);
    }
}

/// <summary>Electron's <c>netLog</c> module: <see cref="Session.DefaultSession"/>'s <see cref="Session.NetLog"/>.</summary>
/// <remarks>Before the app is ready the methods do nothing and <see cref="CurrentlyLogging"/> is false, as in Electron.</remarks>
public static class NetLog
{
    /// <summary>Electron's <c>netLog.currentlyLogging</c>.</summary>
    public static bool CurrentlyLogging => IsReady && Session.DefaultSession.NetLog.CurrentlyLogging;

    /// <summary>Electron's <c>netLog.startLogging()</c>.</summary>
    /// <inheritdoc cref="SessionNetLog.StartLoggingAsync" path="/exception"/>
    public static Task StartLoggingAsync(string path, NetLogOptions? options = null)
        => IsReady ? Session.DefaultSession.NetLog.StartLoggingAsync(path, options) : Task.CompletedTask;

    /// <summary>Electron's <c>netLog.stopLogging()</c>.</summary>
    /// <inheritdoc cref="SessionNetLog.StopLoggingAsync" path="/exception"/>
    public static Task StopLoggingAsync() => IsReady ? Session.DefaultSession.NetLog.StopLoggingAsync() : Task.CompletedTask;

    private static bool IsReady => RiftwayApp.CurrentOrNull is { IsReady: true };
}

/// <summary>The libcef side of a <see cref="SessionNetLog"/>; tasks complete with the error text, empty on success.</summary>
internal interface INetLogBackend
{
    bool IsLogging();
    Task<string> Start(string path, int captureMode, long maxFileSize);
    Task<string> Stop();
}

internal sealed unsafe class CefNetLogBackend(Session session) : INetLogBackend
{
    public bool IsLogging() => CefThread.InvokeOnUi(() => CefPatchedApi.IsNetLogging(session.Native));

    public Task<string> Start(string path, int captureMode, long maxFileSize)
    {
        var callback = new NetLogCallback();
        // On the UI thread libcef binds the exporter before returning, so the check and the start cannot interleave.
        CefThread.InvokeOnUi(() =>
        {
            if (CefPatchedApi.IsNetLogging(session.Native))
            {
                NativeObject.Release(callback.Native);
                throw new InvalidOperationException("There is already a net log running");
            }
            return CefPatchedApi.StartNetLog(session.Native, path, captureMode, maxFileSize, callback.Native);
        });
        return callback.Task;
    }

    public Task<string> Stop()
    {
        var callback = new NetLogCallback();
        CefThread.RunOnUi(() => CefPatchedApi.StopNetLog(session.Native, callback.Native));
        return callback.Task;
    }
}
