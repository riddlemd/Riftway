using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>The X11 CLIPBOARD and PRIMARY selections as Chromium 154's Ozone/X11 clipboard keeps them:
/// <c>ClipboardOzone</c>'s offered data over <c>X11ClipboardOzone</c>, <c>XClipboardHelper</c>, <c>SelectionOwner</c>
/// and <c>SelectionRequester</c>, on Riftway's own xcb connection.</summary>
/// <remarks>One thread owns the connection and the selection window. It answers other clients' requests for as long as
/// the process runs; every call here blocks until that thread has done its part.</remarks>
[SupportedOSPlatform("linux")]
public static unsafe partial class X11Clipboard
{
    private const string LibXcb = "libxcb.so.1";
    private const string LibC = "libc.so.6";

    private const byte PropertyNotify = 28, SelectionRequest = 30, SelectionNotify = 31;
    private const byte PropertyNewValue = 0, PropertyDelete = 1;
    private const uint PropertyChangeMask = 0x400000;
    private const uint AtomPrimary = 1, AtomAtom = 4, AtomInteger = 19, AtomString = 31, AtomWmName = 39;

    // Chromium 154's SelectionRequester waits for an answer without limit; a dead owner would hang reads forever.
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(10);
    // SelectionOwner's kIncrementalTransferTimeout.
    private static readonly TimeSpan s_incrementalTimeout = TimeSpan.FromSeconds(10);

    private static readonly object s_gate = new();
    private static readonly Queue<Action> s_work = new();
    private static Thread? s_thread;
    private static Exception? s_startError;
    private static nint s_conn;
    private static uint s_window;
    private static int s_wakeFd = -1;
    private static readonly Dictionary<string, uint> s_atoms = new(StringComparer.Ordinal);
    private static readonly Owner s_clipboardOwner = new(), s_primaryOwner = new();
    // ClipboardOzone's offered_data_: what was last offered per buffer, read back while this process owns it.
    private static readonly SortedDictionary<string, byte[]>[] s_offered = [new(X11SelectionRules.Utf8Order), new(X11SelectionRules.Utf8Order)];
    private static Request? s_request;
    private static uint? s_serverTime;

    /// <summary>Takes <paramref name="primary"/> (PRIMARY) or CLIPBOARD with <paramref name="data"/>, each payload under
    /// its target name; a later entry for the same name replaces an earlier one. An empty list owns the selection with
    /// nothing on it, which is how Chromium clears.</summary>
    /// <exception cref="PlatformNotSupportedException">There is no X display.</exception>
    public static void Offer(bool primary, IReadOnlyList<KeyValuePair<string, byte[]>> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Invoke(() =>
        {
            var offered = new SortedDictionary<string, byte[]>(X11SelectionRules.Utf8Order);
            foreach (var (name, bytes) in data) offered[name] = bytes;
            s_offered[primary ? 1 : 0] = offered;
            var map = new SortedDictionary<uint, byte[]>();
            foreach (var (name, bytes) in offered) map[Atom(name)] = bytes;
            OwnerFor(primary).TakeOwnership(Selection(primary), map);
            return true;
        });
    }

    /// <summary>The target names on the selection: the offered names while this process owns it, else the owner's
    /// <c>TARGETS</c> with Chromium's MIME names added, sorted and unique. <c>ClipboardOzone</c>'s
    /// <c>GetAvailableMimeTypesAsync</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">There is no X display.</exception>
    public static IReadOnlyList<string> MimeTypes(bool primary) => Invoke<IReadOnlyList<string>>(() =>
    {
        var selection = Selection(primary);
        if (OwnerOf(selection) == s_window) return [.. s_offered[primary ? 1 : 0].Keys];
        return X11SelectionRules.MimeTypesFromTargets([.. TargetList(selection).Select(AtomName)]);
    });

