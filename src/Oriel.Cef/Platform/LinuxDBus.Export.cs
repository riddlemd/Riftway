using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Oriel.Cef.Platform;

/// <summary>A D-Bus object path value (type <c>o</c>).</summary>
public sealed record LinuxDBusObjectPath(string Value);

/// <summary>A D-Bus variant: a value with its own signature, compared by value as <c>dbus_utils::Variant</c>.</summary>
public sealed class LinuxDBusVariant(string signature, object? value) : IEquatable<LinuxDBusVariant>
{
    /// <summary>The single complete type of <see cref="Value"/>.</summary>
    public string Signature { get; } = signature;

    /// <summary>The value, in the shapes <see cref="LinuxDBus"/> writes and reads.</summary>
    public object? Value { get; } = value;

    public bool Equals(LinuxDBusVariant? other) => other is not null && Signature == other.Signature && LinuxDBusValues.DeepEquals(Value, other.Value);

    public override bool Equals(object? obj) => Equals(obj as LinuxDBusVariant);

    public override int GetHashCode() => Signature.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => $"<{Signature}>{LinuxDBusValues.Format(Value)}";
}

/// <summary>A method call to an object exported with <see cref="LinuxDBus.Export"/>; answer it exactly once with
/// <see cref="LinuxDBus.Reply"/> or <see cref="LinuxDBus.ReplyError"/>.</summary>
public sealed class LinuxDBusMethodCall
{
    internal nint Message;

    internal LinuxDBusMethodCall(nint message, string sender, string path, string iface, string member, string signature, object?[] args)
        => (Message, Sender, Path, Interface, Member, Signature, Args) = (message, sender, path, iface, member, signature, args);

    public string Sender { get; }
    public string Path { get; }
    public string Interface { get; }
    public string Member { get; }
    public string Signature { get; }

    /// <summary>The arguments as <see cref="LinuxDBusValues"/> reads them.</summary>
    public object?[] Args { get; }
}

/// <summary>D-Bus values in .NET shapes, and their signatures. Writing follows the signature: <c>a</c> takes any
/// <see cref="IEnumerable"/> (a <c>byte[]</c> for <c>ay</c>; an <see cref="IDictionary"/> or <see cref="ITuple"/> pairs
/// for a dict), a struct an <c>object?[]</c> or <see cref="ITuple"/>, <c>v</c> a <see cref="LinuxDBusVariant"/>, <c>o</c>
/// a <see cref="LinuxDBusObjectPath"/> or string. Reading gives arrays as <c>List&lt;object?&gt;</c> (<c>ay</c> as
/// <c>byte[]</c>), dict entries as <c>KeyValuePair&lt;object?, object?&gt;</c> and structs as <c>object?[]</c>.</summary>
public static class LinuxDBusValues
{
    /// <summary>Splits a signature into its complete types.</summary>
    /// <exception cref="ArgumentException">Not a valid signature.</exception>
    public static IReadOnlyList<string> Split(string signature)
    {
        var types = new List<string>();
        for (var i = 0; i < signature.Length;)
        {
            var end = End(signature, i);
            types.Add(signature[i..end]);
            i = end;
        }
        return types;
    }

    // The index just past the complete type starting at `start`.
    private static int End(string signature, int start)
    {
        if (start >= signature.Length) throw new ArgumentException($"'{signature}' ends inside a type.", nameof(signature));
        switch (signature[start])
        {
            case 'a': return End(signature, start + 1);
            case '(':
            case '{':
                var close = signature[start] == '(' ? ')' : '}';
                var i = start + 1;
                while (i < signature.Length && signature[i] != close) i = End(signature, i);
                if (i >= signature.Length) throw new ArgumentException($"'{signature}' has an unclosed container.", nameof(signature));
                return i + 1;
            case 'y' or 'b' or 'n' or 'q' or 'i' or 'u' or 'x' or 't' or 'd' or 'h' or 's' or 'o' or 'g' or 'v':
                return start + 1;
            default:
                throw new ArgumentException($"'{signature[start]}' in '{signature}' is not a D-Bus type.", nameof(signature));
        }
    }

