using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>The Secret Service through libsecret, making the calls Chromium's os_crypt makes: <c>KeyStorageLibsecret</c>
/// and <c>LibsecretLoader</c> for the synchronous key, and the item lookup of os_crypt_async's
/// <c>FreedesktopSecretKeyProvider</c> for the asynchronous one. Both find the same item, by its "application" attribute.</summary>
[SupportedOSPlatform("linux")]
public static unsafe class LinuxLibsecret
{
    private const string Libsecret = "libsecret-1.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";

    private const int SchemaNone = 0;
    private const int SchemaDontMatchName = 1 << 1;
    private const int SearchAll = 1 << 1;
    private const int SearchUnlock = 1 << 2;
    private const int SearchLoadSecrets = 1 << 3;
    private const int ServiceOpenSession = 1 << 1;

    /// <summary>Chromium's item: label "Chromium Safe Storage" (Electron builds unbranded), schema
    /// <c>chrome_libsecret_os_crypt_password_v2</c>, attribute "application".</summary>
    internal const string SchemaName = "chrome_libsecret_os_crypt_password_v2";
    internal const string SyncItemLabel = "Chromium Safe Storage";

    private static readonly SecretSchema* s_keystoreSchemaV2 = NewSchema(SchemaName, SchemaDontMatchName, "application");
    private static readonly SecretSchema* s_dummySchema = NewSchema("_chrome_dummy_schema", SchemaDontMatchName, "application");
    private static readonly SecretSchema* s_unlockSchema = NewSchema("_chrome_dummy_schema_for_unlocking", SchemaNone, "explanation");

    private const string ExplanationMessage = "Because of quirks in the gnome libsecret API, Chrome needs to store a "
        + "dummy entry to guarantee that this keyring was properly unlocked. More details at http://crbug.com/660005.";

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchemaAttribute
    {
        public byte* Name;
        public int Type;
    }

