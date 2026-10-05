using System.Text.Json;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Chromium's trace configuration; Electron's <c>TraceConfig</c>. CEF takes only category lists.</summary>
public sealed record TraceConfig
{
    /// <summary>Category patterns to record, e.g. <c>blink*</c>.</summary>
    public IReadOnlyList<string> IncludedCategories { get; init; } = [];

    /// <summary>Category patterns to leave out; CEF cannot mix them with <see cref="IncludedCategories"/>.</summary>
    public IReadOnlyList<string> ExcludedCategories { get; init; } = [];

    /// <summary>Not supported (setting it throws): CEF has no recording modes.</summary>
    public string? RecordingMode { get; init; }

    /// <summary>Not supported (setting it throws): CEF chooses the buffer.</summary>
    public int? TraceBufferSizeInKb { get; init; }

    /// <summary>Not supported (setting it true throws): CEF has no argument filter.</summary>
    public bool EnableArgumentFilter { get; init; }
}

/// <summary>Records Chromium's trace events from every process; Electron's <c>contentTracing</c>.</summary>
public static unsafe class ContentTracing
{
    /// <summary>Starts recording the categories in <paramref name="config"/>; every category Chromium records by
    /// default when none are given. Electron's <c>contentTracing.startRecording()</c>.</summary>
    /// <exception cref="NotSupportedException">An option CEF has no equivalent for, or both included and excluded categories.</exception>
    /// <exception cref="InvalidOperationException">Recording has already started.</exception>
    public static Task StartRecordingAsync(TraceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.RecordingMode is not null || config.TraceBufferSizeInKb is not null || config.EnableArgumentFilter)
            throw new NotSupportedException("Only included or excluded categories are supported: CEF has no other trace options.");
        if (config.IncludedCategories.Count > 0 && config.ExcludedCategories.Count > 0)
            throw new NotSupportedException("Included and excluded categories together are not supported: CEF takes one or the other.");
        return Begin(string.Join(",", config.IncludedCategories.Concat(config.ExcludedCategories.Select(c => "-" + c))));
    }

    /// <summary>Starts recording the categories in <paramref name="categoryFilter"/> (comma-separated, <c>-</c> to
    /// exclude). Electron's <c>contentTracing.startRecording({ categoryFilter, traceOptions })</c>.</summary>
    /// <exception cref="NotSupportedException"><paramref name="traceOptions"/> is set: CEF takes no trace options.</exception>
    /// <exception cref="InvalidOperationException">Recording has already started.</exception>
    public static Task StartRecordingAsync(string categoryFilter, string? traceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(categoryFilter);
        if (!string.IsNullOrEmpty(traceOptions)) throw new NotSupportedException("traceOptions are not supported: CEF takes no trace options.");
        return Begin(categoryFilter);
    }

    /// <summary>Stops recording and writes the trace to <paramref name="resultFilePath"/>, or a temporary file;
    /// returns the file's path. Electron's <c>contentTracing.stopRecording()</c>.</summary>
    /// <exception cref="InvalidOperationException">Recording has not started, or is already stopping.</exception>
    public static Task<string> StopRecordingAsync(string? resultFilePath = null)
    {
        var callback = new EndTracingCallback();
        var started = CefThread.InvokeOnUi(() =>
        {
            cef_string_utf16_t path = default;
            CefString.Set(&path, resultFilePath ?? "");
            var ok = CefNative.cef_end_tracing(&path, callback.Native) != 0;
            CefString.Clear(&path);
            return ok;
        });
        return started ? callback.Task : Task.FromException<string>(new InvalidOperationException("Tracing has not started, or is already stopping."));
    }

    /// <summary>Not supported: Chromium lists its categories only to the browser's DevTools target, which CEF does
    /// not open to Oriel (drift P6). Electron's <c>contentTracing.getCategories()</c>.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public static Task<IReadOnlyList<string>> GetCategoriesAsync()
        => throw new NotSupportedException("Listing trace categories is not supported: Chromium lists them only to the browser's DevTools target.");

    /// <summary>Not supported: CEF reports no trace buffer use. Electron's <c>contentTracing.getTraceBufferUsage()</c>.</summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public static Task GetTraceBufferUsageAsync()
        => throw new NotSupportedException("Trace buffer usage is not supported: CEF does not report it.");

    private static Task Begin(string categories)
    {
        var callback = new CompletionCallback();
        var started = CefThread.InvokeOnUi(() =>
        {
            cef_string_utf16_t native = default;
            CefString.Set(&native, categories);
            var ok = CefNative.cef_begin_tracing(&native, callback.Native) != 0;
            CefString.Clear(&native);
            return ok;
        });
        return started ? callback.Task : Task.FromException(new InvalidOperationException("Tracing has already started, or is still stopping."));
    }
}
