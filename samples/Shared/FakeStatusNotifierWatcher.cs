using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Riftway.Samples;

/// <summary>A D-Bus variant as the fake reads it.</summary>
internal sealed record FakeVariant(string Signature, object? Value);

/// <summary>A signal the fake saw on the bus.</summary>
internal sealed record FakeSignal(string Sender, string Path, string Interface, string Member, object?[] Args);

/// <summary>A stand-in StatusNotifierWatcher with a host, and a DBusMenu client: owns <c>org.kde.StatusNotifierWatcher</c>,
/// answers <c>IsStatusNotifierHostRegistered</c> and records <c>RegisterStatusNotifierItem</c>; calls the item and its
/// menu as a panel would and records every broadcast signal.</summary>
/// <remarks>Its own libdbus connection and thread, and its own reader, driven by the wire's type codes rather than by
/// the signatures Riftway writes with, so the two do not share a mistake.</remarks>
internal sealed unsafe partial class FakeStatusNotifierWatcher : IDisposable
{
    private const string LibDBus = "libdbus-1.so.3";
    private const string Name = "org.kde.StatusNotifierWatcher";
    private const int MessageMethodCall = 1, MessageSignal = 4;

    private readonly nint _connection;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _work = new();
    private volatile bool _stop;

    /// <summary>The value <c>IsStatusNotifierHostRegistered</c> answers.</summary>
    public bool HostRegistered { get; set; } = true;

    /// <summary>Leave <c>IsStatusNotifierHostRegistered</c> unanswered, as a hung watcher would.</summary>
    public bool IgnoreHostQuery { get; set; }

    /// <summary>Each <c>RegisterStatusNotifierItem</c>: the service named and the caller's unique name.</summary>
    public ConcurrentQueue<(string Service, string Sender)> Registrations { get; } = new();

    /// <summary>Broadcast signals seen, in order.</summary>
    public ConcurrentQueue<FakeSignal> Signals { get; } = new();

    /// <summary>The fake's unique name.</summary>
    public string UniqueName { get; }

    private FakeStatusNotifierWatcher(nint connection)
    {
        _connection = connection;
        UniqueName = Str(dbus_bus_get_unique_name(connection));
        _thread = new Thread(Loop) { IsBackground = true, Name = "Fake StatusNotifierWatcher" };
        _thread.Start();
    }

    /// <summary>Connects to the session bus, sees every broadcast signal and, when <paramref name="ownName"/>, owns the
    /// watcher's name; null without a bus or when the name is taken.</summary>
    public static FakeStatusNotifierWatcher? Start(bool ownName = true)
    {
        var error = stackalloc long[8];
        dbus_error_init(error);
        var connection = dbus_bus_get_private(0, error);
        dbus_error_free(error);
        if (connection == 0) return null;
        dbus_connection_set_exit_on_disconnect(connection, 0);
        dbus_error_init(error);
        fixed (byte* rule = "type='signal'\0"u8) dbus_bus_add_match(connection, rule, error);
        dbus_error_free(error);
        var fake = new FakeStatusNotifierWatcher(connection);
        if (ownName && !fake.Own())
        {
            fake.Dispose();
            return null;
        }
        return fake;
    }

    /// <summary>Takes the watcher's name; false if another connection holds it.</summary>
    public bool Own() => OnThread(() =>
    {
        var error = stackalloc long[8];
        dbus_error_init(error);
        int owned;
        // DBUS_NAME_FLAG_DO_NOT_QUEUE; 1 is DBUS_REQUEST_NAME_REPLY_PRIMARY_OWNER.
        fixed (byte* n = Utf8(Name)) owned = dbus_bus_request_name(_connection, n, 4, error);
        dbus_error_free(error);
        return owned == 1;
    });

    /// <summary>Gives the watcher's name up.</summary>
    public void Release() => OnThread(() =>
    {
        var error = stackalloc long[8];
        dbus_error_init(error);
        fixed (byte* n = Utf8(Name)) _ = dbus_bus_release_name(_connection, n, error);
        dbus_error_free(error);
        return 0;
    });

