using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>A shortcut's fields as Windows stores them; null leaves a field alone on update and reads as unset.</summary>
public sealed record WindowsShortcut
{
    public string? Target { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? Arguments { get; init; }
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public int? IconIndex { get; init; }
    public string? AppUserModelId { get; init; }
    public Guid? ToastActivatorClsid { get; init; }
}

/// <summary>Electron's Windows shell operations, as Chromium and Electron do them. Call on <see cref="WindowsSta"/>.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsShellOps
{
    private static readonly Guid s_clsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid s_iidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid s_iidPersistFile = new("0000010B-0000-0000-C000-000000000046");
    private static readonly Guid s_iidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid s_clsidFileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static readonly Guid s_iidFileOperation = new("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8");
    private static readonly Guid s_iidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid s_iidSink = new("04B0F1A7-9490-44BC-96E1-4296A31252E2");
    private static readonly Guid s_iidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid s_appUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    /// <summary>Opens <paramref name="url"/> with the scheme's registered handler; null on success, else the reason.
    /// Chromium refuses schemes with no handler under <c>HKEY_CLASSES_ROOT</c>.</summary>
    public static string? OpenExternal(string url, string? workingDirectory)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "Invalid URL";
        if (!SchemeRegistered(uri.Scheme)) return "Invalid URL scheme";
        return Execute(url, workingDirectory) is { } error ? "Failed to open: " + error : null;
    }

    /// <summary>Opens a file or folder with its default handler; the reason it failed, or empty.</summary>
    public static string OpenPath(string path)
        => File.Exists(path) || Directory.Exists(path) ? Execute(path, null) ?? "" : "Failed to open path";

