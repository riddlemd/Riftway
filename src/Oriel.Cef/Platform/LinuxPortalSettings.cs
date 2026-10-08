using System.Runtime.InteropServices;
using System.Text;

namespace Oriel.Cef.Platform;

/// <summary>What the XDG desktop portal says about appearance, as Chromium's <c>DarkModeManagerLinux</c> reads it.</summary>
/// <param name="Present">The portal answered its version query; without that Chromium never reads it.</param>
/// <param name="ColorScheme"><c>org.freedesktop.appearance</c> <c>color-scheme</c>: 0 no preference, 1 dark, 2 light;
/// null until read or when the reply was not a uint32.</param>
/// <param name="Accent"><c>accent-color</c> when inside [0, 1], as Chromium accepts it.</param>
public sealed record LinuxPortalAppearance(bool Present, uint? ColorScheme, (double R, double G, double B)? Accent)
{
    public static readonly LinuxPortalAppearance None = new(false, null, null);
}

/// <summary>A client of <c>org.freedesktop.portal.Settings</c> on the session bus, doing what Chromium 154's
/// <c>DarkModeManagerLinux</c> does: once the portal answers <c>Properties.Get(FileChooser, version)</c>, it subscribes to
/// <c>SettingChanged</c> and calls <c>Read</c> for <c>color-scheme</c> and <c>accent-color</c>.</summary>
/// <remarks>Runs on a private bus connection on a thread of its own. Whether the portal is there is decided once, at
/// <see cref="Start"/>, as Chromium's <c>PortalRegistrar</c> caches its result.</remarks>
public static unsafe partial class LinuxPortalSettings
{
    public const string Service = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";
    public const string Interface = "org.freedesktop.portal.Settings";
    public const string Namespace = "org.freedesktop.appearance";

    private const string LibDBus = "libdbus-1.so.3";
    private const int BusSession = 0;
    private const int TypeString = 's', TypeVariant = 'v', TypeUInt32 = 'u', TypeDouble = 'd', TypeStruct = 'r';
    private const int MessageSignal = 4;
    // DBUS_TIMEOUT_USE_DEFAULT, as dbus::ObjectProxy::TIMEOUT_USE_DEFAULT.
    private const int DefaultTimeout = -1;

    private static readonly Lock s_gate = new();
    private static Thread? s_thread;
    private static volatile LinuxPortalAppearance s_appearance = LinuxPortalAppearance.None;
    private static readonly ManualResetEventSlim s_settled = new();

    /// <summary>The portal's appearance settings as last read or signalled.</summary>
    public static LinuxPortalAppearance Appearance => s_appearance;

    /// <summary>Raised on the portal thread after <see cref="Appearance"/> changes.</summary>
    public static event Action? Changed;

    /// <summary>Waits until the first reads are done or the portal was found missing; for tests.</summary>
    public static bool WaitSettled(TimeSpan timeout) => s_settled.Wait(timeout);

    /// <summary>Starts the client once; later calls do nothing.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public static void Start()
    {
        lock (s_gate)
        {
            if (s_thread is not null) return;
            s_thread = new Thread(Run) { IsBackground = true, Name = "Oriel D-Bus portal" };
            s_thread.Start();
        }
    }

    /// <summary>Chromium's <c>FreedesktopColorSchemeToNativeThemeColorScheme</c> then <c>SetColorScheme</c>: true for
    /// dark, false for light, null for no preference (unknown values included), when the toolkit's own reading applies.</summary>
    public static bool? PrefersDark(uint? colorScheme) => colorScheme switch
    {
        1 => true,
        2 => false,
        _ => null,
    };

    /// <summary>Chromium's <c>SetAccentColor</c>: an accent outside [0, 1] on any channel is no accent.</summary>
    public static (double R, double G, double B)? ValidAccent(double r, double g, double b)
        => r is >= 0.0 and <= 1.0 && g is >= 0.0 and <= 1.0 && b is >= 0.0 and <= 1.0 ? (r, g, b) : null;