    /// <summary>Whether two values are equal as D-Bus values: arrays, structs and dicts element by element.</summary>
    public static bool DeepEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a is string || b is string || a is LinuxDBusVariant || b is LinuxDBusObjectPath) return a.Equals(b);
        if (a is IDictionary da && b is IDictionary db)
        {
            if (da.Count != db.Count) return false;
            foreach (DictionaryEntry e in da)
                if (!db.Contains(e.Key) || !DeepEquals(e.Value, db[e.Key])) return false;
            return true;
        }
        if (a is ITuple ta && b is ITuple tb)
        {
            if (ta.Length != tb.Length) return false;
            for (var i = 0; i < ta.Length; i++) if (!DeepEquals(ta[i], tb[i])) return false;
            return true;
        }
        if (a is IEnumerable ea && b is IEnumerable eb)
        {
            var ia = ea.GetEnumerator();
            var ib = eb.GetEnumerator();
            while (true)
            {
                var ma = ia.MoveNext();
                if (ma != ib.MoveNext()) return false;
                if (!ma) return true;
                if (!DeepEquals(ia.Current, ib.Current)) return false;
            }
        }
        return a.Equals(b);
    }

    /// <summary>A readable form for test output.</summary>
    public static string Format(object? value) => value switch
    {
        null => "null",
        string s => $"'{s}'",
        bool b => b ? "true" : "false",
        byte[] bytes => $"[{bytes.Length} bytes]",
        LinuxDBusObjectPath p => p.Value,
        LinuxDBusVariant v => v.ToString(),
        IDictionary d => "{" + string.Join(", ", d.Cast<DictionaryEntry>().Select(e => $"{Format(e.Key)}: {Format(e.Value)}")) + "}",
        ITuple t => "(" + string.Join(", ", Enumerable.Range(0, t.Length).Select(i => Format(t[i]))) + ")",
        KeyValuePair<object?, object?> kv => $"{Format(kv.Key)}: {Format(kv.Value)}",
        IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Format)) + "]",
        _ => value.ToString() ?? "",
    };
}

public sealed unsafe partial class LinuxDBus
{
    private const int MessageMethodCall = 1;
    // libdbus's _DBUS_DEFAULT_TIMEOUT_VALUE, what TIMEOUT_USE_DEFAULT means.
    private static readonly TimeSpan s_replyTimeout = TimeSpan.FromSeconds(25);
    private readonly List<PendingCall> _pending = [];

    private sealed class PendingCall(LinuxDBus bus, nint pending, Action<object?[]?, string> done, long deadline)
    {
        public readonly LinuxDBus Bus = bus;
        public readonly nint Pending = pending;
        public readonly Action<object?[]?, string> Done = done;
        public readonly long Deadline = deadline;
        public GCHandle Handle;
    }
    private static readonly nint s_vtable = CreateVtable();
    private readonly Dictionary<string, Func<LinuxDBusMethodCall, bool>> _objects = [];

    /// <summary>The connection's unique name (<c>:1.42</c>), or empty without a bus. Bus thread only.</summary>
    public string UniqueName
    {
        get
        {
            var connection = Connection();
            return connection == 0 ? "" : Str(dbus_bus_get_unique_name(connection));
        }
    }

    /// <summary><c>RequestName</c> with <c>DBUS_NAME_FLAG_DO_NOT_QUEUE</c>, as <c>dbus::Bus::REQUIRE_PRIMARY</c>: true when
    /// the connection is the primary owner. Bus thread only.</summary>
    public bool RequestName(string name)
    {
        var connection = Connection();
        if (connection == 0) return false;
        var error = stackalloc long[8];
        dbus_error_init(error);
        int result;
        fixed (byte* n = Utf8(name)) result = dbus_bus_request_name(connection, n, 4, error);
        dbus_error_free(error);
        // DBUS_REQUEST_NAME_REPLY_PRIMARY_OWNER.
        return result == 1;
    }

