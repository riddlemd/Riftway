using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Oriel.Cef.Platform;

/// <summary>Electron's <c>ShowItemHelper</c> (platform_util_linux.cc): the first request decides, once, between the XDG
/// portal's <c>OpenURI.OpenDirectory</c>, <c>org.freedesktop.FileManager1.ShowItems</c>, and neither; a call that fails
/// opens the item's parent folder with <c>xdg-open</c> instead (<see cref="LinuxShellOps.OpenFolder"/>).</summary>
/// <remarks>Runs on a private session-bus connection on a thread of its own, as Chromium's D-Bus thread does. Calls block
/// that thread with libdbus's default timeout, so requests stay in order; portal responses are awaited without one.</remarks>
[SupportedOSPlatform("linux")]
public sealed unsafe partial class LinuxShowItemHelper
{
    public const string PortalService = "org.freedesktop.portal.Desktop";
    public const string FileManagerService = "org.freedesktop.FileManager1";

    private const string LibDBus = "libdbus-1.so.3";
    private const string LibC = "libc.so.6";
    private const int TypeString = 's', TypeUInt32 = 'u', TypeBoolean = 'b', TypeArray = 'a', TypeObjectPath = 'o', TypeUnixFd = 'h';
    private const int TypeVariant = 'v', TypeDictEntry = 'e', TypeInvalid = 0;
    private const int MessageMethodReturn = 2, MessageSignal = 4;
    private const int DefaultTimeout = -1;

    private enum ApiType { None, Portal, FileManager }

    private readonly string _portal;
    private readonly string _fileManager;
    private readonly BlockingCollection<string> _requests = [];
    private readonly Lazy<Thread> _thread;
    // Helper thread only from here on.
    private readonly Dictionary<string, string> _portalRequests = [];
    private nint _connection;
    private ApiType? _apiType;

    /// <summary>The instance <c>shell.showItemInFolder</c> uses, never destroyed, as Electron's.</summary>
    public static LinuxShowItemHelper Instance { get; } = new();

