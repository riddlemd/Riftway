using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Riftway.Cef;
using Riftway.Cef.Handlers;
using Riftway.Cef.Interop;

namespace Riftway;

/// <summary>Chromium's trace configuration; Electron's <c>TraceConfig</c>.</summary>
public sealed record TraceConfig
{
    /// <summary><c>record-until-full</c> (the default), <c>record-continuously</c>, <c>record-as-much-as-possible</c>
    /// or <c>trace-to-console</c>; any other value is the default, as Chromium's.</summary>
    public string? RecordingMode { get; init; }

    /// <summary>The trace buffer's size in kilobytes; Chromium's default when not positive.</summary>
    public int? TraceBufferSizeInKb { get; init; }

    /// <summary>Not supported (setting it positive throws): the DevTools trace configuration has no event count (drift P15).</summary>
    public int? TraceBufferSizeInEvents { get; init; }

    /// <summary>Records only the arguments Chromium has vetted to hold no personal data.</summary>
    public bool EnableArgumentFilter { get; init; }

    /// <summary>Category patterns to record, e.g. <c>blink*</c>; every enabled-by-default category when empty.</summary>
    public IReadOnlyList<string> IncludedCategories { get; init; } = [];

    /// <summary>Category patterns to leave out.</summary>
    public IReadOnlyList<string> ExcludedCategories { get; init; } = [];

    /// <summary>Not supported (setting any throws): the DevTools trace configuration has no process filter (drift P15).</summary>
    public IReadOnlyList<int> IncludedProcessIds { get; init; } = [];

    /// <summary>Not supported (setting any throws): the DevTools trace configuration has no histograms (drift P15).</summary>
    public IReadOnlyList<string> HistogramNames { get; init; } = [];

    /// <summary>Memory-infra's dump triggers, e.g. <c>{"triggers":[{"mode":"detailed","periodic_interval_ms":1000}]}</c>;
    /// used when <c>disabled-by-default-memory-infra</c> is recorded.</summary>
    public JsonObject? MemoryDumpConfig { get; init; }
}

/// <summary>How full the trace buffer is; Electron's <c>getTraceBufferUsage()</c> result.</summary>
/// <param name="Value">Always 0, as Electron's: Chromium no longer counts events.</param>
/// <param name="Percentage">The fullest buffer's use, from 0 to 1.</param>
public sealed record TraceBufferUsage(double Value, double Percentage);

/// <summary>Records Chromium's trace events; Electron's <c>contentTracing</c>.</summary>
/// <remarks>A configuration that is only categories records every process through CEF. Any other option records through
/// an open page's DevTools session, which Chromium limits to the browser, GPU and that page's renderers (drift P14).</remarks>
public static unsafe class ContentTracing
{
    private static readonly Lock s_gate = new();
    private static Recording? s_recording;

    /// <summary>Starts recording as <paramref name="config"/> says; resolves at once when already recording, as
    /// Electron's. Electron's <c>contentTracing.startRecording(traceConfig)</c>.</summary>
    /// <exception cref="NotSupportedException">An option Riftway cannot reach (drift P15), or an option beyond categories
    /// with no page open (drift P14).</exception>
    public static Task StartRecordingAsync(TraceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Start(TracePlan.For(config));
    }

    /// <summary>Starts recording the categories in <paramref name="categoryFilter"/> (comma-separated, <c>-</c> to
    /// exclude) with Chromium's <paramref name="traceOptions"/>, e.g. <c>record-continuously</c>. Electron's
    /// <c>contentTracing.startRecording({ categoryFilter, traceOptions })</c>.</summary>
    /// <exception cref="NotSupportedException">Options beyond the defaults with no page open (drift P14).</exception>
    public static Task StartRecordingAsync(string categoryFilter, string? traceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(categoryFilter);
        return Start(TracePlan.For(categoryFilter, traceOptions ?? ""));
    }

