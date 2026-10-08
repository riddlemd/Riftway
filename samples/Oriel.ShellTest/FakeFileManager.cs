using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>A stand-in <c>org.freedesktop.FileManager1</c> on the session bus that records <c>ShowItems</c> calls and
/// answers them, or refuses them while <see cref="Fails"/> is set.</summary>
sealed unsafe partial class FakeFileManager : IDisposable
{
    private const string LibDBus = "libdbus-1.so.3";
    private const int TypeString = 's', TypeArray = 'a', MessageMethodCall = 1;

    private readonly nint _connection;
    private readonly Thread _thread;
    private volatile bool _stop;

    /// <summary>Each <c>ShowItems</c>: its URIs and startup id.</summary>
    public ConcurrentQueue<(string[] Uris, string StartupId)> ShowItems { get; } = new();

    /// <summary><c>ShowItems</c> answers with an error.</summary>
    public bool Fails { get; set; }

    private FakeFileManager(nint connection)
    {
        _connection = connection;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Fake file manager" };
        _thread.Start();
    }

    /// <summary>Owns <paramref name="name"/>; null without a session bus or when the name is taken.</summary>
    public static FakeFileManager? Start(string name)
    {
        if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is not { Length: > 0 }) return null;
        var error = stackalloc long[8];
        dbus_error_init(error);
        var connection = dbus_bus_get_private(0, error);
        dbus_error_free(error);
        if (connection == 0) return null;
        dbus_connection_set_exit_on_disconnect(connection, 0);
        int owned;
        dbus_error_init(error);
        fixed (byte* n = Encoding.UTF8.GetBytes(name + "\0")) owned = dbus_bus_request_name(connection, n, 4 /* DO_NOT_QUEUE */, error);
        dbus_error_free(error);
        return owned == 1 ? new FakeFileManager(connection) : null;
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
            dbus_connection_flush(_connection);
        }
    }

    private void Answer(nint call)
    {
        var iface = Marshal.PtrToStringUTF8((nint)dbus_message_get_interface(call));
        var member = Marshal.PtrToStringUTF8((nint)dbus_message_get_member(call));
        if (iface != "org.freedesktop.FileManager1" || member != "ShowItems") return;
        var uris = new List<string>();
        var startup = "";
        var it = stackalloc long[16];
        var element = stackalloc long[16];
        if (dbus_message_iter_init(call, it) != 0 && dbus_message_iter_get_arg_type(it) == TypeArray)
        {
            dbus_message_iter_recurse(it, element);
            while (dbus_message_iter_get_arg_type(element) == TypeString)
            {
                byte* s;
                dbus_message_iter_get_basic(element, &s);
                uris.Add(Marshal.PtrToStringUTF8((nint)s)!);
                _ = dbus_message_iter_next(element);
            }
            if (dbus_message_iter_next(it) != 0 && dbus_message_iter_get_arg_type(it) == TypeString)
            {
                byte* s;
                dbus_message_iter_get_basic(it, &s);
                startup = Marshal.PtrToStringUTF8((nint)s)!;
            }
        }
        ShowItems.Enqueue(([.. uris], startup));

        nint reply;
        if (Fails)
            fixed (byte* name = "org.freedesktop.DBus.Error.Failed\0"u8)
            fixed (byte* text = "Refused for the test\0"u8)
                reply = dbus_message_new_error(call, name, text);
        else reply = dbus_message_new_method_return(call);
        uint serial;
        _ = dbus_connection_send(_connection, reply, &serial);
        dbus_message_unref(reply);
    }

    [LibraryImport(LibDBus)] private static partial void dbus_error_init(void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_error_free(void* error);
    [LibraryImport(LibDBus)] private static partial nint dbus_bus_get_private(int type, void* error);
    [LibraryImport(LibDBus)] private static partial int dbus_bus_request_name(nint connection, byte* name, uint flags, void* error);
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
    [LibraryImport(LibDBus)] private static partial void dbus_message_unref(nint message);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_init(nint message, void* iter);
    [LibraryImport(LibDBus)] private static partial int dbus_message_iter_get_arg_type(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_get_basic(void* iter, void* value);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_next(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_recurse(void* iter, void* sub);
}