    /// <summary>The bytes under <paramref name="mime"/>, or null: the offered bytes while this process owns the
    /// selection, else the first target the owner converts (the text targets for <c>text/plain</c>).
    /// <c>ClipboardOzone</c>'s <c>ReadClipboardDataAsync</c>.</summary>
    /// <exception cref="PlatformNotSupportedException">There is no X display.</exception>
    public static byte[]? Read(bool primary, string mime)
    {
        ArgumentNullException.ThrowIfNull(mime);
        return Invoke(() =>
        {
            var selection = Selection(primary);
            if (OwnerOf(selection) == s_window) return s_offered[primary ? 1 : 0].GetValueOrDefault(mime);
            var wanted = mime == "text/plain" ? X11SelectionRules.TextTargets.Select(Atom).ToList() : [Atom(mime)];
            var targets = TargetList(selection);
            foreach (var target in wanted.Where(targets.Contains))
            {
                var (ok, data, type) = Convert(selection, target);
                if (ok && type != 0) return data;
            }
            return null;
        });
    }

    private static uint Selection(bool primary) => primary ? AtomPrimary : Atom("CLIPBOARD");

    private static Owner OwnerFor(bool primary) => primary ? s_primaryOwner : s_clipboardOwner;

    // XClipboardHelper::GetTargetListAsync: our own targets, the owner's TARGETS, or for owners without TARGETS the
    // text targets it will convert.
    private static List<uint> TargetList(uint selection)
    {
        if (OwnerOf(selection) == s_window) return [.. OwnerFor(selection == AtomPrimary).Map.Keys];
        var (ok, data, type) = Convert(selection, Atom("TARGETS"));
        if (ok && (type == AtomAtom || type == Atom("TARGETS")))
            return [.. MemoryMarshal.Cast<byte, uint>(data.AsSpan(0, data.Length & ~3)).ToArray()];
        var found = new List<uint>();
        foreach (var name in X11SelectionRules.TextTargets)
        {
            var atom = Atom(name);
            var (converted, _, convertedType) = Convert(selection, atom);
            if (converted && convertedType == atom) found.Add(atom);
        }
        return found;
    }

    // ---- SelectionRequester ----

    private sealed class Request(uint selection, uint target)
    {
        internal readonly uint Selection = selection, Target = target;
        internal readonly List<byte[]> Data = [];
        internal bool Completed, Success, Incremental;
        internal uint OutType;
        internal DateTime Progress = DateTime.UtcNow;
    }

    // One ConvertSelection into CHROME_SELECTION on our window, answered at once or incrementally (INCR).
    private static (bool Ok, byte[] Data, uint Type) Convert(uint selection, uint target)
    {
        var request = s_request = new Request(selection, target);
        _ = xcb_convert_selection(s_conn, s_window, selection, target, Atom("CHROME_SELECTION"), 0);
        Pump(() => request.Completed, () => request.Progress + s_requestTimeout);
        s_request = null;
        if (!request.Completed || !request.Success) return (false, [], request.OutType);
        return (true, [.. request.Data.SelectMany(d => d)], request.OutType);
    }

    private static void OnSelectionNotify(uint selection, uint target, uint property)
    {
        var request = s_request;
        if (request is null || request.Completed || request.Selection != selection || request.Target != target)
        {
            // ICCCM: the requestor deletes the property named in SelectionNotify.
            if (property != 0) _ = xcb_delete_property(s_conn, s_window, property);
            return;
        }
        var success = false;
        var chromeSelection = Atom("CHROME_SELECTION");
        if (property == chromeSelection && ReadProperty(s_window, chromeSelection) is { } value)
        {
            success = true;
            request.OutType = value.Type;
            request.Data.Clear();
            request.Data.Add(value.Data);
        }
        if (property != 0) _ = xcb_delete_property(s_conn, s_window, property);
        if (request.OutType == Atom("INCR"))
        {
            request.Incremental = true;
            request.Data.Clear();
            request.OutType = 0;
            request.Progress = DateTime.UtcNow;
        }
        else Complete(request, success);
    }

    private static void OnRequestorProperty()
    {
        var request = s_request;
        if (request is null || !request.Incremental || request.Completed) return;
        var chromeSelection = Atom("CHROME_SELECTION");
        if (ReadProperty(s_window, chromeSelection) is not { } chunk || (request.OutType != 0 && request.OutType != chunk.Type))
        {
            Complete(request, false);
            return;
        }
        request.Data.Add(chunk.Data);
        request.OutType = chunk.Type;
        request.Progress = DateTime.UtcNow;
        // Deleting the property asks the owner for the next chunk.
        _ = xcb_delete_property(s_conn, s_window, chromeSelection);
        if (chunk.Data.Length == 0) Complete(request, true);
    }