    /// <summary>Calls a method with arguments of <paramref name="signature"/> (s, i, u, b, as, ai, a(isvu), and v holding
    /// s, i or b as a <see cref="FakeVariant"/>); the reply's values and signature, or null with the error's name.</summary>
    public (object?[]? Values, string Error, string Signature) Call(string destination, string path, string iface, string member, string signature, params object?[] args) => OnThread(() =>
    {
        nint message;
        fixed (byte* d = Utf8(destination))
        fixed (byte* p = Utf8(path))
        fixed (byte* i = Utf8(iface))
        fixed (byte* m = Utf8(member))
            message = dbus_message_new_method_call(d, p, i, m);
        var iter = stackalloc long[16];
        dbus_message_iter_init_append(message, iter);
        var types = Types(signature);
        for (var k = 0; k < types.Count; k++) Write(iter, types[k], args[k]);
        var error = stackalloc nint[8];
        dbus_error_init(error);
        var reply = dbus_connection_send_with_reply_and_block(_connection, message, 5000, error);
        var name = reply == 0 && error[0] != 0 ? Str((byte*)error[0]) : "";
        dbus_error_free(error);
        dbus_message_unref(message);
        if (reply == 0) return ((object?[]?)null, name, "");
        try { return (ReadAll(reply), "", Str(dbus_message_get_signature(reply))); }
        finally { dbus_message_unref(reply); }
    });