    /// <summary><c>ReleaseName</c>; false if the bus refused. Bus thread only.</summary>
    public bool ReleaseName(string name)
    {
        var connection = Connection();
        if (connection == 0) return false;
        var error = stackalloc long[8];
        dbus_error_init(error);
        int result;
        fixed (byte* n = Utf8(name)) result = dbus_bus_release_name(connection, n, error);
        dbus_error_free(error);
        // DBUS_RELEASE_NAME_REPLY_RELEASED.
        return result == 1;
    }

    /// <summary>Serves <paramref name="path"/>: <paramref name="handler"/> sees each call there, on the bus thread, and
    /// returns false for one it does not know, which libdbus then answers with <c>UnknownMethod</c>. False if the path
    /// is taken on this connection or there is no bus. Bus thread only.</summary>
    public bool Export(string path, Func<LinuxDBusMethodCall, bool> handler)
    {
        var connection = Connection();
        if (connection == 0 || _objects.ContainsKey(path)) return false;
        var error = stackalloc long[8];
        dbus_error_init(error);
        uint registered;
        fixed (byte* p = Utf8(path)) registered = dbus_connection_try_register_object_path(connection, p, (void*)s_vtable, GCHandle.ToIntPtr(_self), error);
        dbus_error_free(error);
        if (registered == 0) return false;
        _objects[path] = handler;
        return true;
    }

    /// <summary>Stops serving <paramref name="path"/>. Bus thread only.</summary>
    public void Unexport(string path)
    {
        if (!_objects.Remove(path) || _connection == 0) return;
        fixed (byte* p = Utf8(path)) _ = dbus_connection_unregister_object_path(_connection, p);
    }

    /// <summary>Answers <paramref name="call"/> with values of <paramref name="signature"/>; from any thread.</summary>
    public void Reply(LinuxDBusMethodCall call, string signature, params object?[] values) => Post(() =>
    {
        var message = Interlocked.Exchange(ref call.Message, 0);
        if (message == 0) return;
        try
        {
            if (_connection == 0) return;
            var reply = dbus_message_new_method_return(message);
            try { AppendAll(reply, signature, values); }
            catch (Exception)
            {
                dbus_message_unref(reply);
                reply = Error(message, "org.freedesktop.DBus.Error.Failed", $"error occurred in {call.Member}");
            }
            Send(reply);
        }
        finally { dbus_message_unref(message); }
    });

    /// <summary>Answers <paramref name="call"/> with an error; from any thread.</summary>
    public void ReplyError(LinuxDBusMethodCall call, string name, string text) => Post(() =>
    {
        var message = Interlocked.Exchange(ref call.Message, 0);
        if (message == 0) return;
        try { if (_connection != 0) Send(Error(message, name, text)); }
        finally { dbus_message_unref(message); }
    });

    /// <summary>Sends a signal from <paramref name="path"/>; false without a bus. Bus thread only.</summary>
    public bool Emit(string path, string iface, string member, string signature, params object?[] values)
    {
        var connection = Connection();
        if (connection == 0) return false;
        nint signal;
        fixed (byte* p = Utf8(path))
        fixed (byte* i = Utf8(iface))
        fixed (byte* m = Utf8(member))
            signal = dbus_message_new_signal(p, i, m);
        AppendAll(signal, signature, values);
        Send(signal);
        return true;
    }

    /// <summary>Calls a method with arguments of <paramref name="signature"/> without waiting, as Chromium's
    /// <c>CallMethod</c>: <paramref name="done"/> runs on the bus thread with the reply's values as
    /// <see cref="LinuxDBusValues"/> reads them, or null with the error's name (empty without a bus). The thread keeps
    /// serving exported objects meanwhile, so the callee may call back. Bus thread only.</summary>
    public void CallAsync(string destination, string path, string iface, string method, string signature, object?[] args, Action<object?[]?, string> done)
    {
        var connection = Connection();
        if (connection == 0)
        {
            done(null, "");
            return;
        }
        nint message;
        fixed (byte* d = Utf8(destination))
        fixed (byte* p = Utf8(path))
        fixed (byte* i = Utf8(iface))
        fixed (byte* m = Utf8(method))
            message = dbus_message_new_method_call(d, p, i, m);
        AppendAll(message, signature, args);
        nint pending;
        var sent = dbus_connection_send_with_reply(connection, message, &pending, DefaultTimeout);
        dbus_message_unref(message);
        if (sent == 0 || pending == 0)
        {
            done(null, "");
            return;
        }
        // The reply is read on this thread, so it cannot complete before the notification is set.
        var call = new PendingCall(this, pending, done, Environment.TickCount64 + (long)s_replyTimeout.TotalMilliseconds);
        call.Handle = GCHandle.Alloc(call);
        _pending.Add(call);
        _ = dbus_pending_call_set_notify(pending, &OnPendingCall, GCHandle.ToIntPtr(call.Handle), null);
        dbus_connection_flush(connection);
    }