    private static void Complete(Request request, bool success)
    {
        request.Success = success;
        request.Completed = true;
    }

    // ---- SelectionOwner ----

    private sealed class Owner
    {
        internal SortedDictionary<uint, byte[]> Map = [];
        internal readonly List<Transfer> Transfers = [];
        private uint _timestamp;

        internal void TakeOwnership(uint selection, SortedDictionary<uint, byte[]> map)
        {
            _timestamp = ServerTime();
            _ = xcb_set_selection_owner(s_conn, s_window, selection, _timestamp);
            // The X server may refuse a stale timestamp; the data is only ours once it agrees.
            if (OwnerOf(selection) == s_window) Map = map;
        }

        internal void OnSelectionRequest(uint time, uint requestor, uint selection, uint target, uint property)
        {
            var replyProperty = 0u;
            if (target == Atom("MULTIPLE"))
            {
                if (ReadProperty(requestor, property) is { } pairs && pairs.Format == 32 && pairs.Data.Length % 8 == 0)
                {
                    var atoms = MemoryMarshal.Cast<byte, uint>(pairs.Data).ToArray();
                    var results = new uint[atoms.Length];
                    for (var i = 0; i < atoms.Length; i += 2)
                    {
                        results[i] = atoms[i];
                        results[i + 1] = ProcessTarget(atoms[i], requestor, atoms[i + 1]) ? atoms[i + 1] : 0;
                    }
                    SetAtoms(requestor, property, Atom("ATOM_PAIR"), results);
                    replyProperty = property;
                }
            }
            else if (ProcessTarget(target, requestor, property)) replyProperty = property;

            var reply = stackalloc byte[32];
            new Span<byte>(reply, 32).Clear();
            reply[0] = SelectionNotify;
            *(uint*)(reply + 4) = time;
            *(uint*)(reply + 8) = requestor;
            *(uint*)(reply + 12) = selection;
            *(uint*)(reply + 16) = target;
            *(uint*)(reply + 20) = replyProperty;
            _ = xcb_send_event(s_conn, 0, requestor, 0, reply);
        }

        private bool ProcessTarget(uint target, uint requestor, uint property)
        {
            if (target == Atom("MULTIPLE") || target == Atom("SAVE_TARGETS")) return false;
            if (target == Atom("TIMESTAMP"))
            {
                SetAtoms(requestor, property, AtomInteger, [_timestamp]);
                return true;
            }
            if (target == Atom("TARGETS"))
            {
                SetAtoms(requestor, property, AtomAtom, [.. X11SelectionRules.TargetsReply(Map.Keys, Atom)]);
                return true;
            }
            if (!Map.TryGetValue(target, out var data)) return false;
            if (data.Length > MaxChunk())
            {
                SetAtoms(requestor, property, Atom("INCR"), [(uint)data.Length]);
                // The requestor deleting the property asks for each chunk; those PropertyNotify events need this mask.
                SelectProperties(requestor, PropertyChangeMask);
                Transfers.Add(new Transfer(requestor, target, property, data) { Timeout = DateTime.UtcNow + s_incrementalTimeout });
            }
            else SetBytes(requestor, property, target, data);
            return true;
        }

        internal bool OnPropertyEvent(uint window, uint atom, byte state)
        {
            if (state != PropertyDelete) return false;
            var transfer = Transfers.FirstOrDefault(t => t.Window == window && t.Property == atom);
            if (transfer is null) return false;
            var length = Math.Min(transfer.Data.Length - transfer.Offset, MaxChunk());
            SetBytes(transfer.Window, transfer.Property, transfer.Target, transfer.Data.AsSpan(transfer.Offset, length));
            transfer.Offset += length;
            transfer.Timeout = DateTime.UtcNow + s_incrementalTimeout;
            // The zero-length chunk after the last one tells the requestor the transfer is over.
            if (length == 0) Finish(transfer);
            return true;
        }