    /// <summary><c>NameHasOwner</c>.</summary>
    public bool HasOwner(string name) => Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s", name).Values is [true];

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
        dbus_connection_close(_connection);
    }

    private T OnThread<T>(Func<T> func)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try { done.SetResult(func()); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task.Wait(TimeSpan.FromSeconds(10)) ? done.Task.Result : throw new TimeoutException("The fake watcher did not answer.");
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
                    var type = dbus_message_get_type(message);
                    if (type == MessageSignal)
                        Signals.Enqueue(new FakeSignal(Str(dbus_message_get_sender(message)), Str(dbus_message_get_path(message)), Str(dbus_message_get_interface(message)), Str(dbus_message_get_member(message)), ReadAll(message)));
                    else if (type == MessageMethodCall) Answer(message);
                }
                catch (Exception e) { Console.Error.WriteLine($"Fake watcher: {e}"); }
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
        nint reply;
        if (iface == "org.freedesktop.DBus.Properties" && member == "Get" && args is [Name, "IsStatusNotifierHostRegistered"] && IgnoreHostQuery) return;
        if (iface == "org.freedesktop.DBus.Properties" && member == "Get" && args is [Name, "IsStatusNotifierHostRegistered"])
        {
            reply = dbus_message_new_method_return(message);
            var iter = stackalloc long[16];
            dbus_message_iter_init_append(reply, iter);
            Write(iter, "v", new FakeVariant("b", HostRegistered));
        }
        else if (iface == Name && member == "RegisterStatusNotifierItem" && args is [string service])
        {
            Registrations.Enqueue((service, Str(dbus_message_get_sender(message))));
            reply = dbus_message_new_method_return(message);
        }
        else
        {
            fixed (byte* n = "org.freedesktop.DBus.Error.UnknownMethod\0"u8)
            fixed (byte* t = "Not the fake's\0"u8)
                reply = dbus_message_new_error(message, n, t);
        }
        uint serial;
        _ = dbus_connection_send(_connection, reply, &serial);
        dbus_message_unref(reply);
    }

    private static List<string> Types(string signature)
    {
        var types = new List<string>();
        var i = 0;
        while (i < signature.Length)
        {
            var start = i;
            while (signature[i] == 'a') i++;
            if (signature[i] == '(')
            {
                var depth = 0;
                do
                {
                    if (signature[i] == '(') depth++;
                    else if (signature[i] == ')') depth--;
                    i++;
                } while (depth > 0);
            }
            else i++;
            types.Add(signature[start..i]);
        }
        return types;
    }

    private static void Write(void* iter, string type, object? value)
    {
        switch (type)
        {
            case "s": fixed (byte* b = Utf8((string)value!)) { var p = b; _ = dbus_message_iter_append_basic(iter, 's', &p); } break;
            case "i": { var v = (int)value!; _ = dbus_message_iter_append_basic(iter, 'i', &v); break; }
            case "u": { var v = (uint)value!; _ = dbus_message_iter_append_basic(iter, 'u', &v); break; }
            case "b": { var v = (bool)value! ? 1u : 0u; _ = dbus_message_iter_append_basic(iter, 'b', &v); break; }
            case "v":
            {
                var variant = (FakeVariant)value!;
                var sub = stackalloc long[16];
                fixed (byte* s = Utf8(variant.Signature)) _ = dbus_message_iter_open_container(iter, 'v', s, sub);
                Write(sub, variant.Signature, variant.Value);
                _ = dbus_message_iter_close_container(iter, sub);
                break;
            }
            case "(isvu)":
            {
                var fields = (object?[])value!;
                var sub = stackalloc long[16];
                _ = dbus_message_iter_open_container(iter, 'r', null, sub);
                Write(sub, "i", fields[0]);
                Write(sub, "s", fields[1]);
                Write(sub, "v", fields[2]);
                Write(sub, "u", fields[3]);
                _ = dbus_message_iter_close_container(iter, sub);
                break;
            }
            case ['a', .. var element]:
            {
                var sub = stackalloc long[16];
                fixed (byte* s = Utf8(element)) _ = dbus_message_iter_open_container(iter, 'a', s, sub);
                foreach (var item in (System.Collections.IEnumerable)value!) Write(sub, element, item);
                _ = dbus_message_iter_close_container(iter, sub);
                break;
            }
            default: throw new ArgumentException($"The fake writes no '{type}'.");
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

    /// <summary>A read <c>a{sv}</c> as a dictionary of its variants' signatures and values, keys in wire order.</summary>
    public static List<(string Key, FakeVariant Value)> Dict(object? value)
        => [.. ((List<object?>)value!).Cast<KeyValuePair<object?, object?>>().Select(e => ((string)e.Key!, (FakeVariant)e.Value!))];

    private static string Str(byte* s) => s == null ? "" : Marshal.PtrToStringUTF8((nint)s) ?? "";
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    [LibraryImport(LibDBus)] private static partial void dbus_error_init(void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_error_free(void* error);
    [LibraryImport(LibDBus)] private static partial nint dbus_bus_get_private(int type, void* error);
    [LibraryImport(LibDBus)] private static partial byte* dbus_bus_get_unique_name(nint connection);
    [LibraryImport(LibDBus)] private static partial void dbus_bus_add_match(nint connection, byte* rule, void* error);
    [LibraryImport(LibDBus)] private static partial int dbus_bus_request_name(nint connection, byte* name, uint flags, void* error);
    [LibraryImport(LibDBus)] private static partial int dbus_bus_release_name(nint connection, byte* name, void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_set_exit_on_disconnect(nint connection, uint exit);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_close(nint connection);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_read_write(nint connection, int timeout);
    [LibraryImport(LibDBus)] private static partial nint dbus_connection_pop_message(nint connection);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_send(nint connection, nint message, uint* serial);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_flush(nint connection);
    [LibraryImport(LibDBus)] private static partial nint dbus_connection_send_with_reply_and_block(nint connection, nint message, int timeout, void* error);
    [LibraryImport(LibDBus)] private static partial int dbus_message_get_type(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_sender(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_path(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_signature(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_interface(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_member(nint message);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_method_call(byte* destination, byte* path, byte* iface, byte* method);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_method_return(nint call);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_error(nint call, byte* name, byte* message);
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
