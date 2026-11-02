using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Riftway.Samples;

/// <summary>A method call a <see cref="FakeDBusService"/> received: interface, member and its string, uint32, int32 and
/// boolean arguments.</summary>
internal sealed record FakeDBusCall(string Interface, string Member, object?[] Args)
{
    public override string ToString() => $"{Member}({string.Join(", ", Args.Select(a => a is string s ? $"'{s}'" : a))})";
}

/// <summary>A stand-in system service (logind, a session manager, a screen saver) for the power checks: owns well-known
/// names on the session or system bus, records every method call, answers through <see cref="Handler"/>, and emits
/// signals.</summary>
/// <remarks>Its own libdbus connection and thread. A <see cref="FakeDBusLock"/> in a reply goes out as a Unix fd, as
/// logind's <c>Inhibit</c> returns one; the fake keeps the other end of the socket to see when the client closes it.</remarks>
internal sealed unsafe partial class FakeDBusService : IDisposable
{
    private const string LibDBus = "libdbus-1.so.3";
    private const int TypeString = 's', TypeUInt32 = 'u', TypeInt32 = 'i', TypeBoolean = 'b', TypeUnixFd = 'h';
    private const int MessageMethodCall = 1;

    private readonly nint _connection;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _work = new();
    private volatile bool _stop;

    /// <summary>Method calls received, in order.</summary>
    public ConcurrentQueue<FakeDBusCall> Calls { get; } = new();

    /// <summary>The reply's arguments for a call (string, uint, int, bool or <see cref="FakeDBusLock"/>), or null for an
    /// error reply. Runs on the fake's thread; the default answers every call with no arguments.</summary>
    public Func<FakeDBusCall, object[]?> Handler { get; set; } = _ => [];

    private FakeDBusService(nint connection, Func<FakeDBusCall, object[]?>? handler)
    {
        _connection = connection;
        if (handler is not null) Handler = handler;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Fake D-Bus service" };
        _thread.Start();
    }

    /// <summary>Owns <paramref name="names"/> on the system bus (<paramref name="system"/>) or the session bus, answering
    /// with <paramref name="handler"/> from the first call on; null when that bus cannot be reached or a name is taken.</summary>
    public static FakeDBusService? Start(bool system, Func<FakeDBusCall, object[]?>? handler, params string[] names)
    {
        var error = stackalloc long[8];
        dbus_error_init(error);
        var connection = dbus_bus_get_private(system ? 1 : 0, error);
        dbus_error_free(error);
        if (connection == 0) return null;
        dbus_connection_set_exit_on_disconnect(connection, 0);
        foreach (var name in names)
        {
            int owned;
            dbus_error_init(error);
            // DBUS_NAME_FLAG_DO_NOT_QUEUE; 1 is DBUS_REQUEST_NAME_REPLY_PRIMARY_OWNER.
            fixed (byte* n = Utf8(name)) owned = dbus_bus_request_name(connection, n, 4, error);
            dbus_error_free(error);
            if (owned != 1)
            {
                dbus_connection_close(connection);
                return null;
            }
        }
        return new FakeDBusService(connection, handler);
    }

    /// <summary>Emits a signal from the service's connection, after any call already being answered.</summary>
    public void Emit(string path, string iface, string member, params object[] args) => _work.Enqueue(() =>
    {
        nint signal;
        fixed (byte* p = Utf8(path))
        fixed (byte* i = Utf8(iface))
        fixed (byte* m = Utf8(member))
            signal = dbus_message_new_signal(p, i, m);
        Append(signal, args);
        uint serial;
        _ = dbus_connection_send(_connection, signal, &serial);
        dbus_message_unref(signal);
    });

