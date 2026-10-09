using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Oriel.Samples;

/// <summary>A Notify call the fake server received, as read off the wire.</summary>
internal sealed record FakeNotify(uint Id, string AppName, uint ReplacesId, string AppIcon, string Summary, string Body,
    List<string> Actions, List<(string Key, FakeVariant Value)> Hints, int Timeout);

/// <summary>A stand-in notification server: owns <c>org.freedesktop.Notifications</c> on the session bus, answers
/// <c>GetServerInformation</c>, <c>GetCapabilities</c>, <c>Notify</c> and <c>CloseNotification</c> as the spec says,
/// records each call, and emits the server's signals on demand.</summary>
/// <remarks>Its own libdbus connection, thread and reader, driven by the wire's type codes rather than by the signature
/// Oriel writes with, so the two do not share a mistake.</remarks>
internal sealed unsafe partial class FakeNotificationServer : IDisposable
{
    private const string LibDBus = "libdbus-1.so.3";
    internal const string Name = "org.freedesktop.Notifications", ObjectPath = "/org/freedesktop/Notifications";
    private const int MessageMethodCall = 1;

    private readonly nint _connection;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _work = new();
    private volatile bool _stop;
    private uint _nextId;

    /// <summary>The capabilities <c>GetCapabilities</c> answers.</summary>
    public string[] Capabilities { get; set; } = ["actions", "body", "append"];

    /// <summary>The spec version <c>GetServerInformation</c> answers.</summary>
    public string SpecVersion { get; set; } = "1.2";

    /// <summary>Answer <c>Notify</c> with an error, as a server refusing it would.</summary>
    public bool RefuseNotify { get; set; }

    /// <summary>Every method called, by member name, in order.</summary>
    public ConcurrentQueue<string> Methods { get; } = new();

    /// <summary>Each <c>Notify</c>, with the id answered.</summary>
    public ConcurrentQueue<FakeNotify> Notifies { get; } = new();

    /// <summary>Each id <c>CloseNotification</c> was called with.</summary>
    public ConcurrentQueue<uint> Closes { get; } = new();

    private FakeNotificationServer(nint connection)
    {
        _connection = connection;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Fake notification server" };
        _thread.Start();
    }

    /// <summary>Owns the server's name on the session bus; null without a bus or when the name is taken.</summary>
    public static FakeNotificationServer? Start()
    {
        var error = stackalloc long[8];
        dbus_error_init(error);
        var connection = dbus_bus_get_private(0, error);
        dbus_error_free(error);
        if (connection == 0) return null;
        dbus_connection_set_exit_on_disconnect(connection, 0);
        dbus_error_init(error);
        int owned;
        // DBUS_NAME_FLAG_DO_NOT_QUEUE; 1 is DBUS_REQUEST_NAME_REPLY_PRIMARY_OWNER.
        fixed (byte* n = Utf8(Name)) owned = dbus_bus_request_name(connection, n, 4, error);
        dbus_error_free(error);
        if (owned != 1)
        {
            dbus_connection_close(connection);
            return null;
        }
        return new FakeNotificationServer(connection);
    }

    /// <summary><c>ActionInvoked(id, key)</c>.</summary>
    public void ActionInvoked(uint id, string key) => Emit("ActionInvoked", id, key);

    /// <summary><c>NotificationClosed(id, reason)</c>.</summary>
    public void NotificationClosed(uint id, uint reason) => Emit("NotificationClosed", id, reason);

    /// <summary><c>NotificationReplied(id, text)</c>, which KDE and GNOME servers send for an inline reply.</summary>
    public void NotificationReplied(uint id, string text) => Emit("NotificationReplied", id, text);

    /// <summary><c>ActivationToken(id, token)</c>.</summary>
    public void ActivationToken(uint id, string token) => Emit("ActivationToken", id, token);

