using System.Collections.Concurrent;
using Oriel.Cef;

namespace Oriel;

/// <summary>The one thread every port, link and wire runs on: CEF's UI thread in an app, or Oriel's own loop in a
/// process without CEF (a utility process, a unit test).</summary>
internal static class PortThread
{
    private static readonly BlockingCollection<Action> s_queue = [];
    private static readonly Lazy<Thread> s_loop = new(StartLoop);

    // libcef is only loaded by OrielApp.Run; a utility process never loads it, so CefNative must not be touched there.
    private static bool UsesCef => CefRuntime.IsLoaded;

    internal static bool IsCurrent => UsesCef ? CefThread.IsOnUiThread : s_loop.IsValueCreated && Thread.CurrentThread == s_loop.Value;

    /// <summary>Runs <paramref name="action"/> inline when already on the port thread, else queues it.</summary>
    internal static void Run(Action action)
    {
        if (IsCurrent) action();
        else Post(action);
    }

    /// <summary>Queues <paramref name="action"/>; dropped once CEF has shut down, as nothing would run it.</summary>
    internal static void Post(Action action)
    {
        if (!UsesCef)
        {
            _ = s_loop.Value;
            s_queue.Add(action);
            return;
        }
        try { CefThread.PostUi(action); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Whether the loop keeps the process alive, as a started <c>parentPort</c> does a utility process.</summary>
    internal static void KeepProcessAlive(bool keep)
    {
        if (!UsesCef) s_loop.Value.IsBackground = !keep;
    }

    private static Thread StartLoop()
    {
        var thread = new Thread(() =>
        {
            foreach (var action in s_queue.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a port task: {e}"); }
            }
        })
        { IsBackground = true, Name = "Oriel ports" };
        thread.Start();
        return thread;
    }
}