    /// <summary>Applies a <c>SettingChanged(namespace, key, value)</c> to <paramref name="current"/>, as
    /// <c>OnPortalSettingChanged</c>: other namespaces and keys, and values of the wrong type, leave it unchanged.</summary>
    public static LinuxPortalAppearance Apply(LinuxPortalAppearance current, string ns, string key, object? value)
    {
        if (ns != Namespace) return current;
        return key switch
        {
            "color-scheme" when value is uint scheme => current with { ColorScheme = scheme },
            "accent-color" when value is (double r, double g, double b) => current with { Accent = ValidAccent(r, g, b) },
            _ => current,
        };
    }

    private static void Run()
    {
        try
        {
            Loop();
        }
        catch (DllNotFoundException) { }
        finally { s_settled.Set(); }
    }

    private static void Loop()
    {
        var error = stackalloc long[8];
        dbus_error_init(error);
        var connection = dbus_bus_get_private(BusSession, error);
        dbus_error_free(error);
        if (connection == 0) return;
        dbus_connection_set_exit_on_disconnect(connection, 0);

        // A method call to the name auto-starts an activatable portal, as Chromium's CheckForServiceAndStart.
        if (!PortalVersion(connection)) return;
        Publish(s_appearance with { Present = true });

        fixed (byte* rule = Encoding.UTF8.GetBytes($"type='signal',sender='{Service}',path='{ObjectPath}',interface='{Interface}',member='SettingChanged'\0"))
        {
            dbus_error_init(error);
            dbus_bus_add_match(connection, rule, error);
            dbus_error_free(error);
        }
        foreach (var key in (string[])["color-scheme", "accent-color"])
            if (Read(connection, key) is { } value) Publish(Apply(s_appearance, Namespace, key, value));
        s_settled.Set();

        while (dbus_connection_read_write(connection, -1) != 0)
        {
            nint message;
            while ((message = dbus_connection_pop_message(connection)) != 0)
            {
                try { OnMessage(message); }
                finally { dbus_message_unref(message); }
            }
        }
    }

    private static void OnMessage(nint message)
    {
        if (dbus_message_get_type(message) != MessageSignal) return;
        bool isSetting;
        fixed (byte* iface = "org.freedesktop.portal.Settings\0"u8)
        fixed (byte* member = "SettingChanged\0"u8)
            isSetting = dbus_message_is_signal(message, iface, member) != 0;
        if (!isSetting) return;
        var iter = stackalloc long[16];
        if (dbus_message_iter_init(message, iter) == 0) return;
        if (ReadString(iter) is not { } ns || dbus_message_iter_next(iter) == 0) return;
        if (ReadString(iter) is not { } key || dbus_message_iter_next(iter) == 0) return;
        if (dbus_message_iter_get_arg_type(iter) != TypeVariant) return;
        var inner = stackalloc long[16];
        dbus_message_iter_recurse(iter, inner);
        Publish(Apply(s_appearance, ns, key, ReadValue(inner)));
    }

    private static void Publish(LinuxPortalAppearance next)
    {
        if (next == s_appearance) return;
        s_appearance = next;
        Changed?.Invoke();
    }

    // Chromium's PortalRegistrar::GetVersion: no uint32 reply means no portal.
    private static bool PortalVersion(nint connection)
    {
        var message = NewCall("org.freedesktop.DBus.Properties", "Get");
        if (message == 0) return false;
        var iter = stackalloc long[16];
        dbus_message_iter_init_append(message, iter);
        AppendString(iter, "org.freedesktop.portal.FileChooser");
        AppendString(iter, "version");
        var reply = Call(connection, message);
        if (reply == 0) return false;
        try
        {
            if (dbus_message_iter_init(reply, iter) == 0 || dbus_message_iter_get_arg_type(iter) != TypeVariant) return false;
            var inner = stackalloc long[16];
            dbus_message_iter_recurse(iter, inner);
            return ReadValue(inner) is uint;
        }
        finally { dbus_message_unref(reply); }
    }

