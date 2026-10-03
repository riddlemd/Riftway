using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef;

/// <summary>CEF's UI thread: browser, window and DevTools calls are only valid there.</summary>
public static unsafe class CefThread
{
    /// <summary>Whether the caller is on CEF's UI thread.</summary>
    public static bool IsOnUiThread => CefNative.cef_currently_on(cef_thread_id_t.TID_UI) != 0;

    /// <summary>Runs <paramref name="action"/> on the UI thread: inline when already there, else queued.</summary>
    public static void RunOnUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsOnUiThread) action();
        else PostUi(action);
    }

    /// <summary>Runs <paramref name="func"/> on the UI thread and returns its result, waiting when called
    /// from another thread.</summary>
    /// <remarks>Must not be called from a thread the UI thread is itself waiting on, or both stop.</remarks>
    public static T InvokeOnUi<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (IsOnUiThread) return func();

        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        PostUi(() =>
        {
            try { result = func(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        });
        done.Wait();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return result;
    }

    /// <summary>Queues <paramref name="action"/> on the UI thread.</summary>
    /// <exception cref="InvalidOperationException">CEF is not running, so nothing would run it.</exception>
    public static void PostUi(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // cef_post_task takes the task's only reference; CEF releases it after execute.
        var task = new Task(action);
        if (CefNative.cef_post_task(cef_thread_id_t.TID_UI, task.Native) == 0)
            throw new InvalidOperationException("CEF's UI thread is not running.");
    }

    /// <summary>Queues <paramref name="action"/> on the UI thread after <paramref name="delay"/>.</summary>
    /// <exception cref="InvalidOperationException">CEF is not running, so nothing would run it.</exception>
    public static void PostUiDelayed(Action action, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(action);
        var task = new Task(action);
        if (CefNative.cef_post_delayed_task(cef_thread_id_t.TID_UI, task.Native, (long)delay.TotalMilliseconds) == 0)
            throw new InvalidOperationException("CEF's UI thread is not running.");
    }

    private sealed class Task
    {
        private readonly Action _action;

        public Task(Action action)
        {
            _action = action;
            Native = NativeObject.Create<cef_task_t>(this);
            Native->execute = &Execute;
        }

        public cef_task_t* Native { get; }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Execute(cef_task_t* self)
        {
            var owner = NativeObject.Owner<Task>(self);
            try { owner._action(); }
            catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a UI-thread task: {e}"); }
        }
    }
}