    // Without a main loop libdbus never fires a pending call's timeout; this does, with libdbus's own error.
    private int PollTimeout()
    {
        if (_pending.Count == 0) return -1;
        return (int)Math.Clamp(_pending.Min(p => p.Deadline) - Environment.TickCount64, 0, int.MaxValue);
    }

    private void ExpirePendingCalls()
    {
        var now = Environment.TickCount64;
        foreach (var call in _pending.Where(p => p.Deadline <= now).ToList())
        {
            dbus_pending_call_cancel(call.Pending);
            Finish(call);
            call.Done(null, "org.freedesktop.DBus.Error.NoReply");
        }
    }

    // A closed connection answers nothing more; the callers hear nothing, as with Chromium's bus shut down.
    private void AbandonPendingCalls()
    {
        foreach (var call in _pending.ToList())
        {
            dbus_pending_call_cancel(call.Pending);
            Finish(call);
        }
    }

    private void Finish(PendingCall call)
    {
        _pending.Remove(call);
        call.Handle.Free();
        dbus_pending_call_unref(call.Pending);
    }

    [UnmanagedCallersOnly]
    private static void OnPendingCall(nint pending, nint data)
    {
        var call = (PendingCall)GCHandle.FromIntPtr(data).Target!;
        var done = call.Done;
        var reply = dbus_pending_call_steal_reply(pending);
        call.Bus.Finish(call);
        try
        {
            // DBUS_MESSAGE_TYPE_ERROR.
            if (reply == 0) done(null, "");
            else if (dbus_message_get_type(reply) == 3) done(null, Str(dbus_message_get_error_name(reply)));
            else done(ReadAll(reply), "");
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Console.Error.WriteLine($"Oriel D-Bus: {e}"); }
        finally { if (reply != 0) dbus_message_unref(reply); }
    }

    private void Send(nint message)
    {
        uint serial;
        _ = dbus_connection_send(_connection, message, &serial);
        dbus_message_unref(message);
        dbus_connection_flush(_connection);
    }

    private static nint Error(nint call, string name, string text)
    {
        fixed (byte* n = Utf8(name))
        fixed (byte* t = Utf8(text))
            return dbus_message_new_error(call, n, t);
    }

