using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>Chromium's guard against a gnome-keyring that would deadlock libsecret: the synchronous key's
/// <c>CanUseLibsecret</c> and <c>FreedesktopSecretKeyProvider</c>'s <c>kGnomeKeyringDeadlock</c> check. Over GDBus, which
/// libsecret has already loaded.</summary>
[SupportedOSPlatform("linux")]
public static unsafe class LinuxSecretServiceProbe
{
    private const string Gio = "libgio-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const int BusTypeSession = 2;
    // GDBus's default, the same 25 s as libdbus's that Chromium's TIMEOUT_USE_DEFAULT means.
    private const int DefaultTimeout = -1;

    /// <summary>Whether the Secret Service's default collection answers, or there is none to ask: false only when
    /// <c>org.freedesktop.secrets</c> has an owner whose default collection does not answer a read of its
    /// <c>Label</c>, and, when <paramref name="aliasMustAnswer"/> (the asynchronous provider), when <c>ReadAlias</c> fails.</summary>
    public static bool DefaultCollectionAnswers(bool aliasMustAnswer)
    {
        if (!NativeLibrary.TryLoad(Gio, out _)) return true;
        nint error = 0;
        var bus = g_bus_get_sync(BusTypeSession, 0, &error);
        if (bus == 0) return Free(error, true);
        try
        {
            var owned = Call(bus, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", ["org.freedesktop.secrets"]);
            if (owned == 0) return true;
            var hasOwner = Child(owned, 0, child => g_variant_get_boolean(child) != 0);
            if (!hasOwner) return true;

            var alias = Call(bus, "org.freedesktop.secrets", "/org/freedesktop/secrets", "org.freedesktop.Secret.Service", "ReadAlias", ["default"]);
            if (alias == 0) return !aliasMustAnswer;
            var path = Child(alias, 0, child => Marshal.PtrToStringUTF8((nint)g_variant_get_string(child, null)) ?? "");
            // No default collection: libsecret, or the provider, creates one, which cannot deadlock.
            if (path is "" or "/") return true;

            var label = Call(bus, "org.freedesktop.secrets", path, "org.freedesktop.DBus.Properties", "Get", ["org.freedesktop.Secret.Collection", "Label"]);
            if (label == 0) return false;
            return Child(label, 0, child =>
            {
                var inner = g_variant_get_variant(child);
                var isString = Marshal.PtrToStringUTF8((nint)g_variant_get_type_string(inner)) == "s";
                g_variant_unref(inner);
                return isString;
            });
        }
        finally
        {
            g_object_unref(bus);
        }
    }

    // A method call with string arguments; the reply tuple, or 0 for an error or no answer in time.
    private static nint Call(nint bus, string name, string path, string iface, string method, string[] arguments)
    {
        var children = stackalloc nint[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
            fixed (byte* text = Utf8(arguments[i])) children[i] = g_variant_new_string(text);
        var parameters = g_variant_new_tuple(children, (nuint)arguments.Length);
        nint error = 0;
        nint reply;
        fixed (byte* n = Utf8(name))
        fixed (byte* p = Utf8(path))
        fixed (byte* i = Utf8(iface))
        fixed (byte* m = Utf8(method))
            reply = g_dbus_connection_call_sync(bus, n, p, i, m, parameters, 0, 0, DefaultTimeout, 0, &error);
        return error != 0 ? Free(error, (nint)0) : reply;
    }

    private static T Child<T>(nint tuple, nuint index, Func<nint, T> read)
    {
        var child = g_variant_get_child_value(tuple, index);
        try { return read(child); }
        finally
        {
            g_variant_unref(child);
            g_variant_unref(tuple);
        }
    }

    private static T Free<T>(nint error, T result)
    {
        if (error != 0) g_error_free(error);
        return result;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

    [DllImport(Gio)] private static extern nint g_bus_get_sync(int busType, nint cancellable, nint* error);
    [DllImport(Gio)] private static extern nint g_dbus_connection_call_sync(nint connection, byte* busName, byte* objectPath, byte* interfaceName, byte* methodName, nint parameters, nint replyType, int flags, int timeoutMsec, nint cancellable, nint* error);
    [DllImport(GLib)] private static extern nint g_variant_new_string(byte* text);
    [DllImport(GLib)] private static extern nint g_variant_new_tuple(nint* children, nuint count);
    [DllImport(GLib)] private static extern nint g_variant_get_child_value(nint value, nuint index);
    [DllImport(GLib)] private static extern int g_variant_get_boolean(nint value);
    [DllImport(GLib)] private static extern byte* g_variant_get_string(nint value, nuint* length);
    [DllImport(GLib)] private static extern nint g_variant_get_variant(nint value);
    [DllImport(GLib)] private static extern byte* g_variant_get_type_string(nint value);
    [DllImport(GLib)] private static extern void g_variant_unref(nint value);
    [DllImport(GLib)] private static extern void g_error_free(nint error);
    [DllImport(GObject)] private static extern void g_object_unref(nint obj);
}