    /// <summary>Opens the folder holding <paramref name="path"/> with the item selected; just the folder when the item
    /// cannot be selected.</summary>
    public static void ShowItemInFolder(string path)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full);
        if (folder is null) return;
        void* folderList = null, itemList = null;
        try
        {
            fixed (char* f = folder, i = full)
            {
                if (SHParseDisplayName(f, null, &folderList, 0, null) < 0) return;
                if (SHParseDisplayName(i, null, &itemList, 0, null) < 0 || SHOpenFolderAndSelectItems(folderList, 1, &itemList, 0) < 0)
                    _ = Execute(folder, null);
            }
        }
        finally
        {
            CoTaskMemFree(folderList);
            CoTaskMemFree(itemList);
        }
    }

    /// <summary>Moves a file or folder to the Recycle Bin; null on success, else the reason. Refuses an item Windows would
    /// delete for good instead (too big, or a volume without a bin), as Electron does.</summary>
    public static string? Trash(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return "Path does not exist";
        void* operation = null, item = null;
        try
        {
            var clsid = s_clsidFileOperation;
            var iid = s_iidFileOperation;
            if (CoCreateInstance(&clsid, null, 1, &iid, &operation) < 0) return "Failed to create the file operation";
            // FOF_NO_UI | FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE.
            if (Call(operation, 5, 0x0614u | 0x40u | 0x00080000u) < 0) return "Failed to set the file operation flags";
            var itemIid = s_iidShellItem;
            fixed (char* p = Path.GetFullPath(path))
                if (SHCreateItemFromParsingName(p, null, &itemIid, &item) < 0) return "Failed to find the item";
            var sink = RecycleOnlySink.Create();
            try
            {
                if (((delegate* unmanaged<void*, void*, void*, int>)Slot(operation, 18))(operation, item, sink) < 0) return "Failed to queue the item";
                var performed = ((delegate* unmanaged<void*, int>)Slot(operation, 21))(operation);
                int aborted;
                ((delegate* unmanaged<void*, int*, int>)Slot(operation, 22))(operation, &aborted);
                return performed < 0 || aborted != 0 ? "Failed to move the item to the Recycle Bin" : null;
            }
            finally { Release(sink); }
        }
        finally
        {
            Release(item);
            Release(operation);
        }
    }

    /// <summary>The default system sound; Electron's <c>shell.beep()</c>.</summary>
    public static bool Beep() => MessageBeep(0 /* MB_OK */) != 0;

    /// <summary>Writes a .lnk file: <paramref name="load"/> starts from the existing one (update), else a new one. False if
    /// Windows refused.</summary>
    public static bool WriteShortcut(string path, WindowsShortcut fields, bool load)
    {
        void* link = null, file = null;
        try
        {
            if ((link = CreateLink()) is null || (file = Query(link, s_iidPersistFile)) is null) return false;
            fixed (char* p = path)
                if (load && Call(file, 5, p, 2u /* STGM_READWRITE */) < 0) return false;
            if (fields.Target is { } target && !SetString(link, 20, target)) return false;
            if (fields.WorkingDirectory is { } cwd && !SetString(link, 9, cwd)) return false;
            if (fields.Arguments is { } args && !SetString(link, 11, args)) return false;
            if (fields.Description is { } description && !SetString(link, 7, description)) return false;
            if (fields.Icon is not null || fields.IconIndex is not null)
            {
                var (icon, index) = (fields.Icon, fields.IconIndex ?? 0);
                if (icon is null) icon = ReadIcon(link).Icon ?? "";
                fixed (char* i = icon)
                    if (((delegate* unmanaged<void*, char*, int, int>)Slot(link, 17))(link, i, index) < 0) return false;
            }
            if ((fields.AppUserModelId is not null || fields.ToastActivatorClsid is not null) && !SetAppUserModel(link, fields)) return false;
            fixed (char* p = path)
                return Call(file, 6, p, 1u) >= 0;
        }
        finally
        {
            Release(file);
            Release(link);
        }
    }

    /// <summary>Reads a .lnk file. Throws <see cref="IOException"/> if it cannot be loaded.</summary>
    public static WindowsShortcut ReadShortcut(string path)
    {
        void* link = null, file = null;
        try
        {
            if ((link = CreateLink()) is null || (file = Query(link, s_iidPersistFile)) is null) throw new IOException("Failed to create a shell link.");
            fixed (char* p = path)
                if (Call(file, 5, p, 0u /* STGM_READ */) < 0) throw new IOException($"Failed to read the shortcut link '{path}'.");
            var buffer = stackalloc char[1024];
            string? Get(int slot, bool withFindData)
            {
                buffer[0] = '\0';
                var hr = withFindData
                    ? ((delegate* unmanaged<void*, char*, int, void*, uint, int>)Slot(link, slot))(link, buffer, 1024, null, 0)
                    : ((delegate* unmanaged<void*, char*, int, int>)Slot(link, slot))(link, buffer, 1024);
                return hr >= 0 && buffer[0] != '\0' ? new string(buffer) : null;
            }
            var (icon, index) = ReadIcon(link);
            var (aumid, toast) = ReadAppUserModel(link);
            return new WindowsShortcut
            {
                Target = Get(3, withFindData: true),
                WorkingDirectory = Get(8, false),
                Arguments = Get(10, false),
                Description = Get(6, false),
                Icon = icon,
                IconIndex = index,
                AppUserModelId = aumid,
                ToastActivatorClsid = toast,
            };
        }
        finally
        {
            Release(file);
            Release(link);
        }
    }

    private static (string? Icon, int Index) ReadIcon(void* link)
    {
        var buffer = stackalloc char[1024];
        buffer[0] = '\0';
        int index;
        var hr = ((delegate* unmanaged<void*, char*, int, int*, int>)Slot(link, 16))(link, buffer, 1024, &index);
        return hr >= 0 ? (buffer[0] != '\0' ? new string(buffer) : null, index) : (null, 0);
    }

    private static bool SetAppUserModel(void* link, WindowsShortcut fields)
    {
        var store = Query(link, s_iidPropertyStore);
        if (store is null) return false;
        try
        {
            if (fields.AppUserModelId is { } id)
            {
                var key = new PropertyKey { Format = s_appUserModel, Id = 5 };
                var value = new PropVariant { Vt = 31 /* VT_LPWSTR */, Pointer = (void*)Marshal.StringToCoTaskMemUni(id) };
                var hr = ((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, 6))(store, &key, &value);
                PropVariantClear(&value);
                if (hr < 0) return false;
            }
            if (fields.ToastActivatorClsid is { } clsid)
            {
                var key = new PropertyKey { Format = s_appUserModel, Id = 26 };
                var guid = (Guid*)Marshal.AllocCoTaskMem(sizeof(Guid));
                *guid = clsid;
                var value = new PropVariant { Vt = 72 /* VT_CLSID */, Pointer = guid };
                var hr = ((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, 6))(store, &key, &value);
                PropVariantClear(&value);
                if (hr < 0) return false;
            }
            return ((delegate* unmanaged<void*, int>)Slot(store, 7))(store) >= 0;
        }
        finally { Release(store); }
    }

    private static (string? Id, Guid? Toast) ReadAppUserModel(void* link)
    {
        var store = Query(link, s_iidPropertyStore);
        if (store is null) return (null, null);
        try
        {
            string? id = null;
            Guid? toast = null;
            var key = new PropertyKey { Format = s_appUserModel, Id = 5 };
            var value = default(PropVariant);
            if (((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, 5))(store, &key, &value) >= 0 && value.Vt == 31 && value.Pointer is not null)
                id = new string((char*)value.Pointer);
            PropVariantClear(&value);
            key.Id = 26;
            value = default;
            if (((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, 5))(store, &key, &value) >= 0 && value.Vt == 72 && value.Pointer is not null)
                toast = *(Guid*)value.Pointer;
            PropVariantClear(&value);
            return (id, toast);
        }
        finally { Release(store); }
    }

    // ShellExecuteEx; null on success, else the system's message.
    private static string? Execute(string file, string? directory)
    {
        fixed (char* verb = "open", f = file, d = directory)
        {
            var info = new ShellExecuteInfo
            {
                Size = (uint)sizeof(ShellExecuteInfo),
                Mask = 0x00000400 /* SEE_MASK_FLAG_NO_UI */ | 0x00000100 /* SEE_MASK_NOASYNC */,
                Verb = verb,
                File = f,
                Directory = d,
                Show = 1 /* SW_SHOWNORMAL */,
            };
            return ShellExecuteExW(&info) != 0 ? null : new System.ComponentModel.Win32Exception(Marshal.GetLastSystemError()).Message;
        }
    }

    private static bool SchemeRegistered(string scheme)
    {
        nint key;
        fixed (char* s = scheme)
        {
            if (RegOpenKeyExW(unchecked((nint)0x80000000) /* HKEY_CLASSES_ROOT */, s, 0, 0x20019 /* KEY_READ */, &key) != 0) return false;
        }
        RegCloseKey(key);
        return true;
    }

    private static void* CreateLink()
    {
        void* link;
        var clsid = s_clsidShellLink;
        var iid = s_iidShellLinkW;
        return CoCreateInstance(&clsid, null, 1, &iid, &link) < 0 ? null : link;
    }

    private static bool SetString(void* link, int slot, string value)
    {
        fixed (char* v = value)
            return ((delegate* unmanaged<void*, char*, int>)Slot(link, slot))(link, v) >= 0;
    }

    private static void* Query(void* com, Guid iid)
    {
        void* result;
        return ((delegate* unmanaged<void*, Guid*, void**, int>)Slot(com, 0))(com, &iid, &result) < 0 ? null : result;
    }

    private static int Call(void* com, int slot, uint value) => ((delegate* unmanaged<void*, uint, int>)Slot(com, slot))(com, value);

    private static int Call(void* com, int slot, char* path, uint mode) => ((delegate* unmanaged<void*, char*, uint, int>)Slot(com, slot))(com, path, mode);

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    private static void Release(void* com)
    {
        if (com is not null) ((delegate* unmanaged<void*, uint>)Slot(com, 2))(com);
    }

    /// <summary>An <c>IFileOperationProgressSink</c> that aborts a delete Windows would not send to the Recycle Bin.</summary>
    private static class RecycleOnlySink
    {
        private const int SlotCount = 19;
        private static readonly nint* s_vtable = CreateVtable();

        // A COM object is a pointer to its vtable pointer, plus a reference count after it.
        internal static void* Create()
        {
            var obj = (nint*)NativeMemory.AllocZeroed((nuint)(2 * sizeof(nint)));
            obj[0] = (nint)s_vtable;
            obj[1] = 1;
            return obj;
        }

        private static nint* CreateVtable()
        {
            var v = (nint*)NativeMemory.Alloc((nuint)(SlotCount * sizeof(nint)));
            v[0] = (nint)(delegate* unmanaged<void*, Guid*, void**, int>)&QueryInterface;
            v[1] = (nint)(delegate* unmanaged<void*, uint>)&AddRef;
            v[2] = (nint)(delegate* unmanaged<void*, uint>)&ReleaseSink;
            for (var i = 3; i < SlotCount; i++) v[i] = (nint)(delegate* unmanaged<void*, nint, nint, nint, nint, nint, nint, int>)&Ok;
            // PreDeleteItem.
            v[11] = (nint)(delegate* unmanaged<void*, uint, void*, int>)&PreDeleteItem;
            return v;
        }

        [UnmanagedCallersOnly]
        private static int QueryInterface(void* self, Guid* iid, void** result)
        {
            if (*iid == s_iidUnknown || *iid == s_iidSink)
            {
                Interlocked.Increment(ref *((long*)self + 1));
                *result = self;
                return 0;
            }
            *result = null;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly]
        private static uint AddRef(void* self) => (uint)Interlocked.Increment(ref *((long*)self + 1));

        [UnmanagedCallersOnly]
        private static uint ReleaseSink(void* self)
        {
            var count = Interlocked.Decrement(ref *((long*)self + 1));
            if (count == 0) NativeMemory.Free(self);
            return (uint)count;
        }

        // The other notifications only ask for S_OK; extra arguments are ignored under the x64 convention.
        [UnmanagedCallersOnly]
        private static int Ok(void* self, nint a, nint b, nint c, nint d, nint e, nint f) => 0;

        [UnmanagedCallersOnly]
        private static int PreDeleteItem(void* self, uint flags, void* item)
            => (flags & 0x80 /* TSF_DELETE_RECYCLE_IF_POSSIBLE */) != 0 ? 0 : unchecked((int)0x80004004); // E_ABORT
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellExecuteInfo
    {
        public uint Size;
        public uint Mask;
        public nint Hwnd;
        public char* Verb;
        public char* File;
        public char* Parameters;
        public char* Directory;
        public int Show;
        public nint InstApp;
        public void* IdList;
        public char* Class;
        public nint ClassKey;
        public uint HotKey;
        public nint IconOrMonitor;
        public nint Process;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public void* Pointer;
    }

    [DllImport("shell32.dll")] private static extern int ShellExecuteExW(ShellExecuteInfo* info);
    [DllImport("shell32.dll")] private static extern int SHParseDisplayName(char* name, void* bindContext, void** idList, uint sfgaoIn, uint* sfgaoOut);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(void* folder, uint count, void** items, uint flags);
    [DllImport("shell32.dll")] private static extern int SHCreateItemFromParsingName(char* path, void* bindContext, Guid* iid, void** item);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(void* memory);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(PropVariant* variant);
    [DllImport("user32.dll")] private static extern int MessageBeep(uint type);
    [DllImport("advapi32.dll")] private static extern int RegOpenKeyExW(nint key, char* subKey, uint options, uint access, nint* result);
    [DllImport("advapi32.dll")] private static extern int RegCloseKey(nint key);
}
