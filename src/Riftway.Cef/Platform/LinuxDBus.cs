using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>A received D-Bus signal: where it came from and its arguments (string, bool, uint32 or int32; anything
/// else is null).</summary>
public sealed record LinuxDBusSignal(string Path, string Interface, string Member, object?[] Args);

/// <summary>A private libdbus connection served by a thread of its own, as Chromium's D-Bus thread: work posted with
/// <see cref="Post"/>, signals matched with <see cref="AddMatch"/> and calls to exported objects run there, so blocking
/// calls are safe in all of them.</summary>
[SupportedOSPlatform("linux")]
public sealed unsafe partial class LinuxDBus
{
    private const string LibDBus = "libdbus-1.so.3";
    private const string LibC = "libc.so.6";
    private const int TypeString = 's', TypeBoolean = 'b', TypeUInt32 = 'u', TypeInt32 = 'i', TypeUnixFd = 'h';
    private const int MessageSignal = 4;
    // DBUS_TIMEOUT_USE_DEFAULT, as dbus::ObjectProxy::TIMEOUT_USE_DEFAULT.
    private const int DefaultTimeout = -1;
    private const short PollIn = 1;

    private readonly BlockingCollection<Action> _work = [];
    private readonly int _wakeRead, _wakeWrite;
    private nint _connection;
    private bool _connectTried;
    private readonly bool _system;
    private volatile bool _shutDown;
    private GCHandle _self;

    /// <summary>Raised on the bus thread for every signal the match rules let through.</summary>
    public event Action<LinuxDBusSignal>? Signal;

    private LinuxDBus(bool system, string name)
    {
        _system = system;
        var fds = stackalloc int[2];
        if (pipe(fds) != 0) throw new IOException("pipe() failed.");
        (_wakeRead, _wakeWrite) = (fds[0], fds[1]);
        new Thread(Loop) { IsBackground = true, Name = name }.Start();
    }

    /// <summary>A connection to the system bus (<paramref name="system"/>) or the session bus; it connects on its thread
    /// at the first work item, and a bus that cannot be reached leaves every call failing.</summary>
    public static LinuxDBus Open(bool system, string threadName) => new(system, threadName);

    /// <summary>Runs <paramref name="work"/> on the bus thread.</summary>
    public void Post(Action work)
    {
        _work.Add(work);
        byte b = 1;
        _ = write(_wakeWrite, &b, 1);
    }

    /// <summary>Closes the connection once the work already posted has run, and ends the thread; the bus then drops
    /// every name and object it held. Further work is not run.</summary>
    public void Shutdown() => Post(() => _shutDown = true);

    /// <summary>Whether the bus was reached; bus thread only.</summary>
    public bool Connected => Connection() != 0;

    /// <summary>Adds a match rule; false if the bus refused it. Bus thread only.</summary>
    public bool AddMatch(string rule)
    {
        var connection = Connection();
        if (connection == 0) return false;
        var error = stackalloc long[8];
        dbus_error_init(error);
        fixed (byte* r = Utf8(rule)) dbus_bus_add_match(connection, r, error);
        var failed = dbus_error_is_set(error) != 0;
        dbus_error_free(error);
        return !failed;
    }

    /// <summary><c>org.freedesktop.DBus.NameHasOwner</c>; null when the bus could not answer. Bus thread only.</summary>
    public bool? NameHasOwner(string name)
    {
        var connection = Connection();
        if (connection == 0) return null;
        var error = stackalloc long[8];
        dbus_error_init(error);
        uint owned;
        fixed (byte* n = Utf8(name)) owned = dbus_bus_name_has_owner(connection, n, error);
        var failed = dbus_error_is_set(error) != 0;
        dbus_error_free(error);
        return failed ? null : owned != 0;
    }

    /// <summary>Calls a method and waits for the reply; null on an error reply, a timeout or no bus. Arguments are strings
    /// and uint32s; the reply's arguments come back as in <see cref="LinuxDBusSignal.Args"/>, a Unix fd as a
    /// <see cref="LinuxFd"/> the caller owns. Bus thread only.</summary>
    public object?[]? Call(string destination, string path, string iface, string method, params object[] args)
    {
        var connection = Connection();
        if (connection == 0) return null;
        nint message;
        fixed (byte* d = Utf8(destination))
        fixed (byte* p = Utf8(path))
        fixed (byte* i = Utf8(iface))
        fixed (byte* m = Utf8(method))
            message = dbus_message_new_method_call(d, p, i, m);
        if (message == 0) return null;
        var iter = stackalloc long[16];
        dbus_message_iter_init_append(message, iter);
        foreach (var arg in args)
        {
            switch (arg)
            {
                case string s:
                    fixed (byte* bytes = Utf8(s))
                    {
                        var ptr = bytes;
                        _ = dbus_message_iter_append_basic(iter, TypeString, &ptr);
                    }
                    break;
                case uint u: _ = dbus_message_iter_append_basic(iter, TypeUInt32, &u); break;
                default: throw new ArgumentException($"No D-Bus type for {arg.GetType()}.", nameof(args));
            }
        }
        var error = stackalloc long[8];
        dbus_error_init(error);
        var reply = dbus_connection_send_with_reply_and_block(connection, message, DefaultTimeout, error);
        dbus_error_free(error);
        dbus_message_unref(message);
        if (reply == 0) return null;
        try { return ReadArgs(reply); }
        finally { dbus_message_unref(reply); }
    }

    private nint Connection()
    {
        if (_connection != 0 || _connectTried) return _connection;
        _connectTried = true;
        var error = stackalloc long[8];
        dbus_error_init(error);
        // DBUS_BUS_SESSION 0, DBUS_BUS_SYSTEM 1.
        var connection = dbus_bus_get_private(_system ? 1 : 0, error);
        dbus_error_free(error);
        if (connection == 0) return 0;
        dbus_connection_set_exit_on_disconnect(connection, 0);
        _self = GCHandle.Alloc(this);
        _ = dbus_connection_add_filter(connection, &OnFilter, GCHandle.ToIntPtr(_self), null);
        _connection = connection;
        return connection;
    }

    private void Loop()
    {
        try
        {
            var fds = stackalloc int[4];
            var buffer = stackalloc byte[64];
            while (true)
            {
                while (_work.TryTake(out var work))
                {
                    try { work(); }
                    catch (Exception e) when (e is not OutOfMemoryException) { Console.Error.WriteLine($"Riftway D-Bus: {e}"); }
                }
                if (_shutDown)
                {
                    Close();
                    return;
                }
                Drain();
                if (_work.Count > 0) continue;
                // pollfd is { int fd; short events; short revents; }.
                int busFd = -1;
                if (_connection != 0) _ = dbus_connection_get_unix_fd(_connection, &busFd);
                fds[0] = _wakeRead;
                fds[1] = PollIn;
                fds[2] = busFd;
                fds[3] = PollIn;
                _ = poll(fds, busFd >= 0 ? 2u : 1u, PollTimeout());
                if ((((short*)fds)[3] & PollIn) != 0)
                    _ = read(_wakeRead, buffer, 64);
                if (_connection != 0 && dbus_connection_read_write(_connection, 0) == 0)
                {
                    // Disconnected: work still runs, against a bus that answers nothing.
                    Close();
                }
                ExpirePendingCalls();
            }
        }
        catch (DllNotFoundException) { }
    }

    // libdbus routes each message: signals through the filter, calls to the exported objects, and an UnknownMethod
    // error for any call nothing takes, as it does for Chromium's bus.
    private void Drain()
    {
        if (_connection == 0) return;
        // DBUS_DISPATCH_DATA_REMAINS.
        while (dbus_connection_dispatch(_connection) == 0) { }
        dbus_connection_flush(_connection);
    }

    private void Close()
    {
        if (_connection == 0) return;
        AbandonPendingCalls();
        dbus_connection_close(_connection);
        dbus_connection_unref(_connection);
        _connection = 0;
        if (_self.IsAllocated) _self.Free();
    }

    [UnmanagedCallersOnly]
    private static int OnFilter(nint connection, nint message, nint data)
    {
        try
        {
            if (dbus_message_get_type(message) == MessageSignal && GCHandle.FromIntPtr(data).Target is LinuxDBus bus)
            {
                var signal = new LinuxDBusSignal(Str(dbus_message_get_path(message)), Str(dbus_message_get_interface(message)), Str(dbus_message_get_member(message)), ReadArgs(message));
                bus.Signal?.Invoke(signal);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Console.Error.WriteLine($"Riftway D-Bus: {e}"); }
        // DBUS_HANDLER_RESULT_NOT_YET_HANDLED: the exported objects and libdbus's own replies still see it.
        return 1;
    }

    private static string Str(byte* s) => s == null ? "" : Marshal.PtrToStringUTF8((nint)s) ?? "";

    private static object?[] ReadArgs(nint message)
    {
        var args = new List<object?>();
        var iter = stackalloc long[16];
        if (dbus_message_iter_init(message, iter) == 0) return [];
        do
        {
            switch (dbus_message_iter_get_arg_type(iter))
            {
                case TypeString:
                    byte* s;
                    dbus_message_iter_get_basic(iter, &s);
                    args.Add(Str(s));
                    break;
                case TypeBoolean:
                    uint b;
                    dbus_message_iter_get_basic(iter, &b);
                    args.Add(b != 0);
                    break;
                case TypeUInt32:
                    uint u;
                    dbus_message_iter_get_basic(iter, &u);
                    args.Add(u);
                    break;
                case TypeInt32:
                    int i;
                    dbus_message_iter_get_basic(iter, &i);
                    args.Add(i);
                    break;
                case TypeUnixFd:
                    // libdbus hands out a duplicate the caller must close.
                    int fd;
                    dbus_message_iter_get_basic(iter, &fd);
                    args.Add(new LinuxFd(fd));
                    break;
                default:
                    args.Add(null);
                    break;
            }
        } while (dbus_message_iter_next(iter) != 0);
        return [.. args];
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    [LibraryImport(LibC)] private static partial int pipe(int* fds);
    [LibraryImport(LibC)] private static partial nint write(int fd, void* buffer, nint count);
    [LibraryImport(LibC)] private static partial nint read(int fd, void* buffer, nint count);
    [LibraryImport(LibC)] private static partial int poll(int* fds, uint count, int timeout);
    [LibraryImport(LibDBus)] private static partial void dbus_error_init(void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_error_free(void* error);
    [LibraryImport(LibDBus)] private static partial uint dbus_error_is_set(void* error);
    [LibraryImport(LibDBus)] private static partial nint dbus_bus_get_private(int type, void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_bus_add_match(nint connection, byte* rule, void* error);
    [LibraryImport(LibDBus)] private static partial uint dbus_bus_name_has_owner(nint connection, byte* name, void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_set_exit_on_disconnect(nint connection, uint exit);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_get_unix_fd(nint connection, int* fd);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_close(nint connection);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_unref(nint connection);
    [LibraryImport(LibDBus)] private static partial nint dbus_connection_send_with_reply_and_block(nint connection, nint message, int timeout, void* error);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_read_write(nint connection, int timeout);
    [LibraryImport(LibDBus)] private static partial int dbus_connection_dispatch(nint connection);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_flush(nint connection);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_add_filter(nint connection, delegate* unmanaged<nint, nint, nint, int> function, nint data, void* free);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_method_call(byte* destination, byte* path, byte* iface, byte* method);
    [LibraryImport(LibDBus)] private static partial int dbus_message_get_type(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_path(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_interface(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_member(nint message);
    [LibraryImport(LibDBus)] private static partial void dbus_message_unref(nint message);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_init(nint message, void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_init_append(nint message, void* iter);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_append_basic(void* iter, int type, void* value);
    [LibraryImport(LibDBus)] private static partial int dbus_message_iter_get_arg_type(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_get_basic(void* iter, void* value);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_next(void* iter);
}

/// <summary>A file descriptor received over D-Bus; closing it releases what it stands for (a logind inhibitor).</summary>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxFd(int fd) : IDisposable
{
    private int _fd = fd;

    public bool IsValid => _fd >= 0;

    public void Dispose()
    {
        if (_fd < 0) return;
        _ = close(_fd);
        _fd = -1;
    }

    [LibraryImport("libc.so.6")] private static partial int close(int fd);
}