    private static nint CreateVtable()
    {
        // DBusObjectPathVTable: unregister, message, then four padding pointers.
        var vtable = (nint*)NativeMemory.AllocZeroed((nuint)(6 * sizeof(nint)));
        vtable[1] = (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnObjectMessage;
        return (nint)vtable;
    }

    [UnmanagedCallersOnly]
    private static int OnObjectMessage(nint connection, nint message, nint data)
    {
        try
        {
            if (dbus_message_get_type(message) != MessageMethodCall || GCHandle.FromIntPtr(data).Target is not LinuxDBus bus) return 1;
            var path = Str(dbus_message_get_path(message));
            var iface = Str(dbus_message_get_interface(message));
            // As dbus::ExportedObject: a call without an interface is not taken.
            if (iface.Length == 0 || !bus._objects.TryGetValue(path, out var handler)) return 1;
            object?[] args;
            try { args = ReadAll(message); }
            catch (Exception) { return 1; }
            dbus_message_ref(message);
            var call = new LinuxDBusMethodCall(message, Str(dbus_message_get_sender(message)), path, iface, Str(dbus_message_get_member(message)), Str(dbus_message_get_signature(message)), args);
            if (handler(call)) return 0;
            call.Message = 0;
            dbus_message_unref(message);
            return 1;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Oriel D-Bus: {e}");
            return 1;
        }
    }

    private static void AppendAll(nint message, string signature, object?[] values)
    {
        var types = LinuxDBusValues.Split(signature);
        if (types.Count != values.Length) throw new ArgumentException($"'{signature}' has {types.Count} types for {values.Length} values.", nameof(values));
        var iter = stackalloc long[16];
        dbus_message_iter_init_append(message, iter);
        for (var i = 0; i < types.Count; i++) Append(iter, types[i], values[i]);
    }

    private static void Append(void* iter, string type, object? value)
    {
        switch (type[0])
        {
            case 'y': { var v = Convert.ToByte(value); Basic(iter, 'y', &v); break; }
            // dbus_bool_t is 32 bits.
            case 'b': { var v = (bool)value! ? 1u : 0u; Basic(iter, 'b', &v); break; }
            case 'n': { var v = Convert.ToInt16(value); Basic(iter, 'n', &v); break; }
            case 'q': { var v = Convert.ToUInt16(value); Basic(iter, 'q', &v); break; }
            case 'i' or 'h': { var v = Convert.ToInt32(value); Basic(iter, type[0], &v); break; }
            case 'u': { var v = Convert.ToUInt32(value); Basic(iter, 'u', &v); break; }
            case 'x': { var v = Convert.ToInt64(value); Basic(iter, 'x', &v); break; }
            case 't': { var v = Convert.ToUInt64(value); Basic(iter, 't', &v); break; }
            case 'd': { var v = Convert.ToDouble(value); Basic(iter, 'd', &v); break; }
            case 's' or 'o' or 'g':
                var text = value is LinuxDBusObjectPath path ? path.Value : (string)value!;
                fixed (byte* bytes = Utf8(text))
                {
                    var p = bytes;
                    Basic(iter, type[0], &p);
                }
                break;
            case 'v':
            {
                var variant = (LinuxDBusVariant)value!;
                var sub = stackalloc long[16];
                fixed (byte* s = Utf8(variant.Signature)) _ = dbus_message_iter_open_container(iter, 'v', s, sub);
                Append(sub, variant.Signature, variant.Value);
                _ = dbus_message_iter_close_container(iter, sub);
                break;
            }
            case 'a':
            {
                var element = type[1..];
                var sub = stackalloc long[16];
                fixed (byte* s = Utf8(element)) _ = dbus_message_iter_open_container(iter, 'a', s, sub);
                if (element == "y")
                {
                    var bytes = (byte[])value!;
                    fixed (byte* b = bytes)
                    {
                        var p = b;
                        _ = dbus_message_iter_append_fixed_array(sub, 'y', &p, bytes.Length);
                    }
                }
                else if (element[0] == '{' && value is IDictionary dictionary)
                {
                    foreach (DictionaryEntry entry in dictionary) Append(sub, element, (entry.Key, entry.Value));
                }
                else
                {
                    foreach (var item in (IEnumerable)value!) Append(sub, element, item);
                }
                _ = dbus_message_iter_close_container(iter, sub);
                break;
            }
            case '(' or '{':
            {
                var fields = LinuxDBusValues.Split(type[1..^1]);
                var sub = stackalloc long[16];
                // DBUS_TYPE_STRUCT 'r', DBUS_TYPE_DICT_ENTRY 'e'.
                _ = dbus_message_iter_open_container(iter, type[0] == '(' ? 'r' : 'e', null, sub);
                for (var i = 0; i < fields.Count; i++) Append(sub, fields[i], value is object?[] array ? array[i] : ((ITuple)value!)[i]);
                _ = dbus_message_iter_close_container(iter, sub);
                break;
            }
            default: throw new ArgumentException($"No D-Bus type '{type}'.", nameof(type));
        }
    }

    private static void Basic(void* iter, int type, void* value)
    {
        if (dbus_message_iter_append_basic(iter, type, value) == 0) throw new InvalidOperationException("libdbus could not append a value.");
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
            case 'n': { short v; dbus_message_iter_get_basic(iter, &v); return v; }
            case 'q': { ushort v; dbus_message_iter_get_basic(iter, &v); return v; }
            case 'i' or 'h': { int v; dbus_message_iter_get_basic(iter, &v); return v; }
            case 'u': { uint v; dbus_message_iter_get_basic(iter, &v); return v; }
            case 'x': { long v; dbus_message_iter_get_basic(iter, &v); return v; }
            case 't': { ulong v; dbus_message_iter_get_basic(iter, &v); return v; }
            case 'd': { double v; dbus_message_iter_get_basic(iter, &v); return v; }
            case 's' or 'g': { byte* s; dbus_message_iter_get_basic(iter, &s); return Str(s); }
            case 'o': { byte* s; dbus_message_iter_get_basic(iter, &s); return new LinuxDBusObjectPath(Str(s)); }
            case 'v':
            {
                var sub = stackalloc long[16];
                dbus_message_iter_recurse(iter, sub);
                var signature = dbus_message_iter_get_signature(sub);
                var text = Str(signature);
                dbus_free(signature);
                return new LinuxDBusVariant(text, Read(sub));
            }
            case 'a':
            {
                var sub = stackalloc long[16];
                dbus_message_iter_recurse(iter, sub);
                if (dbus_message_iter_get_element_type(iter) == 'y')
                {
                    byte* data;
                    int count;
                    dbus_message_iter_get_fixed_array(sub, &data, &count);
                    return new ReadOnlySpan<byte>(data, count).ToArray();
                }
                var items = new List<object?>();
                while (dbus_message_iter_get_arg_type(sub) != 0)
                {
                    items.Add(Read(sub));
                    _ = dbus_message_iter_next(sub);
                }
                return items;
            }
            case 'r':
            case 'e':
            {
                var sub = stackalloc long[16];
                dbus_message_iter_recurse(iter, sub);
                var fields = new List<object?>();
                while (dbus_message_iter_get_arg_type(sub) != 0)
                {
                    fields.Add(Read(sub));
                    _ = dbus_message_iter_next(sub);
                }
                return type == 'e' ? new KeyValuePair<object?, object?>(fields[0], fields[1]) : fields.ToArray();
            }
            default: throw new InvalidDataException($"D-Bus type '{(char)type}' is not read.");
        }
    }

