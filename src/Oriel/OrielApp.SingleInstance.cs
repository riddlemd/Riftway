using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oriel.Cef.Platform;

namespace Oriel;

public sealed partial class OrielApp
{
    // Rides on the forwarded command line; the running instance takes it off before raising SecondInstance.
    internal const string AdditionalDataSwitch = "oriel-second-instance-data";

    private bool _singleInstanceLock;

    /// <summary>Asks to be the only instance. Electron's <c>app.requestSingleInstanceLock()</c>: false means another
    /// instance holds it and has been sent this launch's command line, working directory and
    /// <paramref name="additionalData"/> (its <see cref="SecondInstance"/>), so this one should quit.</summary>
    /// <remarks>Oriel is always single-instance per session data directory (drift A2), so the lock is that directory's,
    /// which CEF takes in <see cref="Run"/>. This speaks Chromium's singleton protocol to a running instance: the socket
    /// on macOS and Linux, the message window's <c>WM_COPYDATA</c> on Windows.</remarks>
    public bool RequestSingleInstanceLock(JsonNode? additionalData = null)
    {
        if (_singleInstanceLock) return true;
        // Once running, this instance already holds CEF's lock, and the socket or window there is its own.
        if (!IsReady && OperatingSystem.IsWindows())
        {
            // Chromium forwards the raw command line; the data rides on it as on macOS and Linux.
            var commandLine = Environment.CommandLine + (additionalData is null ? "" : " " + EncodeAdditionalData(additionalData));
            if (WindowsProcessSingleton.Notify(GetPath("sessionData"), Environment.CurrentDirectory, commandLine)) return false;
        }
        else if (!IsReady)
        {
            var directory = GetPath("sessionData");
            var argv = RelaunchArgv(Environment.ProcessPath, Environment.GetCommandLineArgs(), null).ToList();
            if (additionalData is not null) argv.Add(EncodeAdditionalData(additionalData));
            if (NotifySingleton(directory, Environment.CurrentDirectory, argv) == SingletonNotify.Notified) return false;
        }
        _singleInstanceLock = true;
        return true;
    }

    /// <summary>Whether <see cref="RequestSingleInstanceLock"/> succeeded and <see cref="ReleaseSingleInstanceLock"/>
    /// has not been called. Electron's <c>app.hasSingleInstanceLock()</c>.</summary>
    public bool HasSingleInstanceLock() => _singleInstanceLock;

    /// <summary>Gives up the lock <see cref="RequestSingleInstanceLock"/> took. Electron's
    /// <c>app.releaseSingleInstanceLock()</c>; CEF keeps the data directory locked while the app runs, so later launches
    /// are still handed to this instance (drift A2).</summary>
    public void ReleaseSingleInstanceLock() => _singleInstanceLock = false;

    internal static string EncodeAdditionalData(JsonNode data)
        => $"--{AdditionalDataSwitch}=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(data.ToJsonString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // The forwarded command line without the data switch, and the data it carried.
    internal static (string CommandLine, JsonNode? Data) TakeAdditionalData(string commandLine)
    {
        var match = Regex.Match(commandLine, $@"\s*""?--{AdditionalDataSwitch}=([A-Za-z0-9_-]*)""?");
        if (!match.Success) return (commandLine, null);
        var text = match.Groups[1].Value.Replace('-', '+').Replace('_', '/');
        text += new string('=', (4 - text.Length % 4) % 4);
        JsonNode? data;
        try { data = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(text))); }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException) { data = null; }
        return (commandLine.Remove(match.Index, match.Length), data);
    }

    internal enum SingletonNotify
    {
        None,
        Notified,
        ShuttingDown,
    }

    // The client half of Chromium's ProcessSingletonPosix: connect through SingletonSocket once the cookies match, send
    // "START\0<cwd>\0<argv...>", and wait for ACK. Anything short of a connection leaves the decision to CEF in Run.
    internal static SingletonNotify NotifySingleton(string userDataDirectory, string currentDirectory, IReadOnlyList<string> argv)
    {
        if (SingletonSocketTarget(userDataDirectory) is not { } target) return SingletonNotify.None;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.SendTimeout = socket.ReceiveTimeout = (int)TimeSpan.FromSeconds(20).TotalMilliseconds;
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(target));
            if (SingletonSocketTarget(userDataDirectory) != target) return SingletonNotify.None;
            socket.Send(SingletonMessage(currentDirectory, argv));
            socket.Shutdown(SocketShutdown.Send);
            var buffer = new byte[8];
            var length = socket.Receive(buffer);
            return Encoding.ASCII.GetString(buffer, 0, length) switch
            {
                "ACK" => SingletonNotify.Notified,
                "SHUTDOWN" => SingletonNotify.ShuttingDown,
                _ => SingletonNotify.None,
            };
        }
        catch (SocketException)
        {
            return SingletonNotify.None;
        }
    }

    // ProcessSingletonPosix's message: START, the working directory, then each argument, NUL-separated.
    internal static byte[] SingletonMessage(string currentDirectory, IReadOnlyList<string> argv)
        => Encoding.UTF8.GetBytes(string.Join('\0', ["START", currentDirectory, .. argv]));

    // ConnectSocket's checks: SingletonSocket links into a directory whose SingletonCookie names the same cookie as
    // the profile's; a plain socket there (an older Chromium) is used as is.
    internal static string? SingletonSocketTarget(string userDataDirectory)
    {
        var socketPath = Path.Combine(userDataDirectory, "SingletonSocket");
        var socketInfo = new FileInfo(socketPath);
        if (socketInfo.LinkTarget is not { } target) return socketInfo.Exists ? socketPath : null;
        var cookie = new FileInfo(Path.Combine(userDataDirectory, "SingletonCookie")).LinkTarget;
        if (string.IsNullOrEmpty(cookie)) return null;
        var remoteCookie = new FileInfo(Path.Combine(Path.GetDirectoryName(target) ?? "", "SingletonCookie")).LinkTarget;
        return remoteCookie == cookie ? target : null;
    }
}
