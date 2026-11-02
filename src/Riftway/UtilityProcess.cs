using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace Riftway;

/// <summary>The child's exit. Electron's <c>exit</c> event.</summary>
/// <param name="Code">GetExitCodeProcess on Windows; on POSIX the exit status, or 128 + the signal that ended it (drift UP2).</param>
public sealed record UtilityProcessExitEventArgs(int Code);

/// <summary>The child is ending on a fatal error. Electron's <c>error</c> event.</summary>
/// <param name="Type">Always <c>FatalError</c>.</param>
/// <param name="Location">Where the error was thrown: the top frame of its stack.</param>
/// <param name="Report">The exception, with its stack (Electron's is a Node.js diagnostic report).</param>
public sealed record UtilityProcessErrorEventArgs(string Type, string Location, string Report);

/// <summary>A .NET child process connected to this one by a message channel. Electron's <c>utilityProcess</c> and
/// <c>UtilityProcess</c>. Events are raised on the UI thread.</summary>
/// <remarks>The module is a .NET entry assembly (or any executable) that talks back through <see cref="ParentPort"/>.
/// Every utility process is killed when the application quits, and ends itself if this process dies.</remarks>
public sealed partial class UtilityProcess
{
    internal const string PipeVariable = "RIFTWAY_UTILITY_PIPE";

    /// <summary>The channel's pipe name: a bare name on Windows; on Unix the socket's absolute path in this process's temp
    /// directory, which .NET uses as given.</summary>
    /// <remarks>A relative name resolves under the child's own $TMPDIR, which a replaced environment drops (macOS keeps it
    /// under /var/folders). The id stays short: a macOS socket path must fit in 104 bytes.</remarks>
    internal static string PipeName(string id, bool windows, string tempPath) => windows ? id : Path.Combine(tempPath, id);
    internal const string TokenVariable = "RIFTWAY_UTILITY_TOKEN";

    // Electron's kLaunchFailureExitCode.
    private const int LaunchFailureExitCode = 1;

    private static readonly Lock s_gate = new();
    // Live processes, which also keeps each reachable until it exits, as Electron's keep_alive_.
    private static readonly HashSet<UtilityProcess> s_live = [];

    private readonly TaskCompletionSource _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _acceptCancel = new();
    private Process? _process;
    private PortWire? _wire;
    private int _pid;
    private volatile bool _killed;
    private volatile bool _exited;
    // Port thread only.
    private bool _terminated;

    private UtilityProcess() { }

    /// <summary>The child has started. Electron's <c>spawn</c>.</summary>
    public event EventHandler? Spawn;

    /// <summary>The child has ended, after <see cref="Error"/> if it failed. Raised with code 1 and no <see cref="Spawn"/>
    /// when it could not be started. Electron's <c>exit</c>.</summary>
    public event EventHandler<UtilityProcessExitEventArgs>? Exit;

    /// <summary>The child sent a message with <see cref="ParentPort.PostMessage"/>. Electron's <c>message</c>.</summary>
    public event EventHandler<JsonNode?>? Message;

    /// <summary>The child is ending on an unhandled exception. Electron's <c>error</c> (drift UP1).</summary>
    public event EventHandler<UtilityProcessErrorEventArgs>? Error;

    /// <summary>The child's process id: null until <see cref="Spawn"/>, and from <see cref="Exit"/> on. Electron's <c>pid</c>.</summary>
    public int? Pid => Volatile.Read(ref _pid) is var pid and not 0 ? pid : null;

    /// <summary>The child's stdout with <c>stdio</c> <c>pipe</c>, else null; null after <see cref="Exit"/>. Electron's <c>stdout</c>.</summary>
    public Stream? Stdout { get; private set; }

    /// <summary>The child's stderr with <c>stdio</c> <c>pipe</c>, else null; null after <see cref="Exit"/>. Electron's <c>stderr</c>.</summary>
    public Stream? Stderr { get; private set; }

    /// <inheritdoc cref="Fork(string, IReadOnlyList{string}, UtilityProcessOptions)"/>
    public static UtilityProcess Fork(string modulePath, UtilityProcessOptions options) => Fork(modulePath, null, options);

