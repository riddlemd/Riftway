using System.IO.Pipes;
using System.Text.Json.Nodes;

namespace Riftway;

/// <summary>A utility process's channel to the process that forked it. Electron's <c>process.parentPort</c>.</summary>
/// <remarks>Needs no CEF: a plain .NET console app referencing Riftway uses it. Events are raised on Riftway's port thread.
/// Once connected (on first use), the process stays alive, as Electron's utility process does, until it calls
/// <see cref="Environment.Exit"/>, is killed, or its parent goes away, which ends it.</remarks>
public static class ParentPort
{
    private static readonly Lazy<PortWire?> s_wire = new(Connect);
    private static readonly Lock s_gate = new();
    // Port thread only.
    private static readonly Queue<MessagePortEvent> s_queue = [];
    private static bool s_started;
    private static EventHandler<MessagePortEvent>? s_message;

    /// <summary>A message from the parent's <see cref="UtilityProcess.PostMessage"/>, with the ports moved here. Messages
    /// wait until the first handler is added, as Electron's do. Electron's <c>message</c> event.</summary>
    public static event EventHandler<MessagePortEvent>? Message
    {
        add
        {
            bool first;
            lock (s_gate)
            {
                first = s_message is null;
                s_message += value;
            }
            if (first && s_wire.Value is not null) PortThread.Run(Start);
        }
        remove
        {
            bool last;
            lock (s_gate)
            {
                s_message -= value;
                last = s_message is null;
            }
            if (last) PortThread.Run(() => s_started = false);
        }
    }

    /// <summary>Sends <paramref name="message"/> to the parent's <see cref="UtilityProcess.Message"/>. Electron's
    /// <c>parentPort.postMessage()</c>, which also moves no ports.</summary>
    /// <exception cref="InvalidOperationException">This process was not started by <see cref="UtilityProcess.Fork(string, IReadOnlyList{string}, UtilityProcessOptions)"/>.</exception>
    public static void PostMessage(JsonNode? message)
    {
        var wire = s_wire.Value ?? throw new InvalidOperationException("parentPort is only available in a utility process.");
        var data = message?.ToJsonString() ?? "null";
        PortThread.Run(() => wire.SendMessage(data, []));
    }

    private static void Start()
    {
        s_started = true;
        while (s_started && s_queue.TryDequeue(out var queued)) Raise(queued);
    }

    private static void Receive(JsonNode? data, IReadOnlyList<MessagePortMain> ports)
    {
        var message = new MessagePortEvent(data, ports);
        if (s_started) Raise(message);
        else s_queue.Enqueue(message);
    }

    private static void Raise(MessagePortEvent message)
    {
        EventHandler<MessagePortEvent>? handler;
        lock (s_gate) handler = s_message;
        try { handler?.Invoke(null, message); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] A parentPort listener threw: {e}"); }
    }

    private static PortWire? Connect()
    {
        var name = Environment.GetEnvironmentVariable(UtilityProcess.PipeVariable);
        var token = Environment.GetEnvironmentVariable(UtilityProcess.TokenVariable);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(token)) return null;
        // Processes this one starts are not utility processes of the same parent.
        Environment.SetEnvironmentVariable(UtilityProcess.PipeVariable, null);
        Environment.SetEnvironmentVariable(UtilityProcess.TokenVariable, null);

        var wire = new PortWire(ConnectAsync(name, token))
        {
            Message = Receive,
            // Chromium's child processes end when the browser's channel drops.
            Gone = () => Environment.Exit(0),
        };
        wire.Start();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => wire.SendNow(Fatal(e.ExceptionObject));
        // Environment.Exit would otherwise drop messages posted just before it.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => wire.SendNow(null);
        PortThread.KeepProcessAlive(true);
        return wire;
    }

    private static async Task<Stream> ConnectAsync(string name, string token)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(30_000).ConfigureAwait(false);
            WireFrame.Write(client, new WireFrame("hello", Token: token).Encode());
            await client.FlushAsync().ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static WireFrame Fatal(object exception)
    {
        var report = exception.ToString() ?? string.Empty;
        var location = (exception as Exception)?.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0)
            ?? exception.GetType().FullName ?? string.Empty;
        return new WireFrame("fatal", Location: location, Report: report);
    }
}