        internal void AbortStale()
        {
            foreach (var transfer in Transfers.Where(t => t.Timeout <= DateTime.UtcNow).ToList()) Finish(transfer);
        }

        private void Finish(Transfer transfer)
        {
            Transfers.Remove(transfer);
            if (!s_clipboardOwner.Transfers.Concat(s_primaryOwner.Transfers).Any(t => t.Window == transfer.Window))
                SelectProperties(transfer.Window, 0);
        }
    }

    private sealed class Transfer(uint window, uint target, uint property, byte[] data)
    {
        internal readonly uint Window = window, Target = target, Property = property;
        internal readonly byte[] Data = data;
        internal int Offset;
        internal DateTime Timeout;
    }

    private static int MaxChunk() => X11SelectionRules.MaxIncrementalChunk((long)xcb_get_maximum_request_length(s_conn) * 4);

    // X11EventSource::GetCurrentServerTime: a zero-length append to a property comes back as a PropertyNotify carrying
    // the server's time.
    private static uint ServerTime()
    {
        s_serverTime = null;
        var name = Atom("CHROMIUM_TIMESTAMP");
        _ = xcb_change_property(s_conn, 2 /* Append */, s_window, name, AtomString, 8, 0, null);
        Pump(() => s_serverTime is not null, () => DateTime.UtcNow + s_requestTimeout);
        return s_serverTime ?? 0;
    }

    // ---- The connection's thread ----

    private static T Invoke<T>(Func<T> body)
    {
        Start();
        if (Thread.CurrentThread == s_thread) return body();
        T result = default!;
        ExceptionDispatchInfo? error = null;
        using var done = new ManualResetEventSlim();
        lock (s_work)
            s_work.Enqueue(() =>
            {
                try { result = body(); }
                catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
                finally { done.Set(); }
            });
        Wake();
        done.Wait();
        error?.Throw();
        return result;
    }

    private static void Start()
    {
        lock (s_gate)
        {
            if (s_thread is null)
            {
                using var ready = new ManualResetEventSlim();
                s_thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Riftway X11 clipboard" };
                s_thread.Start();
                ready.Wait();
            }
            if (s_startError is not null) throw new PlatformNotSupportedException(s_startError.Message, s_startError);
        }
    }

    private static void Run(ManualResetEventSlim ready)
    {
        try { Connect(); }
        catch (Exception e) { s_startError = e; }
        finally { ready.Set(); }
        if (s_startError is not null) return;

        var fds = stackalloc PollFd[2];
        fds[0] = new PollFd { Fd = xcb_get_file_descriptor(s_conn), Events = 1 /* POLLIN */ };
        fds[1] = new PollFd { Fd = s_wakeFd, Events = 1 };
        while (true)
        {
            Drain();
            while (true)
            {
                Action? work;
                lock (s_work) work = s_work.Count > 0 ? s_work.Dequeue() : null;
                if (work is null) break;
                work();
                Drain();
            }
            s_clipboardOwner.AbortStale();
            s_primaryOwner.AbortStale();
            _ = xcb_flush(s_conn);
            Drain();
            fds[0].Revents = fds[1].Revents = 0;
            // The timeout lets stale incremental transfers lapse, as SelectionOwner's abort timer does.
            _ = poll(fds, 2, 1000);
            if (fds[1].Revents != 0)
            {
                ulong count;
                _ = read(s_wakeFd, &count, 8);
            }
        }
    }