    [LibraryImport(LibDBus)] private static partial byte* dbus_bus_get_unique_name(nint connection);
    [LibraryImport(LibDBus)] private static partial int dbus_bus_request_name(nint connection, byte* name, uint flags, void* error);
    [LibraryImport(LibDBus)] private static partial int dbus_bus_release_name(nint connection, byte* name, void* error);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_try_register_object_path(nint connection, byte* path, void* vtable, nint data, void* error);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_unregister_object_path(nint connection, byte* path);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_send(nint connection, nint message, uint* serial);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_method_return(nint call);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_error(nint call, byte* name, byte* message);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_signal(byte* path, byte* iface, byte* name);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_ref(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_sender(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_signature(nint message);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_open_container(void* iter, int type, byte* signature, void* sub);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_close_container(void* iter, void* sub);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_append_fixed_array(void* iter, int type, void* value, int count);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_recurse(void* iter, void* sub);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_iter_get_signature(void* iter);
    [LibraryImport(LibDBus)] private static partial int dbus_message_iter_get_element_type(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_get_fixed_array(void* iter, void* value, int* count);
    [LibraryImport(LibDBus)] private static partial void dbus_free(void* memory);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_send_with_reply(nint connection, nint message, nint* pending, int timeout);
    [LibraryImport(LibDBus)] private static partial uint dbus_pending_call_set_notify(nint pending, delegate* unmanaged<nint, nint, void> notify, nint data, void* free);
    [LibraryImport(LibDBus)] private static partial nint dbus_pending_call_steal_reply(nint pending);
    [LibraryImport(LibDBus)] private static partial void dbus_pending_call_unref(nint pending);
    [LibraryImport(LibDBus)] private static partial void dbus_pending_call_cancel(nint pending);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_error_name(nint message);
}
