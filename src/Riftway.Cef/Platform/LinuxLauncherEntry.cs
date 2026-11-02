using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>Electron's <c>launcher_entry</c> (shell/browser/linux): <c>com.canonical.Unity.LauncherEntry</c>
/// <c>Update</c> signals on the session bus, the de facto Linux protocol for taskbar badge counts and progress.</summary>
/// <remarks>Each signal is <c>(s app_uri, a{sv} properties)</c>, where the uri is <c>application://</c> and the app's
/// desktop name (<c>CHROME_DESKTOP</c>). It is sent on a thread of its own, as Chromium's D-Bus thread does; a session
/// bus that cannot be reached drops it silently.</remarks>
public static unsafe partial class LinuxLauncherEntry
{
    public const string Interface = "com.canonical.Unity.LauncherEntry";
    public const string ObjectPath = "/com/canonical/unity/launcherentry";
    public const string Member = "Update";

    private const string LibDBus = "libdbus-1.so.3";
    private const string LibC = "libc.so.6";
    private const int BusSession = 0;
    private const int TypeString = 's', TypeArray = 'a', TypeDictEntry = 'e', TypeVariant = 'v';
    private const int TypeInt64 = 'x', TypeBoolean = 'b', TypeDouble = 'd';

    private static readonly BlockingCollection<(string Uri, (string Key, object Value)[] Properties)> s_pending = [];
    private static readonly Lazy<Thread> s_sender = new(StartSender);
    // Sender thread only.
    private static nint s_connection;

    /// <summary>The desktop file name the launcher knows the app by: <c>CHROME_DESKTOP</c> in the native environment,
    /// which .NET's <see cref="Environment"/> does not see change. Electron's <c>platform_util::GetDesktopName</c>.</summary>
    [SupportedOSPlatform("linux")]
    public static string? DesktopName()
    {
        fixed (byte* name = "CHROME_DESKTOP\0"u8)
        {
            var value = getenv(name);
            return value == null ? null : Marshal.PtrToStringUTF8((nint)value);
        }
    }

    /// <summary>Sets <c>CHROME_DESKTOP</c> natively, where Chromium also reads it. Electron's <c>app.setDesktopName</c>.</summary>
    [SupportedOSPlatform("linux")]
    public static void SetDesktopName(string name)
    {
        fixed (byte* key = "CHROME_DESKTOP\0"u8)
        fixed (byte* value = Encoding.UTF8.GetBytes(name + "\0"))
            _ = setenv(key, value, 1);
    }

    /// <summary><c>application://</c> and the desktop name; null without one, when nothing is sent.</summary>
    public static string? AppUri(string? desktopName) => string.IsNullOrEmpty(desktopName) ? null : "application://" + desktopName;

    // In key order, as Electron's std::map writes them.
    public static (string Key, object Value)[] BadgeProperties(long count) => [("count", count), ("count-visible", count != 0)];

    public static (string Key, object Value)[] ProgressProperties(double progress)
        => [("progress", progress), ("progress-visible", progress > 0.0 && progress < 1.0)];

    /// <summary>Shows <paramref name="count"/> on the launcher entry; 0 hides it. False without a desktop name.</summary>
    [SupportedOSPlatform("linux")]
    public static bool SetBadgeCount(long count) => Emit(BadgeProperties(count));

    /// <summary>Shows <paramref name="progress"/> on the launcher entry; outside (0, 1) hides it. False without a desktop name.</summary>
    [SupportedOSPlatform("linux")]
    public static bool SetProgress(double progress) => Emit(ProgressProperties(progress));

    [SupportedOSPlatform("linux")]
    private static bool Emit((string Key, object Value)[] properties)
    {
        if (AppUri(DesktopName()) is not { } uri) return false;
        _ = s_sender.Value;
        s_pending.Add((uri, properties));
        return true;
    }