    private static void Connect()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            throw new InvalidOperationException("The clipboard needs an X11 display (DISPLAY is not set).");
        int screen;
        s_conn = xcb_connect(null, &screen);
        if (xcb_connection_has_error(s_conn) != 0) throw new InvalidOperationException("Could not connect to the X11 display for the clipboard.");
        var roots = xcb_setup_roots_iterator(xcb_get_setup(s_conn));
        for (var i = 0; i < screen && roots.Rem > 0; i++) xcb_screen_next(&roots);
        var root = *(uint*)roots.Data;
        s_window = xcb_generate_id(s_conn);
        // Connection::CreateDummyWindow: an unmapped override-redirect InputOnly window off screen.
        var values = stackalloc uint[] { 1, PropertyChangeMask };
        _ = xcb_create_window(s_conn, 0, s_window, root, -100, -100, 10, 10, 0, 2 /* InputOnly */, 0, 0x200 | 0x800 /* OverrideRedirect | EventMask */, values);
        var name = "Chromium clipboard"u8;
        fixed (byte* p = name) _ = xcb_change_property(s_conn, 0, s_window, AtomWmName, AtomString, 8, (uint)name.Length, p);
        _ = xcb_flush(s_conn);
        s_wakeFd = eventfd(0, 0x80000 | 0x800 /* EFD_CLOEXEC | EFD_NONBLOCK */);
        if (s_wakeFd < 0) throw new InvalidOperationException("eventfd failed for the X11 clipboard thread.");
    }

    private static void Wake()
    {
        ulong one = 1;
        _ = write(s_wakeFd, &one, 8);
    }

    // Dispatches events until done() or the deadline; the nested loop keeps answering other clients meanwhile.
    private static void Pump(Func<bool> done, Func<DateTime> deadline)
    {
        var fd = new PollFd { Fd = xcb_get_file_descriptor(s_conn), Events = 1 };
        while (true)
        {
            _ = xcb_flush(s_conn);
            Drain();
            if (done() || xcb_connection_has_error(s_conn) != 0) return;
            var remaining = deadline() - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return;
            fd.Revents = 0;
            _ = poll(&fd, 1, (int)Math.Ceiling(Math.Min(remaining.TotalMilliseconds, 1000)));
        }
    }

    private static void Drain()
    {
        byte* e;
        while ((e = xcb_poll_for_event(s_conn)) != null)
        {
            try { Dispatch(e); }
            finally { free(e); }
        }
    }

    // XClipboardHelper::DispatchEvent, plus the PropertyNotify that answers ServerTime.
    private static void Dispatch(byte* e)
    {
        switch (e[0] & 0x7F)
        {
            case SelectionRequest:
                if (U32(e, 8) != s_window) return;
                var selection = U32(e, 16);
                if (selection != AtomPrimary && selection != Atom("CLIPBOARD")) return;
                OwnerFor(selection == AtomPrimary).OnSelectionRequest(U32(e, 4), U32(e, 12), selection, U32(e, 20), U32(e, 24));
                return;
            case SelectionNotify:
                if (U32(e, 8) == s_window) OnSelectionNotify(U32(e, 12), U32(e, 16), U32(e, 20));
                return;
            case PropertyNotify:
                var (window, atom, state) = (U32(e, 4), U32(e, 8), e[16]);
                if (window == s_window && atom == Atom("CHROMIUM_TIMESTAMP") && state == PropertyNewValue)
                {
                    s_serverTime = U32(e, 12);
                    return;
                }
                if (s_primaryOwner.OnPropertyEvent(window, atom, state) || s_clipboardOwner.OnPropertyEvent(window, atom, state)) return;
                if (window == s_window && atom == Atom("CHROME_SELECTION") && state == PropertyNewValue) OnRequestorProperty();
                return;
        }
    }

    private static uint U32(byte* e, int offset) => *(uint*)(e + offset);

    // ---- xcb helpers ----

    private static uint Atom(string name)
    {
        if (s_atoms.TryGetValue(name, out var atom)) return atom;
        var bytes = Encoding.UTF8.GetBytes(name);
        byte* reply;
        fixed (byte* p = bytes) reply = xcb_intern_atom_reply(s_conn, xcb_intern_atom(s_conn, 0, (ushort)bytes.Length, p), null);
        if (reply == null) return 0;
        atom = U32(reply, 8);
        free(reply);
        return s_atoms[name] = atom;
    }

    // Atom names are Latin-1 by the protocol; empty when the server has none.
    private static string AtomName(uint atom)
    {
        var reply = xcb_get_atom_name_reply(s_conn, xcb_get_atom_name(s_conn, atom), null);
        if (reply == null) return "";
        var name = Encoding.Latin1.GetString(xcb_get_atom_name_name(reply), xcb_get_atom_name_name_length(reply));
        free(reply);
        return name;
    }

    private static uint OwnerOf(uint selection)
    {
        var reply = xcb_get_selection_owner_reply(s_conn, xcb_get_selection_owner(s_conn, selection), null);
        if (reply == null) return 0;
        var owner = U32(reply, 8);
        free(reply);
        return owner;
    }

    // ui::GetRawBytesOfProperty: the whole value as the server packs it, or null when the property is missing.
    private static (byte[] Data, uint Type, byte Format)? ReadProperty(uint window, uint property)
    {
        var reply = xcb_get_property_reply(s_conn, xcb_get_property(s_conn, 0, window, property, 0, 0, uint.MaxValue), null);
        if (reply == null) return null;
        try
        {
            var format = reply[1];
            if (format == 0) return null;
            var data = new ReadOnlySpan<byte>(xcb_get_property_value(reply), xcb_get_property_value_length(reply)).ToArray();
            return (data, U32(reply, 8), format);
        }
        finally { free(reply); }
    }

    private static void SetBytes(uint window, uint property, uint type, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data) _ = xcb_change_property(s_conn, 0, window, property, type, 8, (uint)data.Length, p);
    }

    private static void SetAtoms(uint window, uint property, uint type, uint[] values)
    {
        fixed (uint* p = values) _ = xcb_change_property(s_conn, 0, window, property, type, 32, (uint)values.Length, p);
    }

    private static void SelectProperties(uint window, uint mask) => _ = xcb_change_window_attributes(s_conn, window, 0x800 /* EventMask */, &mask);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenIterator
    {
        public nint Data;
        public int Rem;
        public int Index;
    }

    [LibraryImport(LibXcb)] private static partial nint xcb_connect(byte* display, int* screen);
    [LibraryImport(LibXcb)] private static partial int xcb_connection_has_error(nint c);
    [LibraryImport(LibXcb)] private static partial nint xcb_get_setup(nint c);
    [LibraryImport(LibXcb)] private static partial ScreenIterator xcb_setup_roots_iterator(nint setup);
    [LibraryImport(LibXcb)] private static partial void xcb_screen_next(ScreenIterator* i);
    [LibraryImport(LibXcb)] private static partial uint xcb_generate_id(nint c);
    [LibraryImport(LibXcb)] private static partial int xcb_get_file_descriptor(nint c);
    [LibraryImport(LibXcb)] private static partial int xcb_flush(nint c);
    [LibraryImport(LibXcb)] private static partial uint xcb_get_maximum_request_length(nint c);
    [LibraryImport(LibXcb)] private static partial byte* xcb_poll_for_event(nint c);
    [LibraryImport(LibXcb)] private static partial uint xcb_create_window(nint c, byte depth, uint wid, uint parent, short x, short y, ushort width, ushort height, ushort border, ushort cls, uint visual, uint mask, uint* values);
    [LibraryImport(LibXcb)] private static partial uint xcb_change_window_attributes(nint c, uint window, uint mask, uint* values);
    [LibraryImport(LibXcb)] private static partial uint xcb_change_property(nint c, byte mode, uint window, uint property, uint type, byte format, uint length, void* data);
    [LibraryImport(LibXcb)] private static partial uint xcb_delete_property(nint c, uint window, uint property);
    [LibraryImport(LibXcb)] private static partial uint xcb_get_property(nint c, byte delete, uint window, uint property, uint type, uint offset, uint length);
    [LibraryImport(LibXcb)] private static partial byte* xcb_get_property_reply(nint c, uint cookie, nint* error);
    [LibraryImport(LibXcb)] private static partial byte* xcb_get_property_value(byte* reply);
    [LibraryImport(LibXcb)] private static partial int xcb_get_property_value_length(byte* reply);
    [LibraryImport(LibXcb)] private static partial uint xcb_intern_atom(nint c, byte onlyIfExists, ushort length, byte* name);
    [LibraryImport(LibXcb)] private static partial byte* xcb_intern_atom_reply(nint c, uint cookie, nint* error);
    [LibraryImport(LibXcb)] private static partial uint xcb_get_atom_name(nint c, uint atom);
    [LibraryImport(LibXcb)] private static partial byte* xcb_get_atom_name_reply(nint c, uint cookie, nint* error);
    [LibraryImport(LibXcb)] private static partial byte* xcb_get_atom_name_name(byte* reply);
    [LibraryImport(LibXcb)] private static partial int xcb_get_atom_name_name_length(byte* reply);
    [LibraryImport(LibXcb)] private static partial uint xcb_set_selection_owner(nint c, uint owner, uint selection, uint time);
    [LibraryImport(LibXcb)] private static partial uint xcb_get_selection_owner(nint c, uint selection);
    [LibraryImport(LibXcb)] private static partial byte* xcb_get_selection_owner_reply(nint c, uint cookie, nint* error);
    [LibraryImport(LibXcb)] private static partial uint xcb_convert_selection(nint c, uint requestor, uint selection, uint target, uint property, uint time);
    [LibraryImport(LibXcb)] private static partial uint xcb_send_event(nint c, byte propagate, uint destination, uint mask, byte* e);
    [LibraryImport(LibC)] private static partial int eventfd(uint initial, int flags);
    [LibraryImport(LibC)] private static partial nint read(int fd, void* buffer, nint count);
    [LibraryImport(LibC)] private static partial nint write(int fd, void* buffer, nint count);
    [LibraryImport(LibC)] private static partial int poll(PollFd* fds, nuint count, int timeout);
    [LibraryImport(LibC)] private static partial void free(void* p);
}