    /// <summary>Stops recording and writes the trace to <paramref name="resultFilePath"/>, or a temporary file;
    /// returns the file's path. Electron's <c>contentTracing.stopRecording()</c>.</summary>
    /// <exception cref="InvalidOperationException">No trace is in progress, or it is already stopping, or the page
    /// recording it was destroyed.</exception>
    public static Task<string> StopRecordingAsync(string? resultFilePath = null)
    {
        Recording? recording;
        lock (s_gate)
        {
            recording = s_recording;
            if (recording is null) return Task.FromException<string>(new InvalidOperationException("Failed to stop tracing - no trace in progress"));
            if (recording.Stopped is not null) return Task.FromException<string>(new InvalidOperationException("Failed to stop tracing"));
            recording.Stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        var path = string.IsNullOrEmpty(resultFilePath) ? null : resultFilePath;
        if (recording.Host is null) StopCef(recording, path);
        else CefThread.RunOnUi(() => StopDevTools(recording, path));
        return recording.Stopped.Task;
    }

    /// <summary>The trace categories Chromium knows of, sorted. Electron's <c>contentTracing.getCategories()</c>.</summary>
    /// <exception cref="NotSupportedException">No page is open: Chromium lists categories only to a DevTools session (drift P14).</exception>
    public static Task<IReadOnlyList<string>> GetCategoriesAsync()
    {
        var done = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            if (FindHost() is not { } host) { done.SetException(NoPage("Listing trace categories")); return; }
            // Detaching drops the pending reply, so a page destroyed mid-request must answer for it.
            var gone = host.DestroyedToken.Register(() => done.TrySetException(new InvalidOperationException("The page asked for trace categories was destroyed.")));
            host.DevTools.Send("Tracing.getCategories", onResult: (result, error) =>
            {
                gone.Dispose();
                if (error is not null) done.TrySetException(new InvalidOperationException(error));
                else done.TrySetResult([.. result.GetProperty("categories").EnumerateArray().Select(c => c.GetString()!)]);
            });
        });
        return done.Task;
    }

    /// <summary>How full the trace buffer is; zeros when not recording, as Electron's. Electron's
    /// <c>contentTracing.getTraceBufferUsage()</c>.</summary>
    /// <exception cref="NotSupportedException">The recording is categories only, through CEF, which reports no use (drift P14).</exception>
    public static Task<TraceBufferUsage> GetTraceBufferUsageAsync()
    {
        lock (s_gate)
        {
            if (s_recording is null) return Task.FromResult(new TraceBufferUsage(0, 0));
            if (s_recording.Host is null)
                return Task.FromException<TraceBufferUsage>(new NotSupportedException("Trace buffer usage is not supported for a categories-only recording: CEF does not report it."));
            return Task.FromResult(new TraceBufferUsage(0, s_recording.PercentFull));
        }
    }

    private static Task Start(TracePlan plan)
    {
        lock (s_gate)
        {
            if (s_recording is not null) return Task.CompletedTask;
        }
        return plan.DevTools ? StartDevTools(plan) : StartCef(plan.Categories!);
    }

    private static Task StartCef(string categories)
    {
        var callback = new CompletionCallback();
        var started = CefThread.InvokeOnUi(() =>
        {
            lock (s_gate)
            {
                if (s_recording is not null) return false;
                cef_string_utf16_t native = default;
                CefString.Set(&native, categories);
                var ok = CefNative.cef_begin_tracing(&native, callback.Native) != 0;
                CefString.Clear(&native);
                if (ok) s_recording = new Recording(null);
                return ok;
            }
        });
        // CEF refuses only while it is already tracing, which Electron answers by resolving.
        return started ? callback.Task : Task.CompletedTask;
    }

    private static void StopCef(Recording recording, string? path)
    {
        var callback = new EndTracingCallback();
        var ended = CefThread.InvokeOnUi(() =>
        {
            cef_string_utf16_t native = default;
            CefString.Set(&native, path ?? "");
            var ok = CefNative.cef_end_tracing(&native, callback.Native) != 0;
            CefString.Clear(&native);
            return ok;
        });
        if (!ended) { Finish(recording, error: new InvalidOperationException("Failed to stop tracing")); return; }
        callback.Task.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) Finish(recording, t.Result);
            else Finish(recording, error: t.Exception!.InnerException!);
        }, TaskScheduler.Default);
    }

    private static Task StartDevTools(TracePlan plan)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            Recording recording;
            lock (s_gate)
            {
                if (s_recording is not null) { done.SetResult(); return; }
                if (FindHost() is not { } host) { done.SetException(NoPage("Trace options beyond categories")); return; }
                s_recording = recording = new Recording(host);
            }
            recording.Started = done;
            recording.Host!.DevTools.EventReceived += recording.OnEvent;
            recording.HostGone = recording.Host.DestroyedToken.Register(() => OnHostDestroyed(recording));
            recording.Host.DevTools.Send("Tracing.start", w =>
            {
                if (plan.TraceConfigJson is { } json)
                {
                    w.WritePropertyName("traceConfig");
                    w.WriteRawValue(json);
                }
                else
                {
                    w.WriteString("categories", plan.Categories);
                    w.WriteString("options", plan.Options);
                }
                w.WriteString("transferMode", "ReturnAsStream");
                w.WriteString("streamFormat", "json");
                w.WriteNumber("bufferUsageReportingInterval", 250);
            }, (_, error) =>
            {
                if (error is null) { done.TrySetResult(); return; }
                Finish(recording, error: null);
                // Chromium is already tracing for another client, which Electron answers by resolving.
                if (error.Contains("already", StringComparison.Ordinal)) done.TrySetResult();
                else done.TrySetException(new InvalidOperationException(error));
            });
        });
        return done.Task;
    }

    private static void StopDevTools(Recording recording, string? path)
    {
        recording.Path = path;
        recording.Host!.DevTools.Send("Tracing.end", onResult: (_, error) =>
        {
            if (error is not null) Finish(recording, error: new InvalidOperationException($"Failed to stop tracing - {error}"));
        });
    }

    private static void OnTracingComplete(Recording recording, JsonElement parameters)
    {
        if (!parameters.TryGetProperty("stream", out var stream)) { Finish(recording, error: new InvalidOperationException("Failed to stop tracing")); return; }
        var handle = stream.GetString()!;
        var data = new ArrayBufferWriter<byte>();
        // Bounded chunks keep each DevTools message small; a trace can run to hundreds of megabytes.
        void ReadNext() => recording.Host!.DevTools.Send("IO.read", w =>
        {
            w.WriteString("handle", handle);
            w.WriteNumber("size", 1 << 18);
        }, (result, error) =>
        {
            if (error is not null) { Finish(recording, error: new InvalidOperationException(error)); return; }
            var chunk = result.GetProperty("data").GetString() ?? "";
            data.Write(result.TryGetProperty("base64Encoded", out var b64) && b64.GetBoolean() ? Convert.FromBase64String(chunk) : Encoding.UTF8.GetBytes(chunk));
            if (!result.GetProperty("eof").GetBoolean()) { ReadNext(); return; }
            recording.Host!.DevTools.Send("IO.close", w => w.WriteString("handle", handle));
            var bytes = data.WrittenMemory;
            Task.Run(() =>
            {
                var file = recording.Path ?? Path.GetTempFileName();
                File.WriteAllBytes(file, bytes.Span);
                return file;
            }).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) Finish(recording, t.Result);
                else Finish(recording, error: t.Exception!.InnerException!);
            }, TaskScheduler.Default);
        });
        ReadNext();
    }

    private static void OnHostDestroyed(Recording recording)
    {
        var error = new InvalidOperationException("Failed to stop tracing - the page recording the trace was destroyed");
        recording.Started?.TrySetException(error);
        Finish(recording, error: error);
    }

    private static void Finish(Recording recording, string? path = null, Exception? error = null)
    {
        lock (s_gate)
        {
            if (ReferenceEquals(s_recording, recording)) s_recording = null;
        }
        if (recording.Host is { } host)
        {
            CefThread.RunOnUi(() => host.DevTools.EventReceived -= recording.OnEvent);
            recording.HostGone.Dispose();
        }
        if (recording.Stopped is { } stopped)
        {
            if (error is not null) stopped.TrySetException(error);
            else if (path is not null) stopped.TrySetResult(path);
        }
    }

    private static WebContents? FindHost()
    {
        if (s_recording?.Host is { } recordingHost) return recordingHost;
        return WebContents.GetAllWebContents().FirstOrDefault(w => w.HasBrowser && !w.IsDestroyed());
    }

    private static NotSupportedException NoPage(string what)
        => new($"{what} needs an open page: Riftway reaches Chromium's tracing service only through a page's DevTools session.");

    private sealed class Recording(WebContents? host)
    {
        public WebContents? Host { get; } = host;
        public TaskCompletionSource? Started { get; set; }
        public TaskCompletionSource<string>? Stopped { get; set; }
        public string? Path { get; set; }
        public double PercentFull { get; set; }
        public CancellationTokenRegistration HostGone { get; set; }

        public void OnEvent(string method, JsonElement parameters)
        {
            switch (method)
            {
                case "Tracing.bufferUsage" when parameters.TryGetProperty("percentFull", out var percent):
                    PercentFull = percent.GetDouble();
                    break;
                case "Tracing.tracingComplete":
                    OnTracingComplete(this, parameters);
                    break;
            }
        }
    }
}