    [InlineArray(32)]
    private struct SchemaAttributes
    {
        private SecretSchemaAttribute _element;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecretSchema
    {
        public byte* Name;
        public int Flags;
        public SchemaAttributes Attributes;
        public int Reserved;
        public nint Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GList
    {
        public nint Data;
        public GList* Next;
        public GList* Prev;
    }

    [DllImport(Libsecret)] private static extern GList* secret_service_search_sync(nint service, SecretSchema* schema, nint attributes, int flags, nint cancellable, nint* error);
    [DllImport(Libsecret)] private static extern int secret_password_storev_sync(SecretSchema* schema, nint attributes, byte* collection, byte* label, byte* password, nint cancellable, nint* error);
    [DllImport(Libsecret)] private static extern nint secret_item_get_secret(nint item);
    [DllImport(Libsecret)] private static extern byte* secret_value_get_text(nint value);
    [DllImport(Libsecret)] private static extern byte* secret_value_get(nint value, nuint* length);
    [DllImport(Libsecret)] private static extern nint secret_value_new(byte* secret, nint length, byte* contentType);
    [DllImport(Libsecret)] private static extern void secret_value_unref(nint value);
    [DllImport(Libsecret)] private static extern nint secret_service_get_sync(int flags, nint cancellable, nint* error);
    [DllImport(Libsecret)] private static extern nint secret_collection_for_alias_sync(nint service, byte* alias, int flags, nint cancellable, nint* error);
    [DllImport(Libsecret)] private static extern nint secret_collection_create_sync(nint service, byte* label, byte* alias, int flags, nint cancellable, nint* error);
    [DllImport(Libsecret)] private static extern GList* secret_collection_search_sync(nint collection, SecretSchema* schema, nint attributes, int flags, nint cancellable, nint* error);
    [DllImport(Libsecret)] private static extern nint secret_item_create_sync(nint collection, SecretSchema* schema, nint attributes, byte* label, nint value, int flags, nint cancellable, nint* error);
    [DllImport(Libsecret)] private static extern int secret_service_unlock_sync(nint service, GList* objects, nint cancellable, GList** unlocked, nint* error);
    [DllImport(GLib)] private static extern nint g_hash_table_new(nint hash, nint equal);
    [DllImport(GLib)] private static extern int g_hash_table_insert(nint table, nint key, nint value);
    [DllImport(GLib)] private static extern void g_hash_table_unref(nint table);
    [DllImport(GLib)] private static extern void g_list_free_full(GList* list, nint destroy);
    [DllImport(GLib)] private static extern void g_list_free(GList* list);
    [DllImport(GLib)] private static extern GList* g_list_append(GList* list, nint data);
    [DllImport(GLib)] private static extern void g_error_free(nint error);
    [DllImport(GObject)] private static extern void g_object_unref(nint obj);

    private static readonly Lazy<bool> s_loaded = new(() => NativeLibrary.TryLoad(Libsecret, out _));

    /// <summary><c>KeyStorageLibsecret</c>'s <c>Init()</c> then <c>GetKey()</c>: libsecret must load and answer a search,
    /// the default keyring is unlocked with Chromium's dummy entry, and then the item for <paramref name="appName"/> is read,
    /// or created with a new random password. Null when any step fails.</summary>
    public static byte[]? GetOsCryptPassword(string appName)
    {
        // CanUseLibsecret: a keyring whose default collection does not answer would block every call below.
        if (!s_loaded.Value || !LinuxSecretServiceProbe.DefaultCollectionAnswers(aliasMustAnswer: false) || !LibsecretIsAvailable()) return null;
        EnsureKeyringUnlocked();

        using var attributes = new Attributes(("application", appName));
        nint error = 0;
        var items = secret_service_search_sync(0, s_keystoreSchemaV2, attributes.Table, SearchUnlock | SearchLoadSecrets, 0, &error);
        if (error != 0)
        {
            g_error_free(error);
            FreeObjects(items);
            return null;
        }
        try
        {
            if (items is not null)
            {
                var value = secret_item_get_secret(items->Data);
                if (value == 0) return null;
                try
                {
                    var text = secret_value_get_text(value);
                    return text is null ? null : Encoding.UTF8.GetBytes(Marshal.PtrToStringUTF8((nint)text) ?? "");
                }
                finally
                {
                    secret_value_unref(value);
                }
            }
        }
        finally
        {
            FreeObjects(items);
        }
        return AddRandomPassword(appName);
    }

    /// <summary><c>FreedesktopSecretKeyProvider</c>'s lookup: the "default" collection (created as "Default Keyring" when
    /// missing) is unlocked, then its first item with "application" = <paramref name="appName"/> gives its raw secret, or
    /// an item labelled <paramref name="keyName"/> is created with a new random secret. Null when any step fails.</summary>
    public static byte[]? GetDefaultCollectionSecret(string appName, string keyName)
    {
        // kGnomeKeyringDeadlock and kReadAliasFailed: the provider gives up, leaving "v10".
        if (!s_loaded.Value || !LinuxSecretServiceProbe.DefaultCollectionAnswers(aliasMustAnswer: true)) return null;
        nint error = 0;
        var service = secret_service_get_sync(ServiceOpenSession, 0, &error);
        if (error != 0 || service == 0) return Fail(error, service);
        try
        {
            return GetDefaultCollectionSecret(service, appName, keyName);
        }
        finally
        {
            g_object_unref(service);
        }
    }

    private static byte[]? GetDefaultCollectionSecret(nint service, string appName, string keyName)
    {
        nint error = 0;
        nint collection;
        fixed (byte* alias = Utf8("default")) collection = secret_collection_for_alias_sync(service, alias, 0, 0, &error);
        if (error != 0) return Fail(error, collection);
        if (collection == 0)
        {
            fixed (byte* label = Utf8("Default Keyring"))
            fixed (byte* alias = Utf8("default"))
                collection = secret_collection_create_sync(service, label, alias, 0, 0, &error);
            if (error != 0 || collection == 0) return Fail(error, collection);
        }
        try
        {
            var objects = g_list_append(null, collection);
            secret_service_unlock_sync(service, objects, 0, null, &error);
            g_list_free(objects);
            if (error != 0) return Fail(error, 0);

            using var search = new Attributes(("application", appName));
            var items = secret_collection_search_sync(collection, null, search.Table, SearchUnlock | SearchLoadSecrets, 0, &error);
            if (error != 0)
            {
                FreeObjects(items);
                return Fail(error, 0);
            }
            try
            {
                if (items is not null)
                {
                    var value = secret_item_get_secret(items->Data);
                    if (value == 0) return null;
                    try
                    {
                        nuint length;
                        var bytes = secret_value_get(value, &length);
                        return length == 0 ? null : new ReadOnlySpan<byte>(bytes, (int)length).ToArray();
                    }
                    finally
                    {
                        secret_value_unref(value);
                    }
                }
            }
            finally
            {
                FreeObjects(items);
            }

            var secret = NewPassword();
            using var attributes = new Attributes(("application", appName), ("xdg:schema", SchemaName));
            nint created;
            fixed (byte* secretBytes = secret)
            fixed (byte* type = Utf8("text/plain"))
            fixed (byte* label = Utf8(keyName))
            {
                var value = secret_value_new(secretBytes, secret.Length, type);
                created = secret_item_create_sync(collection, null, attributes.Table, label, value, 0, 0, &error);
                secret_value_unref(value);
            }
            if (error != 0 || created == 0) return Fail(error, created);
            g_object_unref(created);
            return secret;
        }
        finally
        {
            g_object_unref(collection);
        }
    }

    private static byte[]? Fail(nint error, nint obj)
    {
        if (error != 0) g_error_free(error);
        if (obj != 0) g_object_unref(obj);
        return null;
    }

    // LibsecretIsAvailable(): libsecret has no availability call, so a search meant to find nothing stands in.
    private static bool LibsecretIsAvailable()
    {
        using var attributes = new Attributes(("application", "chrome-string_to_get_empty_result"));
        nint error = 0;
        var items = secret_service_search_sync(0, s_dummySchema, attributes.Table, SearchAll | SearchUnlock, 0, &error);
        FreeObjects(items);
        if (error == 0) return true;
        g_error_free(error);
        return false;
    }

    // crbug.com/660005: storing a dummy entry is what reliably unlocks the default keyring.
    private static void EnsureKeyringUnlocked()
    {
        using var attributes = new Attributes(("explanation", ExplanationMessage));
        nint error = 0;
        fixed (byte* label = Utf8("Chrome Safe Storage Control"))
        fixed (byte* password = Utf8("The meaning of life"))
            secret_password_storev_sync(s_unlockSchema, attributes.Table, null, label, password, 0, &error);
        if (error != 0) g_error_free(error);
    }

    private static byte[]? AddRandomPassword(string appName)
    {
        var password = NewPassword();
        using var attributes = new Attributes(("application", appName));
        nint error = 0;
        int stored;
        fixed (byte* label = Utf8(SyncItemLabel))
        fixed (byte* text = Utf8(Encoding.ASCII.GetString(password)))
            stored = secret_password_storev_sync(s_keystoreSchemaV2, attributes.Table, null, label, text, 0, &error);
        if (error != 0)
        {
            g_error_free(error);
            return null;
        }
        return stored != 0 ? password : null;
    }

    private static byte[] NewPassword()
        => Encoding.ASCII.GetBytes(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));