/// <summary>The pure parts of Chromium's X11 selection code: target names, the <c>TARGETS</c> order and the MIME list
/// built from another owner's targets.</summary>
internal static class X11SelectionRules
{
    /// <summary><c>GetTextAtomsFrom</c>, in Chromium's order of preference.</summary>
    internal static readonly string[] TextTargets = ["UTF8_STRING", "STRING", "TEXT", "text/plain", "text/plain;charset=utf-8"];

    /// <summary>Byte order of the UTF-8 encoding, as <c>std::string</c> compares.</summary>
    internal static readonly IComparer<string> Utf8Order = Comparer<string>.Create((x, y) => Encoding.UTF8.GetBytes(x).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y)));

    /// <summary><c>XClipboardHelper::OnGetAvailableMimeTypesTargetList</c>: Chromium's MIME names for the targets it
    /// knows, then every target name, sorted and unique; empty names are dropped.</summary>
    internal static List<string> MimeTypesFromTargets(IReadOnlyList<string> targets)
    {
        var types = new List<string>();
        if (TextTargets.Any(targets.Contains)) types.Add("text/plain");
        foreach (var mime in (string[])["text/html", "image/svg+xml", "text/rtf", "image/png", "text/uri-list", "chromium/x-web-custom-data"])
            if (targets.Contains(mime)) types.Add(mime);
        types.AddRange(targets.Where(t => t.Length > 0));
        return [.. types.Distinct().Order(Utf8Order)];
    }

    /// <summary><c>SelectionOwner</c>'s <c>TARGETS</c> answer: <c>TIMESTAMP</c>, <c>TARGETS</c>, <c>SAVE_TARGETS</c>,
    /// <c>MULTIPLE</c>, then the URI-list, URL and text targets it holds in that order, then the rest by atom.</summary>
    internal static List<uint> TargetsReply(IEnumerable<uint> held, Func<string, uint> atom)
    {
        var map = held.ToHashSet();
        var targets = new List<uint> { atom("TIMESTAMP"), atom("TARGETS"), atom("SAVE_TARGETS"), atom("MULTIPLE") };
        var seen = targets.ToHashSet();
        foreach (var name in (string[])["text/uri-list", "text/x-moz-url", .. TextTargets])
            if (map.Contains(atom(name)) && seen.Add(atom(name))) targets.Add(atom(name));
        foreach (var a in map.Order())
            if (seen.Add(a)) targets.Add(a);
        return targets;
    }

    /// <summary><c>GetMaxIncrementalTransferSize</c>: the largest property sent in one piece, 100 bytes under the
    /// server's request limit and at most 1 MiB; bigger payloads go out as <c>INCR</c>.</summary>
    internal static int MaxIncrementalChunk(long maxRequestBytes) => (int)Math.Min(maxRequestBytes - 100, 0x100000);
}