    private void Emit(string member, uint id, object second) => _work.Enqueue(() =>
    {
        nint signal;
        fixed (byte* p = Utf8(ObjectPath))
        fixed (byte* i = Utf8(Name))
        fixed (byte* m = Utf8(member))
            signal = dbus_message_new_signal(p, i, m);
        var iter = stackalloc long[16];
        dbus_message_iter_init_append(signal, iter);
        var first = id;
        _ = dbus_message_iter_append_basic(iter, 'u', &first);
        if (second is uint reason) _ = dbus_message_iter_append_basic(iter, 'u', &reason);
        else AppendString(iter, (string)second);
        uint serial;
        _ = dbus_connection_send(_connection, signal, &serial);
        dbus_message_unref(signal);
    });

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
                try
                {
                    if (dbus_message_get_type(message) == MessageMethodCall) Answer(message);
                }
                catch (Exception e) { Console.Error.WriteLine($"Fake notification server: {e}"); }
                finally { dbus_message_unref(message); }
            }
            while (_work.TryDequeue(out var work)) work();
            dbus_connection_flush(_connection);
        }
    }

    private void Answer(nint message)
    {
        var iface = Str(dbus_message_get_interface(message));
        var member = Str(dbus_message_get_member(message));
        var args = ReadAll(message);
        if (iface != Name)
        {
            Reply(Error(message, "org.freedesktop.DBus.Error.UnknownMethod"));
            return;
        }
        Methods.Enqueue(member);
        var reply = dbus_message_new_method_return(message);
        var iter = stackalloc long[16];
        dbus_message_iter_init_append(reply, iter);
        switch (member, args)
        {
            case ("GetServerInformation", []):
                foreach (var s in new[] { "Fake", "Oriel", "1", SpecVersion }) AppendString(iter, s);
                break;
            case ("GetCapabilities", []):
            {
                var sub = stackalloc long[16];
                fixed (byte* s = "s\0"u8) _ = dbus_message_iter_open_container(iter, 'a', s, sub);
                foreach (var cap in Capabilities) AppendString(sub, cap);
                _ = dbus_message_iter_close_container(iter, sub);
                break;
            }
            case ("Notify", [string app, uint replaces, string icon, string summary, string body, List<object?> actions, List<object?> hints, int timeout]):
            {
                if (RefuseNotify)
                {
                    dbus_message_unref(reply);
                    Reply(Error(message, "org.freedesktop.DBus.Error.Failed"));
                    return;
                }
                var id = replaces != 0 ? replaces : ++_nextId;
                Notifies.Enqueue(new FakeNotify(id, app, replaces, icon, summary, body, [.. actions.Cast<string>()],
                    [.. hints.Cast<KeyValuePair<object?, object?>>().Select(e => ((string)e.Key!, (FakeVariant)e.Value!))], timeout));
                _ = dbus_message_iter_append_basic(iter, 'u', &id);
                break;
            }
            case ("CloseNotification", [uint id]):
                Closes.Enqueue(id);
                // A server closing on request reports it with reason 3.
                _work.Enqueue(() => NotificationClosed(id, 3));
                break;
            default:
                dbus_message_unref(reply);
                Reply(Error(message, "org.freedesktop.DBus.Error.InvalidArgs"));
                return;
        }
        Reply(reply);
    }

    private void Reply(nint reply)
    {
        uint serial;
        _ = dbus_connection_send(_connection, reply, &serial);
        dbus_message_unref(reply);
    }

    private static nint Error(nint message, string name)
    {
        fixed (byte* n = Utf8(name))
        fixed (byte* t = "Not the fake's\0"u8)
            return dbus_message_new_error(message, n, t);
    }

    private static void AppendString(void* iter, string value)
    {
        fixed (byte* b = Utf8(value))
        {
            var p = b;
            _ = dbus_message_iter_append_basic(iter, 's', &p);
        }
    }

    private static object?[] ReadAll(nint message)
    {
        var values = new List<object?>();
        var iter = stackalloc long[16];
        if (dbus_message_iter_init(message, iter) == 0) return [];
        do values.Add(Read(iter));
        while (dbus_message_iter_next(iter) != 0);
        return [.. values];
    }

    private static object? Read(void* iter)
    {
        var type = dbus_message_iter_get_arg_type(iter);
        switch (type)
        {
            case 'y': { byte v; dbus_message_iter_get_basic(iter, &v); return v; }
            case 'b': { uint v; dbus_message_iter_get_basic(iter, &v); return v != 0; }
            case 'i': { int v; dbus_message_iter_get_basic(iter, &v); return v; }
            case 'u': { uint v; dbus_message_iter_get_basic(iter, &v); return v; }
            case 'x': { long v; dbus_message_iter_get_basic(iter, &v); return v; }
            case 's' or 'o' or 'g': { byte* s; dbus_message_iter_get_basic(iter, &s); return Str(s); }
            case 'v':
            {
                var sub = stackalloc long[16];
                dbus_message_iter_recurse(iter, sub);
                var signature = dbus_message_iter_get_signature(sub);
                var text = Str(signature);
                dbus_free(signature);
                return new FakeVariant(text, Read(sub));
            }
            case 'a' or 'r' or 'e':
            {
                var sub = stackalloc long[16];
                dbus_message_iter_recurse(iter, sub);
                var items = new List<object?>();
                while (dbus_message_iter_get_arg_type(sub) != 0)
                {
                    items.Add(Read(sub));
                    _ = dbus_message_iter_next(sub);
                }
                if (type == 'a' && dbus_message_iter_get_element_type(iter) == 'y') return items.Select(b => (byte)b!).ToArray();
                if (type == 'e') return new KeyValuePair<object?, object?>(items[0], items[1]);
                return type == 'r' ? items.ToArray() : items;
            }
            default: return $"<type {(char)type}>";
        }
    }

    private static string Str(byte* s) => s == null ? "" : Marshal.PtrToStringUTF8((nint)s) ?? "";
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

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
    [LibraryImport(LibDBus)] private static partial int dbus_message_iter_get_element_type(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_get_basic(void* iter, void* value);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_next(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_recurse(void* iter, void* sub);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_iter_get_signature(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_free(void* memory);
}