/// <summary>Which route a trace request takes, and what it sends: CEF's category string, or a DevTools
/// <c>Tracing.start</c>'s categories and options, or its trace configuration.</summary>
internal sealed record TracePlan(bool DevTools, string? Categories, string? Options, string? TraceConfigJson)
{
    private const string UntilFull = "record-until-full";
    private const string ToConsole = "trace-to-console";
    private static readonly string[] s_modes = [UntilFull, "record-continuously", "record-as-much-as-possible", ToConsole];

    /// <summary>Chromium's <c>TraceConfig(category_filter, trace_options)</c>; CEF's own string when the options are the defaults.</summary>
    public static TracePlan For(string categoryFilter, string traceOptions)
    {
        var mode = UntilFull;
        var other = false;
        foreach (var token in traceOptions.Split(',', StringSplitOptions.TrimEntries))
        {
            // Chromium ignores tokens it does not know, e.g. the enable-sampling Electron still documents.
            if (s_modes.Contains(token)) mode = token;
            else if (token == "enable-argument-filter" || token.StartsWith("enable-systrace", StringComparison.Ordinal)) other = true;
        }
        return mode == UntilFull && !other ? new(false, categoryFilter, null, null) : new(true, categoryFilter, traceOptions, null);
    }

    /// <summary>Chromium's <c>TraceConfig(dict)</c>, through the narrowest route that carries every option set.</summary>
    /// <exception cref="NotSupportedException">An option no route carries.</exception>
    public static TracePlan For(TraceConfig config)
    {
        if (config.IncludedProcessIds.Count > 0) throw Unreachable("included_process_ids");
        if (config.HistogramNames.Count > 0) throw Unreachable("histogram_names");
        if (config.TraceBufferSizeInEvents > 0) throw Unreachable("trace_buffer_size_in_events");

        var mode = s_modes.Contains(config.RecordingMode) ? config.RecordingMode! : UntilFull;
        var categories = CategoryFilter(config);
        if (categories is not null && config.TraceBufferSizeInKb is not > 0 && config.MemoryDumpConfig is null)
        {
            if (mode == UntilFull && !config.EnableArgumentFilter) return new(false, categories, null, null);
            return new(true, categories, config.EnableArgumentFilter ? mode + ",enable-argument-filter" : mode, null);
        }
        // DevTools turns echoToConsole into echo-to-console, which Chromium does not know.
        if (mode == ToConsole)
            throw new NotSupportedException("trace-to-console with a buffer size, memory dump configuration or unusual category names is not supported (drift P15).");
        return new(true, null, null, DevToolsTraceConfig(config, mode));
    }

