using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>One background STA thread for shell calls that want an apartment-threaded COM caller (ShellExecute, the
/// shell link and file operation objects); calls run one at a time, in order.</summary>
/// <remarks>Chromium's UI thread is in the MTA, and a caller's own thread may be anything.</remarks>
[SupportedOSPlatform("windows")]
public static class WindowsSta
{
    private static readonly BlockingCollection<Action> s_queue = [];
    private static readonly Lazy<Thread> s_thread = new(() =>
    {
        var thread = new Thread(() =>
        {
            // Blocking waits on an STA thread pump COM messages.
            foreach (var work in s_queue.GetConsumingEnumerable()) work();
        }) { IsBackground = true, Name = "Oriel shell STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    });

    /// <summary>Runs <paramref name="func"/> on the STA thread; its exception, if any, faults the task.</summary>
    public static Task<T> Run<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        _ = s_thread.Value;
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_queue.Add(() =>
        {
            try { done.SetResult(func()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }
}