    // Read returns the value wrapped in a second variant; Chromium rejects a reply without it.
    private static object? Read(nint connection, string key)
    {
        var message = NewCall(Interface, "Read");
        if (message == 0) return null;
        var iter = stackalloc long[16];
        dbus_message_iter_init_append(message, iter);
        AppendString(iter, Namespace);
        AppendString(iter, key);
        var reply = Call(connection, message);
        if (reply == 0) return null;
        try
        {
            if (dbus_message_iter_init(reply, iter) == 0 || dbus_message_iter_get_arg_type(iter) != TypeVariant) return null;
            var outer = stackalloc long[16];
            dbus_message_iter_recurse(iter, outer);
            if (dbus_message_iter_get_arg_type(outer) != TypeVariant) return null;
            var inner = stackalloc long[16];
            dbus_message_iter_recurse(outer, inner);
            return ReadValue(inner);
        }
        finally { dbus_message_unref(reply); }
    }

    // A uint32, a (ddd) struct, or null for anything else.
    private static object? ReadValue(void* iter)
    {
        switch (dbus_message_iter_get_arg_type(iter))
        {
            case TypeUInt32:
                uint u;
                dbus_message_iter_get_basic(iter, &u);
                return u;
            case TypeStruct:
                var field = stackalloc long[16];
                dbus_message_iter_recurse(iter, field);
                var rgb = stackalloc double[3];
                for (var i = 0; i < 3; i++)
                {
                    if (dbus_message_iter_get_arg_type(field) != TypeDouble) return null;
                    dbus_message_iter_get_basic(field, &rgb[i]);
                    var more = dbus_message_iter_next(field) != 0;
                    if (more != (i < 2)) return null;
                }
                return (rgb[0], rgb[1], rgb[2]);
            default:
                return null;
        }
    }

    private static string? ReadString(void* iter)
    {
        if (dbus_message_iter_get_arg_type(iter) != TypeString) return null;
        byte* value;
        dbus_message_iter_get_basic(iter, &value);
        return Marshal.PtrToStringUTF8((nint)value);
    }

    private static nint NewCall(string iface, string method)
    {
        fixed (byte* dest = Encoding.UTF8.GetBytes(Service + "\0"))
        fixed (byte* path = Encoding.UTF8.GetBytes(ObjectPath + "\0"))
        fixed (byte* i = Encoding.UTF8.GetBytes(iface + "\0"))
        fixed (byte* m = Encoding.UTF8.GetBytes(method + "\0"))
            return dbus_message_new_method_call(dest, path, i, m);
    }

    // Takes the call's reference; returns the reply or 0 on an error reply or timeout.
    private static nint Call(nint connection, nint message)
    {
        var error = stackalloc long[8];
        dbus_error_init(error);
        var reply = dbus_connection_send_with_reply_and_block(connection, message, DefaultTimeout, error);
        dbus_error_free(error);
        dbus_message_unref(message);
        return reply;
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
    [LibraryImport(LibDBus)] private static partial void dbus_bus_add_match(nint connection, byte* rule, void* error);
    [LibraryImport(LibDBus)] private static partial void dbus_connection_set_exit_on_disconnect(nint connection, uint exit);
    [LibraryImport(LibDBus)] private static partial nint dbus_connection_send_with_reply_and_block(nint connection, nint message, int timeout, void* error);
    [LibraryImport(LibDBus)] private static partial uint dbus_connection_read_write(nint connection, int timeout);
    [LibraryImport(LibDBus)] private static partial nint dbus_connection_pop_message(nint connection);
    [LibraryImport(LibDBus)] private static partial nint dbus_message_new_method_call(byte* destination, byte* path, byte* iface, byte* method);
    [LibraryImport(LibDBus)] private static partial int dbus_message_get_type(nint message);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_is_signal(nint message, byte* iface, byte* name);
    [LibraryImport(LibDBus)] private static partial void dbus_message_unref(nint message);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_init(nint message, void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_init_append(nint message, void* iter);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_append_basic(void* iter, int type, void* value);
    [LibraryImport(LibDBus)] private static partial int dbus_message_iter_get_arg_type(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_get_basic(void* iter, void* value);
    [LibraryImport(LibDBus)] private static partial uint dbus_message_iter_next(void* iter);
    [LibraryImport(LibDBus)] private static partial void dbus_message_iter_recurse(void* iter, void* sub);
}