    /// <summary>Gives up a name, so it has no owner on the bus.</summary>
    public void Release(string name)
    {
        using var done = new ManualResetEventSlim();
        _work.Enqueue(() =>
        {
            var error = stackalloc long[8];
            dbus_error_init(error);
            fixed (byte* n = Utf8(name)) _ = dbus_bus_release_name(_connection, n, error);
            dbus_error_free(error);
            done.Set();
        });
        done.Wait(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
        dbus_connection_close(_connection);
    }

    private void Loop()
    {
        while (!_stop)
        {
            _ = dbus_connection_read_write(_connection, 20);
            nint message;
            while ((message = dbus_connection_pop_message(_connection)) != 0)
            {
                if (dbus_message_get_type(message) == MessageMethodCall) Answer(message);
                dbus_message_unref(message);
            }
            while (_work.TryDequeue(out var work)) work();
            dbus_connection_flush(_connection);
        }
    }

    private void Answer(nint message)
    {
        var call = new FakeDBusCall(Str(dbus_message_get_interface(message)), Str(dbus_message_get_member(message)), ReadArgs(message));
        Calls.Enqueue(call);
        object[]? result;
        try { result = Handler(call); }
        catch (Exception e) { Console.Error.WriteLine($"Fake D-Bus service: {e}"); result = null; }
        nint reply;
        if (result is null)
            fixed (byte* name = "org.freedesktop.DBus.Error.Failed\0"u8)
            fixed (byte* text = "Refused by the fake\0"u8)
                reply = dbus_message_new_error(message, name, text);
        else
        {
            reply = dbus_message_new_method_return(message);
            Append(reply, result);
        }
        uint serial;
        _ = dbus_connection_send(_connection, reply, &serial);
        dbus_message_unref(reply);
        // libdbus duplicated the fd into the message; the client's copy is now the only one at that end.
        foreach (var arg in result ?? []) if (arg is FakeDBusLock l) l.CloseSentEnd();
    }

    private static void Append(nint message, object[] args)
    {
        var iter = stackalloc long[16];
        dbus_message_iter_init_append(message, iter);
        foreach (var arg in args)
        {
            switch (arg)
            {
                case string s:
                    fixed (byte* bytes = Utf8(s))
                    {
                        var p = bytes;
                        _ = dbus_message_iter_append_basic(iter, TypeString, &p);
                    }
                    break;
                case uint u: _ = dbus_message_iter_append_basic(iter, TypeUInt32, &u); break;
                case int i: _ = dbus_message_iter_append_basic(iter, TypeInt32, &i); break;
                // dbus_bool_t is 32 bits.
                case bool b: { var v = b ? 1u : 0u; _ = dbus_message_iter_append_basic(iter, TypeBoolean, &v); break; }
                case FakeDBusLock l: { var fd = l.SentEnd; _ = dbus_message_iter_append_basic(iter, TypeUnixFd, &fd); break; }
                default: throw new ArgumentException($"No D-Bus type for {arg.GetType()}.", nameof(args));
            }
        }
    }

    private static object?[] ReadArgs(nint message)
    {
        var args = new List<object?>();
        var iter = stackalloc long[16];
        if (dbus_message_iter_init(message, iter) == 0) return [];
        do
        {
            switch (dbus_message_iter_get_arg_type(iter))
            {
                case TypeString: { byte* s; dbus_message_iter_get_basic(iter, &s); args.Add(Str(s)); break; }
                case TypeUInt32: { uint u; dbus_message_iter_get_basic(iter, &u); args.Add(u); break; }
                case TypeInt32: { int i; dbus_message_iter_get_basic(iter, &i); args.Add(i); break; }
                case TypeBoolean: { uint b; dbus_message_iter_get_basic(iter, &b); args.Add(b != 0); break; }
                default: args.Add(null); break;
            }
        } while (dbus_message_iter_next(iter) != 0);
        return [.. args];
    }

    private static string Str(byte* s) => s == null ? "" : Marshal.PtrToStringUTF8((nint)s) ?? "";
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    [LibraryImport(LibDBus)] private static partial void dbus_error_init(void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_error_free(void* error);
    [LibraryImport(LibDBus)] private static partial nint dbus_bus_get_private(int type, void* error);
    [LibraryImport(LibDBus)] private static partial int dbus_bus_request_name(nint connection, byte* name, uint flags, void* error);
    [LibraryImport(LibDBus)] private static partial int dbus_bus_release_name(nint connection, byte* name, void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_set_exit_on_disconnect(nint connection, uint exit);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_close(nint connection);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_read_write(nint connection, int timeout);
    [LibraryImport(LibDBus)] private static partial nint dbus_connection_pop_message(nint connection);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_send(nint connection, nint message, uint* serial);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_flush(nint connection);
    [LibraryImport(LibDBus)] private static partial int dbus_message_get_type(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_interface(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_member(nint message);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_method_return(nint call);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_error(nint call, byte* name, byte* message);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_signal(byte* path, byte* iface, byte* name);
    [LibraryImport(LibDBus)] private static partial void dbus_message_unref(nint message);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_init(nint message, void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_init_append(nint message, void* iter);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_append_basic(void* iter, int type, void* value);
    [LibraryImport(LibDBus)] private static partial int dbus_message_iter_get_arg_type(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_get_basic(void* iter, void* value);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_next(void* iter);
}

/// <summary>An inhibitor fd as logind hands it out: one end of a socket pair goes to the client; the end kept here sees
/// the client close it.</summary>
internal sealed unsafe partial class FakeDBusLock
{
    private readonly int _kept;

    public int SentEnd { get; private set; }

    public FakeDBusLock()
    {
        var fds = stackalloc int[2];
        // AF_UNIX, SOCK_STREAM.
        if (socketpair(1, 1, 0, fds) != 0) throw new IOException("socketpair() failed.");
        (SentEnd, _kept) = (fds[0], fds[1]);
    }

    internal void CloseSentEnd()
    {
        if (SentEnd < 0) return;
        _ = close(SentEnd);
        SentEnd = -1;
    }

    /// <summary>Whether every copy of the client's end is closed: the kept end reads end of file.</summary>
    public bool Released
    {
        get
        {
            // pollfd { int fd; short events; short revents; }: POLLIN 1, POLLHUP 0x10.
            var pfd = stackalloc int[2];
            pfd[0] = _kept;
            pfd[1] = 1;
            if (poll(pfd, 1, 0) <= 0) return false;
            var revents = ((short*)pfd)[3];
            if ((revents & 0x10) != 0) return true;
            byte b;
            // MSG_PEEK | MSG_DONTWAIT: 0 bytes is end of file.
            return (revents & 1) != 0 && recv(_kept, &b, 1, 0x2 | 0x40) == 0;
        }
    }

    [LibraryImport("libc.so.6")] private static partial int socketpair(int domain, int type, int protocol, int* fds);
    [LibraryImport("libc.so.6")] private static partial int close(int fd);
    [LibraryImport("libc.so.6")] private static partial int poll(int* fds, uint count, int timeout);
    [LibraryImport("libc.so.6")] private static partial nint recv(int fd, void* buffer, nint length, int flags);
}