    /// <summary>Starts <paramref name="modulePath"/> as a utility process. Electron's <c>utilityProcess.fork()</c>.</summary>
    /// <param name="modulePath">A .NET entry assembly (<c>.dll</c>), run by this process's .NET host, or by its apphost beside it
    /// when this process is self-contained; or an executable, run as it is.</param>
    /// <param name="args">The child's command-line arguments (its <c>Main(args)</c>).</param>
    /// <param name="options">How to start it.</param>
    /// <exception cref="ArgumentException"><paramref name="modulePath"/> is empty, or <c>stdio</c> is not valid.</exception>
    /// <remarks>Events are queued on the UI thread, so a caller on the UI thread subscribes before <see cref="Spawn"/>, as in
    /// Electron; from another thread <see cref="Spawn"/> may already have been raised when this returns.</remarks>
    /// <exception cref="InvalidOperationException">The application is not ready.</exception>
    public static UtilityProcess Fork(string modulePath, IReadOnlyList<string>? args = null, UtilityProcessOptions? options = null)
    {
        if (string.IsNullOrEmpty(modulePath)) throw new ArgumentException("Missing UtilityProcess entry script.", nameof(modulePath));
        options ??= new UtilityProcessOptions();
        var (stdout, stderr) = options.Stdio?.Resolve() ?? (StdioMode.Inherit, StdioMode.Inherit);
        if (RiftwayApp.CurrentOrNull is not { IsReady: true }) throw new InvalidOperationException("utilityProcess cannot be created before app is ready.");

        var child = new UtilityProcess();
        child.Launch(modulePath, args ?? [], options, stdout, stderr);
        return child;
    }

    /// <summary>Sends <paramref name="message"/> to the child's <see cref="ParentPort.Message"/>, moving
    /// <paramref name="transfer"/> there. Does nothing once the child has exited. Electron's <c>child.postMessage()</c>.</summary>
    /// <exception cref="InvalidOperationException">A port to move was already moved.</exception>
    public void PostMessage(JsonNode? message, IReadOnlyList<MessagePortMain>? transfer = null)
    {
        if (_wire is not { } wire || _exited) return;
        var data = message?.ToJsonString() ?? "null";
        var ports = PortLink.CheckTransfer(transfer, null);
        PortThread.Run(() =>
        {
            if (_terminated) foreach (var port in ports) port.Link?.Close(port);
            else wire.SendMessage(data, ports);
        });
    }

    /// <summary>Ends the child: TerminateProcess with exit code 0 on Windows, SIGTERM (then SIGKILL after 2 s) on POSIX;
    /// <see cref="Exit"/> then reports code 0. Electron's <c>child.kill()</c>.</summary>
    /// <returns>Whether the child was signalled; false before <see cref="Spawn"/> and after <see cref="Exit"/>.</returns>
    public bool Kill()
    {
        if (Volatile.Read(ref _pid) == 0 || _process is not { } process) return false;
        var result = Terminate(process);
        _killed = result;
        return result;
    }

    /// <summary>Kills every utility process still running: Chromium terminates its child processes at shutdown.</summary>
    internal static void TerminateAll()
    {
        UtilityProcess[] live;
        lock (s_gate) live = [.. s_live];
        foreach (var child in live)
        {
            child._wire?.Close();
            if (child._process is { } process) Terminate(process);
        }
    }

