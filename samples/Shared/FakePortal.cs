using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Oriel.Samples;

/// <summary>A stand-in <c>xdg-desktop-portal</c> on the session bus for the appearance checks: owns
/// <c>org.freedesktop.portal.Desktop</c>, answers the FileChooser version, <c>Settings.Read</c>/<c>ReadOne</c> for
/// <c>org.freedesktop.appearance</c>, and emits <c>SettingChanged</c>. Chromium and Oriel in this process both see it.</summary>
/// <remarks>Its own libdbus connection and thread, separate from Oriel's portal client.</remarks>
internal sealed unsafe partial class FakePortal : IDisposable
{
    private const string LibDBus = "libdbus-1.so.3";
    private const string Namespace = "org.freedesktop.appearance";
    private const int TypeString = 's', TypeVariant = 'v', TypeUInt32 = 'u', TypeDouble = 'd', TypeStruct = 'r';
    private const int MessageMethodCall = 1;

    private readonly nint _connection;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<(string Key, object Value)> _emits = new();
    private readonly ConcurrentDictionary<string, object> _values = new();
    private volatile bool _stop;

    /// <summary>Method calls answered, as "Interface.Member"; for checks on what the clients asked.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    private FakePortal(nint connection, IEnumerable<(string Key, object Value)> initial)
    {
        _connection = connection;
        foreach (var (key, value) in initial) _values[key] = value;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Fake portal" };
        _thread.Start();
    }

    /// <summary>Owns the portal's name with <paramref name="initial"/> settings (uint32 or (r, g, b) doubles); null
    /// without a session bus or when the name is taken.</summary>
    public static FakePortal? Start(params (string Key, object Value)[] initial)
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
        // DBUS_NAME_FLAG_DO_NOT_QUEUE; 1 is DBUS_REQUEST_NAME_REPLY_PRIMARY_OWNER.
        fixed (byte* name = "org.freedesktop.portal.Desktop\0"u8) owned = dbus_bus_request_name(connection, name, 4, error);
        dbus_error_free(error);
        return owned == 1 ? new FakePortal(connection, initial) : null;
    }

    /// <summary>Changes a setting and emits <c>SettingChanged</c> for it.</summary>
    public void Set(string key, object value)
    {
        _values[key] = value;
        _emits.Enqueue((key, value));
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
            while (_emits.TryDequeue(out var emit)) Emit(emit.Key, emit.Value);
            dbus_connection_flush(_connection);
        }
    }

    private void Answer(nint call)
    {
        var iface = Marshal.PtrToStringUTF8((nint)dbus_message_get_interface(call)) ?? "";
        var member = Marshal.PtrToStringUTF8((nint)dbus_message_get_member(call)) ?? "";
        Calls.Enqueue(iface + "." + member);
        var args = new List<string>();
        var it = stackalloc long[16];
        if (dbus_message_iter_init(call, it) != 0)
            do
            {
                if (dbus_message_iter_get_arg_type(it) != TypeString) break;
                byte* s;
                dbus_message_iter_get_basic(it, &s);
                args.Add(Marshal.PtrToStringUTF8((nint)s)!);
            } while (dbus_message_iter_next(it) != 0);

        object? value = null;
        var wraps = 0;
        if (iface == "org.freedesktop.DBus.Properties" && member == "Get" && args is ["org.freedesktop.portal.FileChooser", "version"])
            // 1, not a current portal's 4: Chromium sends file dialogs to a FileChooser of version 3 or more, and this has none.
            (value, wraps) = (1u, 1);
        else if (iface == "org.freedesktop.portal.Settings" && args.Count == 2 && args[0] == Namespace && _values.TryGetValue(args[1], out var v))
            (value, wraps) = member switch { "Read" => (v, 2), "ReadOne" => (v, 1), _ => (null, 0) };
        else if (iface == "org.freedesktop.host.portal.Registry" && member == "Register")
            wraps = -1;

        nint reply;
        if (value is null && wraps != -1)
            fixed (byte* name = "org.freedesktop.portal.Error.NotFound\0"u8)
            fixed (byte* text = "Requested setting not found\0"u8)
                reply = dbus_message_new_error(call, name, text);
        else
        {
            reply = dbus_message_new_method_return(call);
            if (value is not null)
            {
                var top = stackalloc long[16];
                dbus_message_iter_init_append(reply, top);
                AppendWrapped(top, value, wraps);
            }
        }
        uint serial;
        _ = dbus_connection_send(_connection, reply, &serial);
        dbus_message_unref(reply);
    }

    private void Emit(string key, object value)
    {
        nint signal;
        fixed (byte* path = "/org/freedesktop/portal/desktop\0"u8)
        fixed (byte* iface = "org.freedesktop.portal.Settings\0"u8)
        fixed (byte* member = "SettingChanged\0"u8)
            signal = dbus_message_new_signal(path, iface, member);
        var top = stackalloc long[16];
        dbus_message_iter_init_append(signal, top);
        AppendString(top, Namespace);
        AppendString(top, key);
        AppendWrapped(top, value, 1);
        uint serial;
        _ = dbus_connection_send(_connection, signal, &serial);
        dbus_message_unref(signal);
    }

    // `value` inside `wraps` variants.
    private static void AppendWrapped(void* iter, object value, int wraps)
    {
        if (wraps == 0)
        {
            switch (value)
            {
                case uint u: _ = dbus_message_iter_append_basic(iter, TypeUInt32, &u); break;
                case ValueTuple<double, double, double> (var r, var g, var b):
                    var fields = stackalloc long[16];
                    _ = dbus_message_iter_open_container(iter, TypeStruct, null, fields);
                    foreach (var d in (double[])[r, g, b]) { var x = d; _ = dbus_message_iter_append_basic(fields, TypeDouble, &x); }
                    _ = dbus_message_iter_close_container(iter, fields);
                    break;
                default: throw new ArgumentException($"No D-Bus type for {value.GetType()}.", nameof(value));
            }
            return;
        }
        var signature = wraps > 1 ? "v" : value is uint ? "u" : "(ddd)";
        var inner = stackalloc long[16];
        fixed (byte* sig = Encoding.ASCII.GetBytes(signature + "\0")) _ = dbus_message_iter_open_container(iter, TypeVariant, sig, inner);
        AppendWrapped(inner, value, wraps - 1);
        _ = dbus_message_iter_close_container(iter, inner);
    }

    private static void AppendString(void* iter, string value)
    {
        fixed (byte* bytes = Encoding.UTF8.GetBytes(value + "\0"))
        {
            var p = bytes;
            _ = dbus_message_iter_append_basic(iter, TypeString, &p);
        }
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
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_signal(byte* path, byte* iface, byte* name);
    [LibraryImport(LibDBus)] private static partial void dbus_message_unref(nint message);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_init(nint message, void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_init_append(nint message, void* iter);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_append_basic(void* iter, int type, void* value);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_open_container(void* iter, int type, byte* signature, void* sub);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_close_container(void* iter, void* sub);
    [LibraryImport(LibDBus)] private static partial int dbus_message_iter_get_arg_type(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_get_basic(void* iter, void* value);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_next(void* iter);
}
