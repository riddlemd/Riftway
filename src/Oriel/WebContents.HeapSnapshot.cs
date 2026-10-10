using System.Text;
using System.Text.Json;
using Oriel.Cef;

namespace Oriel;

/// <summary>V8 heap snapshots of the page's renderer.</summary>
public sealed unsafe partial class WebContents
{
    // UI thread: snapshots wait their turn, as DevTools' chunk events do not say which snapshot they belong to.
    private readonly Queue<Action> _heapSnapshots = new();
    private bool _heapSnapshotRunning;

    /// <summary>Writes a V8 heap snapshot of the page's renderer to <paramref name="filePath"/>, in the
    /// <c>.heapsnapshot</c> JSON DevTools reads; taken through DevTools' <c>HeapProfiler.takeHeapSnapshot</c>.
    /// Electron's <c>webContents.takeHeapSnapshot()</c>.</summary>
    /// <exception cref="InvalidOperationException">The file cannot be written, the page has closed or its renderer has
    /// gone, or the snapshot failed; with Electron's messages.</exception>
    public Task TakeHeapSnapshotAsync(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        FileStream file;
        try { file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromException(new InvalidOperationException($"Failed to take heap snapshot with invalid file path {filePath}"));
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CefThread.RunOnUi(() =>
        {
            if (_browser is null || _mainFrameId is null)
            {
                file.Dispose();
                done.TrySetException(new InvalidOperationException("Failed to take heap snapshot with invalid webContents main frame"));
                return;
            }
            if (_crashed)
            {
                file.Dispose();
                done.TrySetException(new InvalidOperationException("Failed to take heap snapshot with nonexistent render frame"));
                return;
            }
            _heapSnapshots.Enqueue(() => WriteHeapSnapshot(file, done));
            if (!_heapSnapshotRunning) NextHeapSnapshot();
        });
        return done.Task;
    }

    // UI thread.
    private void NextHeapSnapshot()
    {
        _heapSnapshotRunning = _heapSnapshots.TryDequeue(out var next);
        next?.Invoke();
    }

    // UI thread: the chunks arrive as events before the command's result.
    private void WriteHeapSnapshot(FileStream file, TaskCompletionSource done)
    {
        var writeFailed = false;
        var finished = false;
        CancellationTokenRegistration closed = default;
        void OnChunk(string method, JsonElement parameters)
        {
            if (method != "HeapProfiler.addHeapSnapshotChunk" || writeFailed) return;
            try { file.Write(Encoding.UTF8.GetBytes(parameters.GetProperty("chunk").GetString() ?? "")); }
            catch (IOException) { writeFailed = true; }
        }
        // A renderer that goes, or a page that closes, never answers: the file is let go and the snapshot fails.
        void OnGone(object? sender, RenderProcessGoneEventArgs e) => Finish(false);
        void Finish(bool answered)
        {
            if (finished) return;
            finished = true;
            _devTools.EventReceived -= OnChunk;
            RenderProcessGone -= OnGone;
            closed.Dispose();
            try { file.Dispose(); }
            catch (IOException) { writeFailed = true; }
            if (answered && !writeFailed) done.TrySetResult();
            else done.TrySetException(new InvalidOperationException("Failed to take heap snapshot"));
            NextHeapSnapshot();
        }
        _devTools.EventReceived += OnChunk;
        RenderProcessGone += OnGone;
        closed = _destroyed.Token.Register(() => CefThread.RunOnUi(() => Finish(false)));
        _devTools.Send("HeapProfiler.takeHeapSnapshot", w => w.WriteBoolean("reportProgress", false), (_, error) => Finish(error is null));
    }
}