    // Chromium's string form splits on commas and trims, and a leading '-' excludes, so such names need the dictionary form.
    private static string? CategoryFilter(TraceConfig config)
    {
        static bool Plain(string c) => !c.Contains(',') && c.Trim() == c;
        if (!config.IncludedCategories.All(c => Plain(c) && !c.StartsWith('-')) || !config.ExcludedCategories.All(Plain)) return null;
        return string.Join(",", config.IncludedCategories.Concat(config.ExcludedCategories.Select(c => "-" + c)));
    }

    private static string DevToolsTraceConfig(TraceConfig config, string mode)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            // DevTools spells the modes in camel case and converts them back with dashes.
            w.WriteString("recordMode", mode switch
            {
                "record-continuously" => "recordContinuously",
                "record-as-much-as-possible" => "recordAsMuchAsPossible",
                _ => "recordUntilFull",
            });
            if (config.TraceBufferSizeInKb is > 0 and var kb) w.WriteNumber("traceBufferSizeInKb", kb);
            if (config.EnableArgumentFilter) w.WriteBoolean("enableArgumentFilter", true);
            WriteList(w, "includedCategories", config.IncludedCategories);
            WriteList(w, "excludedCategories", config.ExcludedCategories);
            if (config.MemoryDumpConfig is { } dump)
            {
                w.WritePropertyName("memoryDumpConfig");
                dump.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteList(Utf8JsonWriter w, string name, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        w.WriteStartArray(name);
        foreach (var item in items) w.WriteStringValue(item);
        w.WriteEndArray();
    }

    private static NotSupportedException Unreachable(string option)
        => new($"{option} is not supported: Riftway reaches Chromium's tracing only through a page's DevTools session, whose trace configuration has no such field (drift P15).");
}