    private static void FreeObjects(GList* list)
    {
        if (list is not null) g_list_free_full(list, NativeLibrary.GetExport(NativeLibrary.Load(GObject), "g_object_unref"));
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

    // Kept for the process, as Chromium's schemas are static.
    private static SecretSchema* NewSchema(string name, int flags, string attribute)
    {
        var schema = (SecretSchema*)NativeMemory.AllocZeroed((nuint)sizeof(SecretSchema));
        schema->Name = (byte*)Marshal.StringToCoTaskMemUTF8(name);
        schema->Flags = flags;
        schema->Attributes[0].Name = (byte*)Marshal.StringToCoTaskMemUTF8(attribute);
        return schema;
    }

    /// <summary>A <c>GHashTable</c> of UTF-8 attributes whose strings live as long as it does.</summary>
    private sealed class Attributes : IDisposable
    {
        private readonly List<nint> _strings = [];

        public Attributes(params ReadOnlySpan<(string Name, string Value)> pairs)
        {
            var glib = NativeLibrary.Load(GLib);
            Table = g_hash_table_new(NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal"));
            foreach (var (name, value) in pairs)
            {
                var key = Marshal.StringToCoTaskMemUTF8(name);
                var text = Marshal.StringToCoTaskMemUTF8(value);
                _strings.Add(key);
                _strings.Add(text);
                g_hash_table_insert(Table, key, text);
            }
        }

        public nint Table { get; }

        public void Dispose()
        {
            g_hash_table_unref(Table);
            foreach (var s in _strings) Marshal.FreeCoTaskMem(s);
        }
    }
}