    private void Launch(string modulePath, IReadOnlyList<string> args, UtilityProcessOptions options, StdioMode stdout, StdioMode stderr)
    {
        lock (s_gate) s_live.Add(this);
        var full = Path.GetFullPath(modulePath, options.Cwd is { Length: > 0 } cwd ? Path.GetFullPath(cwd) : Environment.CurrentDirectory);
        var launch = UtilityLaunch.Resolve(full, args, options.ExecArgv ?? [], Environment.ProcessPath,
            RuntimeEnvironment.GetRuntimeDirectory(), OperatingSystem.IsWindows(), File.Exists);

        var name = PipeName("riftway-up-" + RandomNumberGenerator.GetHexString(16, lowercase: true), OperatingSystem.IsWindows(), Path.GetTempPath());
        var token = RandomNumberGenerator.GetHexString(32, lowercase: true);
        NamedPipeServerStream? server = null;
        try
        {
            if (!File.Exists(full)) throw new FileNotFoundException("The utility process module does not exist.", full);
            server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            var info = new ProcessStartInfo(launch.FileName)
            {
                UseShellExecute = false,
                // A console child of a GUI app would otherwise open a console window; Electron's utility processes have none.
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = stdout != StdioMode.Inherit,
                RedirectStandardError = stderr != StdioMode.Inherit,
                WorkingDirectory = options.Cwd ?? string.Empty,
            };
            foreach (var arg in launch.Arguments) info.ArgumentList.Add(arg);
            if (options.Env is { Count: > 0 } env)
            {
                info.Environment.Clear();
                foreach (var (key, value) in env) info.Environment[key] = value;
            }
            info.Environment[PipeVariable] = name;
            info.Environment[TokenVariable] = token;

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.Exited += (_, _) => _ = OnProcessExitedAsync(process);
            process.Start();
            _process = process;
            process.StandardInput.Close();
            Stdout = Attach(() => process.StandardOutput.BaseStream, stdout);
            Stderr = Attach(() => process.StandardError.BaseStream, stderr);

            _wire = new PortWire(AcceptAsync(server, token, _acceptCancel.Token))
            {
                Message = (data, _) => Raise(Message, data, "message"),
                Fatal = (location, report) => Raise(Error, new UtilityProcessErrorEventArgs("FatalError", location, report), "error"),
            };
            _wire.Start();
            var pid = process.Id;
            PortThread.Post(() =>
            {
                if (_terminated) return;
                Volatile.Write(ref _pid, pid);
                Raise(Spawn, EventArgs.Empty, "spawn");
            });
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            server?.Dispose();
            Console.Error.WriteLine($"[Riftway] Utility process {modulePath} failed to launch: {e.Message}");
            // Electron's stream exists from fork() and ends empty at exit.
            if (stdout == StdioMode.Pipe) Stdout = Stream.Null;
            if (stderr == StdioMode.Pipe) Stderr = Stream.Null;
            PortThread.Post(() => HandleTermination(LaunchFailureExitCode));
        }
        finally
        {
            _launched.TrySetResult();
        }
    }

    // 'ignore' is read and dropped: .NET cannot hand the child the null device, and an unread pipe would block it.
    private static Stream? Attach(Func<Stream> stream, StdioMode mode)
    {
        if (mode == StdioMode.Pipe) return stream();
        if (mode == StdioMode.Ignore) _ = stream().CopyToAsync(Stream.Null);
        return null;
    }

    private static async Task<Stream> AcceptAsync(NamedPipeServerStream server, string token, CancellationToken cancellationToken)
    {
        try
        {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            // Only the child holds the token: another process of this user that found the pipe first is refused.
            var hello = await WireFrame.ReadAsync(server, cancellationToken).ConfigureAwait(false);
            if (hello is null || WireFrame.Decode(hello) is not { Type: "hello" } frame || frame.Token != token)
                throw new InvalidDataException("A utility process connected without its token.");
            return server;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task OnProcessExitedAsync(Process process)
    {
        await _launched.Task.ConfigureAwait(false);
        var code = process.ExitCode;
        await _acceptCancel.CancelAsync().ConfigureAwait(false);
        // What the child wrote before it died (its last messages, a fatal report) reaches listeners before exit.
        if (_wire is { } wire)
        {
            try { await wire.ReadCompletion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        PortThread.Post(() => HandleTermination(UtilityLaunch.ExitCode(code, _killed, OperatingSystem.IsWindows())));
    }

    // Port thread. Electron's HandleTermination: once, pid cleared before exit, streams after.
    private void HandleTermination(int code)
    {
        if (_terminated) return;
        _terminated = true;
        _exited = true;
        Volatile.Write(ref _pid, 0);
        lock (s_gate) s_live.Remove(this);
        _wire?.Close();
        try { Raise(Exit, new UtilityProcessExitEventArgs(code), "exit"); }
        finally
        {
            Stdout = null;
            Stderr = null;
        }
    }

    private void Raise<T>(EventHandler<T>? handler, T args, string name)
    {
        try { handler?.Invoke(this, args); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] A UtilityProcess '{name}' listener threw: {e}"); }
    }

    private void Raise(EventHandler? handler, EventArgs args, string name)
    {
        try { handler?.Invoke(this, args); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] A UtilityProcess '{name}' listener threw: {e}"); }
    }

    private static bool Terminate(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return TerminateProcess(process.SafeHandle, 0);
            int result;
            try { result = SysKill(process.Id, Sigterm); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // No libc to send SIGTERM through: SIGKILL, which ExitCode still reports as 0 for a kill.
                process.Kill();
                return true;
            }
            if (result != 0) return false;
            _ = EnsureTerminatedAsync(process);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    // Chromium's EnsureProcessTerminated: SIGKILL a child that ignores SIGTERM for 2 s.
    private static async Task EnsureTerminatedAsync(Process process)
    {
        try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(); }
            catch (InvalidOperationException) { }
        }
    }

    private const int Sigterm = 15;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SysKill(int pid, int signal);
}