    /// <summary>A helper that looks for the portal and file manager under other names; Chromium's
    /// <c>CreateWithPortalServiceName</c>, for tests.</summary>
    public LinuxShowItemHelper(string portalService = PortalService, string fileManagerService = FileManagerService)
    {
        _portal = portalService;
        _fileManager = fileManagerService;
        _thread = new Lazy<Thread>(() =>
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "Oriel D-Bus show item" };
            thread.Start();
            return thread;
        });
    }

    /// <summary>Queues <paramref name="fullPath"/> to be shown; returns at once, as Electron's.</summary>
    public void ShowItemInFolder(string fullPath)
    {
        _ = _thread.Value;
        _requests.Add(fullPath);
    }

    private void Loop()
    {
        var error = stackalloc long[8];
        dbus_error_init(error);
        _connection = dbus_bus_get_private(0 /* DBUS_BUS_SESSION */, error);
        dbus_error_free(error);
        if (_connection != 0) dbus_connection_set_exit_on_disconnect(_connection, 0);

        while (true)
        {
            if (_portalRequests.Count == 0)
            {
                Show(_requests.Take());
                continue;
            }
            while (_requests.TryTake(out var path)) Show(path);
            if (dbus_connection_read_write(_connection, 50) == 0) _portalRequests.Clear();
            nint message;
            while ((message = dbus_connection_pop_message(_connection)) != 0)
            {
                try { OnMessage(message); }
                finally { dbus_message_unref(message); }
            }
        }
    }

    private void Show(string fullPath)
    {
        // The portal is always preferred; an error asking the bus counts as "not running".
        _apiType ??= CheckForServiceAndStart(_portal) == true ? ApiType.Portal
            : CheckForServiceAndStart(_fileManager) == true ? ApiType.FileManager
            : ApiType.None;
        switch (_apiType)
        {
            case ApiType.Portal: ShowItemUsingPortal(fullPath); break;
            case ApiType.FileManager: ShowItemUsingFileManager(fullPath); break;
            default: OpenParentFolderFallback(fullPath); break;
        }
    }

    private static void OpenParentFolderFallback(string fullPath) => LinuxShellOps.OpenFolder(LinuxShellRules.DirName(fullPath));

    private void ShowItemUsingPortal(string fullPath)
    {
        int fd;
        fixed (byte* p = Encoding.UTF8.GetBytes(fullPath + "\0")) fd = open(p, 0x80000 /* O_RDONLY | O_CLOEXEC */);
        if (fd < 0)
        {
            OpenParentFolderFallback(fullPath);
            return;
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var requestPath = LinuxShellRules.PortalRequestPath(UniqueName(), token);
        // The Response signal is subscribed to before the call, so it cannot arrive unseen.
        var connected = AddResponseMatch(requestPath, add: true);
        var message = NewCall(_portal, "/org/freedesktop/portal/desktop", "org.freedesktop.portal.OpenURI", "OpenDirectory");
        nint reply = 0;
        if (message != 0)
        {
            var top = stackalloc long[16];
            dbus_message_iter_init_append(message, top);
            AppendString(top, "");
            _ = dbus_message_iter_append_basic(top, TypeUnixFd, &fd);
            // std::map order; the activation token is empty without a Wayland token creator.
            AppendOptions(top, [("activation_token", ""), ("handle_token", token)]);
            reply = Call(message);
        }
        _ = close(fd);

        if (!connected)
        {
            if (reply != 0) dbus_message_unref(reply);
            AddResponseMatch(requestPath, add: false);
            OpenParentFolderFallback(fullPath);
            return;
        }
        var returned = reply == 0 ? null : ReadSingleObjectPath(reply);
        if (reply != 0) dbus_message_unref(reply);
        if (returned is null)
        {
            AddResponseMatch(requestPath, add: false);
            OpenParentFolderFallback(fullPath);
            return;
        }
        // Portals before 0.9 ignore handle_token and answer on a path of their own.
        if (returned != requestPath)
        {
            AddResponseMatch(requestPath, add: false);
            requestPath = returned;
            if (!AddResponseMatch(requestPath, add: true))
            {
                OpenParentFolderFallback(fullPath);
                return;
            }
        }
        _portalRequests[requestPath] = fullPath;
    }

    private void OnMessage(nint message)
    {
        if (dbus_message_get_type(message) != MessageSignal) return;
        bool isResponse;
        fixed (byte* iface = "org.freedesktop.portal.Request\0"u8)
        fixed (byte* member = "Response\0"u8)
            isResponse = dbus_message_is_signal(message, iface, member) != 0;
        if (!isResponse || Marshal.PtrToStringUTF8((nint)dbus_message_get_path(message)) is not { } path
            || !_portalRequests.Remove(path, out var fullPath)) return;
        AddResponseMatch(path, add: false);

        var iter = stackalloc long[16];
        if (dbus_message_iter_init(message, iter) == 0 || dbus_message_iter_get_arg_type(iter) != TypeUInt32)
        {
            OpenParentFolderFallback(fullPath);
            return;
        }
        uint code;
        dbus_message_iter_get_basic(iter, &code);
        // 1 is the user cancelling, which Electron leaves alone; 0 needs its results dictionary to count as success.
        var ok = code switch
        {
            0 => dbus_message_iter_next(iter) != 0 && Signature(iter) == "a{sv}",
            1 => true,
            _ => false,
        };
        if (!ok) OpenParentFolderFallback(fullPath);
    }

    private void ShowItemUsingFileManager(string fullPath)
    {
        var message = NewCall(_fileManager, "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1", "ShowItems");
        if (message == 0) return;
        var top = stackalloc long[16];
        var array = stackalloc long[16];
        dbus_message_iter_init_append(message, top);
        fixed (byte* s = "s\0"u8) _ = dbus_message_iter_open_container(top, TypeArray, s, array);
        AppendString(array, LinuxShellRules.FileUri(fullPath));
        _ = dbus_message_iter_close_container(top, array);
        AppendString(top, ""); // startup-id
        var reply = Call(message);
        // dbus_utils::CallMethod<..., ""> also refuses a reply that carries anything.
        var iter = stackalloc long[16];
        var ok = reply != 0 && dbus_message_iter_init(reply, iter) == 0;
        if (reply != 0) dbus_message_unref(reply);
        if (!ok) OpenParentFolderFallback(fullPath);
    }

    // dbus_utils::CheckForServiceAndStart: running, or activatable and started; null when the bus did not answer.
    private bool? CheckForServiceAndStart(string name)
    {
        var owned = CallBus("NameHasOwner", name, null, reply => ReadBasic(reply, TypeBoolean) is { } b ? b != 0 : (bool?)null);
        if (owned ?? true) return owned;
        var activatable = CallBus("ListActivatableNames", null, null, ReadStrings);
        if (activatable is null) return null;
        if (!activatable.Contains(name)) return false;
        // DBUS_START_REPLY_SUCCESS or DBUS_START_REPLY_ALREADY_RUNNING.
        return CallBus("StartServiceByName", name, 0u, reply => ReadBasic(reply, TypeUInt32) is { } r ? r is 1 or 2 : (bool?)null);
    }

    private T? CallBus<T>(string method, string? name, uint? flags, Func<nint, T?> read)
    {
        if (_connection == 0) return default;
        var message = NewCall("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", method);
        if (message == 0) return default;
        var iter = stackalloc long[16];
        dbus_message_iter_init_append(message, iter);
        if (name is not null) AppendString(iter, name);
        if (flags is { } f) _ = dbus_message_iter_append_basic(iter, TypeUInt32, &f);
        var reply = Call(message);
        if (reply == 0) return default;
        try { return read(reply); }
        finally { dbus_message_unref(reply); }
    }

    private static uint? ReadBasic(nint reply, int type)
    {
        var iter = stackalloc long[16];
        if (dbus_message_iter_init(reply, iter) == 0 || dbus_message_iter_get_arg_type(iter) != type) return null;
        uint value;
        dbus_message_iter_get_basic(iter, &value);
        return value;
    }

    private static List<string>? ReadStrings(nint reply)
    {
        var iter = stackalloc long[16];
        if (dbus_message_iter_init(reply, iter) == 0 || dbus_message_iter_get_arg_type(iter) != TypeArray
            || dbus_message_iter_get_element_type(iter) != TypeString) return null;
        var element = stackalloc long[16];
        dbus_message_iter_recurse(iter, element);
        var names = new List<string>();
        while (dbus_message_iter_get_arg_type(element) == TypeString)
        {
            byte* s;
            dbus_message_iter_get_basic(element, &s);
            names.Add(Marshal.PtrToStringUTF8((nint)s)!);
            _ = dbus_message_iter_next(element);
        }
        return names;
    }

    // A request's reply is one object path and nothing more.
    private static string? ReadSingleObjectPath(nint reply)
    {
        var iter = stackalloc long[16];
        if (dbus_message_iter_init(reply, iter) == 0 || dbus_message_iter_get_arg_type(iter) != TypeObjectPath) return null;
        byte* s;
        dbus_message_iter_get_basic(iter, &s);
        var path = Marshal.PtrToStringUTF8((nint)s);
        return dbus_message_iter_next(iter) != 0 ? null : path;
    }

    private static string? Signature(void* iter)
    {
        var signature = dbus_message_iter_get_signature(iter);
        if (signature == null) return null;
        try { return Marshal.PtrToStringUTF8((nint)signature); }
        finally { dbus_free(signature); }
    }

    // dbus::ObjectProxy::ConnectToSignal's match rule.
    private bool AddResponseMatch(string path, bool add)
    {
        if (_connection == 0) return false;
        var error = stackalloc long[8];
        dbus_error_init(error);
        fixed (byte* rule = Encoding.UTF8.GetBytes($"type='signal',sender='{_portal}',interface='org.freedesktop.portal.Request',path='{path}'\0"))
        {
            if (add) dbus_bus_add_match(_connection, rule, error);
            else dbus_bus_remove_match(_connection, rule, error);
        }
        var ok = dbus_error_is_set(error) == 0;
        dbus_error_free(error);
        return ok;
    }

    private string UniqueName() => _connection == 0 ? "" : Marshal.PtrToStringUTF8((nint)dbus_bus_get_unique_name(_connection)) ?? "";

    private static void AppendOptions(void* iter, (string Key, string Value)[] options)
    {
        var array = stackalloc long[16];
        var entry = stackalloc long[16];
        var variant = stackalloc long[16];
        fixed (byte* sig = "{sv}\0"u8) _ = dbus_message_iter_open_container(iter, TypeArray, sig, array);
        foreach (var (key, value) in options)
        {
            _ = dbus_message_iter_open_container(array, TypeDictEntry, null, entry);
            AppendString(entry, key);
            fixed (byte* s = "s\0"u8) _ = dbus_message_iter_open_container(entry, TypeVariant, s, variant);
            AppendString(variant, value);
            _ = dbus_message_iter_close_container(entry, variant);
            _ = dbus_message_iter_close_container(array, entry);
        }
        _ = dbus_message_iter_close_container(iter, array);
    }

    private static void AppendString(void* iter, string value)
    {
        fixed (byte* bytes = Encoding.UTF8.GetBytes(value + "\0"))
        {
            var p = bytes;
            _ = dbus_message_iter_append_basic(iter, TypeString, &p);
        }
    }

    private static nint NewCall(string destination, string path, string iface, string method)
    {
        fixed (byte* d = Encoding.UTF8.GetBytes(destination + "\0"))
        fixed (byte* p = Encoding.UTF8.GetBytes(path + "\0"))
        fixed (byte* i = Encoding.UTF8.GetBytes(iface + "\0"))
        fixed (byte* m = Encoding.UTF8.GetBytes(method + "\0"))
            return dbus_message_new_method_call(d, p, i, m);
    }

    // Takes the call's reference; returns the method return, or 0 on an error reply, a timeout or no bus.
    private nint Call(nint message)
    {
        if (_connection == 0)
        {
            dbus_message_unref(message);
            return 0;
        }
        var error = stackalloc long[8];
        dbus_error_init(error);
        var reply = dbus_connection_send_with_reply_and_block(_connection, message, DefaultTimeout, error);
        dbus_error_free(error);
        dbus_message_unref(message);
        if (reply != 0 && dbus_message_get_type(reply) != MessageMethodReturn)
        {
            dbus_message_unref(reply);
            return 0;
        }
        return reply;
    }

    [LibraryImport(LibC)] private static partial int open(byte* path, int flags);
    [LibraryImport(LibC)] private static partial int close(int fd);
    [LibraryImport(LibDBus)] private static partial void dbus_error_init(void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_error_free(void* error);
    [LibraryImport(LibDBus)] private static partial uint dbus_error_is_set(void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_free(void* memory);
    [LibraryImport(LibDBus)] private static partial nint dbus_bus_get_private(int type, void* error);
    [LibraryImport(LibDBus)] private static partial byte* dbus_bus_get_unique_name(nint connection);
    [LibraryImport(LibDBus)] private static partial void dbus_bus_add_match(nint connection, byte* rule, void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_bus_remove_match(nint connection, byte* rule, void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_set_exit_on_disconnect(nint connection, uint exit);
    [LibraryImport(LibDBus)] private static partial nint dbus_connection_send_with_reply_and_block(nint connection, nint message, int timeout, void* error);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_read_write(nint connection, int timeout);
    [LibraryImport(LibDBus)] private static partial nint dbus_connection_pop_message(nint connection);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_method_call(byte* destination, byte* path, byte* iface, byte* method);
    [LibraryImport(LibDBus)] private static partial int dbus_message_get_type(nint message);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_get_path(nint message);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_is_signal(nint message, byte* iface, byte* name);
    [LibraryImport(LibDBus)] private static partial void dbus_message_unref(nint message);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_init(nint message, void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_init_append(nint message, void* iter);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_append_basic(void* iter, int type, void* value);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_open_container(void* iter, int type, byte* signature, void* sub);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_close_container(void* iter, void* sub);
    [LibraryImport(LibDBus)] private static partial int dbus_message_iter_get_arg_type(void* iter);
    [LibraryImport(LibDBus)] private static partial int dbus_message_iter_get_element_type(void* iter);
    [LibraryImport(LibDBus)] private static partial byte* dbus_message_iter_get_signature(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_get_basic(void* iter, void* value);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_next(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_recurse(void* iter, void* sub);
}