    private static Thread StartSender()
    {
        var thread = new Thread(() =>
        {
            foreach (var (uri, properties) in s_pending.GetConsumingEnumerable()) Send(uri, properties);
        }) { IsBackground = true, Name = "Riftway D-Bus" };
        thread.Start();
        return thread;
    }

    private static void Send(string uri, (string Key, object Value)[] properties)
    {
        // As dbus::Bus::Connect: retried on every send until it succeeds, and never exits the process on disconnect.
        if (s_connection == 0)
        {
            var error = stackalloc long[8];
            dbus_error_init(error);
            s_connection = dbus_bus_get_private(BusSession, error);
            dbus_error_free(error);
            if (s_connection == 0) return;
            dbus_connection_set_exit_on_disconnect(s_connection, 0);
        }
        nint message;
        fixed (byte* path = "/com/canonical/unity/launcherentry\0"u8)
        fixed (byte* iface = "com.canonical.Unity.LauncherEntry\0"u8)
        fixed (byte* member = "Update\0"u8)
            message = dbus_message_new_signal(path, iface, member);
        if (message == 0) return;
        // DBusMessageIter is 72 bytes on 64-bit; room to spare.
        var top = stackalloc long[16];
        var array = stackalloc long[16];
        var entry = stackalloc long[16];
        var variant = stackalloc long[16];
        var variantSignature = stackalloc byte[2];
        dbus_message_iter_init_append(message, top);
        AppendString(top, uri);
        fixed (byte* signature = "{sv}\0"u8) _ = dbus_message_iter_open_container(top, TypeArray, signature, array);
        foreach (var (key, value) in properties)
        {
            _ = dbus_message_iter_open_container(array, TypeDictEntry, null, entry);
            AppendString(entry, key);
            var type = value switch
            {
                long => TypeInt64,
                bool => TypeBoolean,
                double => TypeDouble,
                _ => throw new ArgumentException($"No D-Bus type for {value.GetType()}.", nameof(properties)),
            };
            variantSignature[0] = (byte)type;
            variantSignature[1] = 0;
            _ = dbus_message_iter_open_container(entry, TypeVariant, variantSignature, variant);
            switch (value)
            {
                case long l: _ = dbus_message_iter_append_basic(variant, type, &l); break;
                // dbus_bool_t is 32 bits.
                case bool b: { var v = b ? 1u : 0u; _ = dbus_message_iter_append_basic(variant, type, &v); break; }
                case double d: _ = dbus_message_iter_append_basic(variant, type, &d); break;
            }
            _ = dbus_message_iter_close_container(entry, variant);
            _ = dbus_message_iter_close_container(array, entry);
        }
        _ = dbus_message_iter_close_container(top, array);
        uint serial;
        _ = dbus_connection_send(s_connection, message, &serial);
        dbus_connection_flush(s_connection);
        dbus_message_unref(message);
    }

    private static void AppendString(void* iter, string value)
    {
        fixed (byte* bytes = Encoding.UTF8.GetBytes(value + "\0"))
        {
            var p = bytes;
            _ = dbus_message_iter_append_basic(iter, TypeString, &p);
        }
    }

    [LibraryImport(LibC)] private static partial byte* getenv(byte* name);
    [LibraryImport(LibC)] private static partial int setenv(byte* name, byte* value, int overwrite);
    [LibraryImport(LibDBus)] private static partial void dbus_error_init(void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_error_free(void* error);
    [LibraryImport(LibDBus)] private static partial nint dbus_bus_get_private(int type, void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_set_exit_on_disconnect(nint connection, uint exit);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_signal(byte* path, byte* iface, byte* name);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_init_append(nint message, void* iter);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_append_basic(void* iter, int type, void* value);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_open_container(void* iter, int type, byte* signature, void* sub);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_close_container(void* iter, void* sub);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_send(nint connection, nint message, uint* serial);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_flush(nint connection);
    [LibraryImport(LibDBus)] private static partial void dbus_message_unref(nint message);
}
