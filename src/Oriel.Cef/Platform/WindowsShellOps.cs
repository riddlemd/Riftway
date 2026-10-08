using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Oriel.Cef.Platform;

/// <summary>A shortcut's fields as Windows stores them; null leaves a field unset on write.</summary>
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

/// <summary>How <see cref="WindowsShellOps.WriteShortcut"/> treats the file; Chromium's <c>base::win::ShortcutOperation</c>.</summary>
public enum WindowsShortcutOperation
{
    /// <summary><c>kCreateAlways</c>: a new shortcut, overwriting any file there.</summary>
    CreateAlways,

    /// <summary><c>kUpdateExisting</c>: the existing shortcut with the given fields changed.</summary>
    UpdateExisting,

    /// <summary><c>kReplaceExisting</c>: a new shortcut over an existing one, keeping its arguments unless given.</summary>
    ReplaceExisting,
}

/// <summary>Electron's Windows shell operations, as Electron's <c>platform_util_win.cc</c> and Chromium's
/// <c>base/win/shortcut.cc</c> do them. Call on <see cref="WindowsSta"/>.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsShellOps
{
    private const int MaxPath = 260;
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

    /// <summary>Electron's <c>OpenExternalOnWorkerThread</c>: shell-executes <paramref name="file"/> (the quoted, escaped
    /// URL) with the "open" verb; null on success, else <c>Failed to open: </c> and the system's message.</summary>
    public static string? OpenExternal(string file, string? workingDirectory, bool logUsage)
    {
        var mask = 0x00000100u /* SEE_MASK_NOASYNC */ | 0x00000400u /* SEE_MASK_FLAG_NO_UI */;
        if (logUsage) mask |= 0x04000000u; // SEE_MASK_FLAG_LOG_USAGE
        fixed (char* verb = "open", f = file, d = string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory)
        {
            var info = new ShellExecuteInfo { Size = (uint)sizeof(ShellExecuteInfo), Mask = mask, Verb = verb, File = f, Directory = d, Show = 1 /* SW_SHOWNORMAL */ };
            return ShellExecuteExW(&info) != 0 ? null : "Failed to open: " + SystemErrorCodeToString((uint)Marshal.GetLastSystemError());
        }
    }

    /// <summary>Electron's <c>OpenPathOnThread</c>: a folder opens with the "explore" verb and the folder class, anything
    /// else with its default verb, both through an id list so Windows does not guess extensions; "" on success, else
    /// <c>Failed to open path</c>.</summary>
    public static string OpenPath(string path)
    {
        var full = WindowsPaths.NormalizeSeparators(path);
        var opened = Directory.Exists(full)
            ? ExecuteIdList(full, full, "explore", "folder")
            : ExecuteIdList(full, WindowsPaths.DirName(full), null, null);
        return opened ? "" : "Failed to open path";
    }

    /// <summary>Electron's <c>ShowItemInFolderOnWorkerThread</c>: the item's folder opened with the item selected;
    /// nothing if either cannot be parsed or Explorer refuses.</summary>
    public static void ShowItemInFolder(string path)
    {
        var full = WindowsPaths.NormalizeSeparators(path);
        var dir = WindowsPaths.AsEndingWithSeparator(WindowsPaths.DirName(full));
        if (dir.Length == 0) return;
        void* desktop = null, dirItem = null, fileItem = null;
        try
        {
            if (SHGetDesktopFolder(&desktop) < 0) return;
            fixed (char* d = dir, f = full)
            {
                if (ParseDisplayName(desktop, d, &dirItem) < 0 || ParseDisplayName(desktop, f, &fileItem) < 0) return;
            }
            // Chromium falls back to opening the folder only when the HRESULT equals ERROR_FILE_NOT_FOUND, which a
            // failed HRESULT never does, so a refusal opens nothing.
            _ = SHOpenFolderAndSelectItems(dirItem, 1, &fileItem, 0);
        }
        finally
        {
            CoTaskMemFree(dirItem);
            CoTaskMemFree(fileItem);
            Release(desktop);
        }
    }

    /// <summary>Electron's <c>MoveItemToTrashWithError</c>: <c>IFileOperation</c> with its flags and a progress sink that
    /// aborts any delete Windows would not send to the Recycle Bin; null on success, else Electron's message.</summary>
    public static string? Trash(string path)
    {
        void* operation = null, item = null;
        try
        {
            var clsid = s_clsidFileOperation;
            var iid = s_iidFileOperation;
            if (CoCreateInstance(&clsid, null, 0x17 /* CLSCTX_ALL */, &iid, &operation) < 0) return "Failed to create FileOperation instance";
            // FOF_NO_UI | FOFX_ADDUNDORECORD | FOF_NOERRORUI | FOF_SILENT | FOFX_SHOWELEVATIONPROMPT | FOFX_RECYCLEONDELETE.
            if (Call(operation, 5, 0x0614u | 0x20000000u | 0x0400u | 0x0004u | 0x00040000u | 0x00080000u) < 0) return "Failed to set operation flags";
            var itemIid = s_iidShellItem;
            fixed (char* p = path)
                if (SHCreateItemFromParsingName(p, null, &itemIid, &item) < 0) return "Failed to parse path";
            var sink = RecycleOnlySink.Create();
            try
            {
                if (((delegate* unmanaged<void*, void*, void*, int>)Slot(operation, 18))(operation, item, sink) < 0) return "Failed to enqueue DeleteItem command";
                if (((delegate* unmanaged<void*, int>)Slot(operation, 21))(operation) < 0) return "Failed to perform delete operation";
                int aborted;
                if (((delegate* unmanaged<void*, int*, int>)Slot(operation, 22))(operation, &aborted) < 0) return "Failed to check operation status";
                return aborted != 0 ? "Operation was aborted" : null;
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

    /// <summary>Chromium's <c>CreateOrUpdateShortcutLink</c>; false where it returns false. Create makes the parent
    /// folder; update and replace need a file the shell link object loads read-write.</summary>
    public static bool WriteShortcut(string path, WindowsShortcut fields, WindowsShortcutOperation operation)
    {
        if (operation == WindowsShortcutOperation.CreateAlways)
        {
            try { Directory.CreateDirectory(WindowsPaths.DirName(path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
        }
        var existed = File.Exists(path) || Directory.Exists(path);
        void* link = null, file = null, oldLink = null, oldFile = null;
        try
        {
            fixed (char* p = path)
            {
                switch (operation)
                {
                    case WindowsShortcutOperation.CreateAlways:
                        Initialize(null, &link, &file);
                        break;
                    case WindowsShortcutOperation.UpdateExisting:
                        Initialize(p, &link, &file);
                        break;
                    case WindowsShortcutOperation.ReplaceExisting:
                        Initialize(p, &oldLink, &oldFile);
                        if (oldFile is not null) Initialize(null, &link, &file);
                        break;
                }
                if (file is null) return false;
                if (fields.Target is { } target && !SetString(link, 20, target)) return false;
                if (fields.WorkingDirectory is { } cwd && !SetString(link, 9, cwd)) return false;
                if (fields.Arguments is { } args)
                {
                    if (!SetString(link, 11, args)) return false;
                }
                else if (oldFile is not null)
                {
                    var current = stackalloc char[MaxPath];
                    current[0] = '\0';
                    if (((delegate* unmanaged<void*, char*, int, int>)Slot(oldLink, 10))(oldLink, current, MaxPath) >= 0)
                        ((delegate* unmanaged<void*, char*, int>)Slot(link, 11))(link, current);
                }
                if (fields.Description is { } description && !SetString(link, 7, description)) return false;
                if (fields is { Icon: { } icon, IconIndex: { } index })
                {
                    fixed (char* i = icon)
                        if (((delegate* unmanaged<void*, char*, int, int>)Slot(link, 17))(link, i, index) < 0) return false;
                }
                if ((fields.AppUserModelId is not null || fields.ToastActivatorClsid is not null) && !SetAppUserModel(link, fields)) return false;

                // The old shortcut is released first so it cannot block overwriting the file.
                Release(oldFile);
                Release(oldLink);
                oldFile = oldLink = null;
                var saved = Call(file, 6, p, 1u /* fRemember = TRUE */);
                Release(file);
                Release(link);
                file = link = null;
                if (saved < 0) return false;
                SHChangeNotify(existed ? 0x00002000 /* SHCNE_UPDATEITEM */ : 0x00000002 /* SHCNE_CREATE */, 0x0005 /* SHCNF_PATHW */ | 0x1000 /* SHCNF_FLUSH */, p, null);
                return true;
            }
        }
        finally
        {
            Release(oldFile);
            Release(oldLink);
            Release(file);
            Release(link);
        }
    }

    /// <summary>Chromium's <c>ResolveShortcutProperties</c> with every property: unset text fields read as "", an unset
    /// toast CLSID as <see cref="Guid.Empty"/>, and a CLSID of another variant type as null.</summary>
    /// <exception cref="IOException">The file cannot be loaded or a field cannot be read: Electron's
    /// <c>Failed to read shortcut link</c>.</exception>
    public static WindowsShortcut ReadShortcut(string path)
    {
        void* link = null, file = null, store = null;
        try
        {
            if ((link = CreateLink()) is null || (file = Query(link, s_iidPersistFile)) is null) throw ReadFailed();
            fixed (char* p = path)
                if (Call(file, 5, p, 0u /* STGM_READ */) < 0) throw ReadFailed();
            var buffer = stackalloc char[MaxPath];
            string Get(int slot)
            {
                buffer[0] = '\0';
                var hr = slot == 3
                    ? ((delegate* unmanaged<void*, char*, int, void*, uint, int>)Slot(link, slot))(link, buffer, MaxPath, null, 2 /* SLGP_UNCPRIORITY */)
                    : ((delegate* unmanaged<void*, char*, int, int>)Slot(link, slot))(link, buffer, MaxPath);
                return hr >= 0 ? new string(buffer) : throw ReadFailed();
            }
            var target = Get(3);
            var cwd = Get(8);
            var args = Get(10);
            var description = Get(6);
            buffer[0] = '\0';
            int index;
            if (((delegate* unmanaged<void*, char*, int, int*, int>)Slot(link, 16))(link, buffer, MaxPath, &index) < 0) throw ReadFailed();
            var icon = new string(buffer);
            if ((store = Query(link, s_iidPropertyStore)) is null) throw ReadFailed();
            var (appId, toast) = ReadAppUserModel(store);
            return new WindowsShortcut
            {
                Target = target, WorkingDirectory = cwd, Arguments = args, Description = description, Icon = icon, IconIndex = index,
                AppUserModelId = appId, ToastActivatorClsid = toast,
            };
        }
        finally
        {
            Release(store);
            Release(file);
            Release(link);
        }
    }

    /// <summary>Chromium's <c>logging::SystemErrorCodeToString</c>: the system message with its whitespace collapsed and
    /// line breaks dropped, then the code in hex.</summary>
    public static string SystemErrorCodeToString(uint code)
    {
        char* message = null;
        var length = FormatMessageW(0x100 /* ALLOCATE_BUFFER */ | 0x1000 /* FROM_SYSTEM */ | 0x200 /* IGNORE_INSERTS */, null, code, 0, &message, 0, null);
        if (length == 0) return $"Error (0x{(uint)Marshal.GetLastSystemError():X}) while retrieving error. (0x{code:X})";
        var text = new string(message);
        LocalFree(message);
        return WindowsPaths.CollapseWhitespace(text) + $" (0x{code:X})";
    }

    private static IOException ReadFailed() => new("Failed to read shortcut link");

    private static (string AppId, Guid? Toast) ReadAppUserModel(void* store)
    {
        var key = new PropertyKey { Format = s_appUserModel, Id = 5 };
        var value = default(PropVariant);
        try
        {
            if (GetValue(store, &key, &value) != 0) throw ReadFailed();
            var appId = value.Vt switch
            {
                31 /* VT_LPWSTR */ => new string((char*)value.Pointer),
                8 /* VT_BSTR */ => new string((char*)value.Pointer, 0, (int)SysStringLen((char*)value.Pointer)),
                _ => "",
            };
            PropVariantClear(&value);
            key.Id = 26;
            if (GetValue(store, &key, &value) != 0) throw ReadFailed();
            Guid? toast = value.Vt switch
            {
                0 /* VT_EMPTY */ => Guid.Empty,
                72 /* VT_CLSID */ => *(Guid*)value.Pointer,
                _ => null,
            };
            return (appId, toast);
        }
        finally { PropVariantClear(&value); }
    }

    // Chromium's SetAppIdForPropertyStore and SetClsidForPropertyStore: each value is set, then committed.
    private static bool SetAppUserModel(void* link, WindowsShortcut fields)
    {
        var store = Query(link, s_iidPropertyStore);
        if (store is null) return false;
        try
        {
            if (fields.AppUserModelId is { } id)
            {
                var value = new PropVariant { Vt = 31 /* VT_LPWSTR */, Pointer = (void*)Marshal.StringToCoTaskMemUni(id) };
                if (!SetAndCommit(store, 5, &value)) return false;
            }
            if (fields.ToastActivatorClsid is { } clsid)
            {
                var guid = (Guid*)Marshal.AllocCoTaskMem(sizeof(Guid));
                *guid = clsid;
                var value = new PropVariant { Vt = 72 /* VT_CLSID */, Pointer = guid };
                if (!SetAndCommit(store, 26, &value)) return false;
            }
            return true;
        }
        finally { Release(store); }
    }

    private static bool SetAndCommit(void* store, uint id, PropVariant* value)
    {
        var key = new PropertyKey { Format = s_appUserModel, Id = id };
        var hr = ((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, 6))(store, &key, value);
        PropVariantClear(value);
        if (hr == 0) hr = ((delegate* unmanaged<void*, int>)Slot(store, 7))(store);
        return hr >= 0;
    }

    private static int GetValue(void* store, PropertyKey* key, PropVariant* value)
        => ((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, 5))(store, key, value);

    // Chromium's InitializeShortcutInterfaces: both interfaces, or neither if creating or loading (read-write) fails.
    private static void Initialize(char* shortcut, void** link, void** file)
    {
        var l = CreateLink();
        if (l is null) return;
        var f = Query(l, s_iidPersistFile);
        if (f is null || (shortcut is not null && Call(f, 5, shortcut, 2u /* STGM_READWRITE */) < 0))
        {
            Release(f);
            Release(l);
            return;
        }
        *link = l;
        *file = f;
    }

    // ui::win::InvokeShellExecuteDirectly with kManuallyParsePathForShellExecute: the path goes in as an id list.
    private static bool ExecuteIdList(string path, string workingDirectory, string? verb, string? className)
    {
        void* idList = null;
        try
        {
            fixed (char* p = path)
                if (SHParseDisplayName(p, null, &idList, 0x40000000 /* SFGAO_FILESYSTEM */, null) < 0) return false;
            var mask = 0x00000100u /* SEE_MASK_NOASYNC */ | 0x00000004u /* SEE_MASK_IDLIST */;
            if (className is not null) mask |= 0x00000001u; // SEE_MASK_CLASSNAME
            fixed (char* v = verb, d = workingDirectory.Length == 0 ? null : workingDirectory, c = className)
            {
                var info = new ShellExecuteInfo { Size = (uint)sizeof(ShellExecuteInfo), Mask = mask, Verb = v, Directory = d, Class = c, Show = 1, IdList = idList };
                return ShellExecuteExW(&info) != 0;
            }
        }
        finally { CoTaskMemFree(idList); }
    }

    private static int ParseDisplayName(void* folder, char* name, void** idList)
        => ((delegate* unmanaged<void*, nint, void*, char*, uint*, void**, uint*, int>)Slot(folder, 3))(folder, 0, null, name, null, idList, null);

    private static void* CreateLink()
    {
        void* link;
        var clsid = s_clsidShellLink;
        var iid = s_iidShellLinkW;
        return CoCreateInstance(&clsid, null, 1 /* CLSCTX_INPROC_SERVER */, &iid, &link) < 0 ? null : link;
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

    /// <summary>Electron's <c>DeleteFileProgressSink</c>: aborts a delete Windows would not send to the Recycle Bin.</summary>
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
            // Electron answers E_NOTIMPL for the rename, move, copy and new-item notifications after PreRenameItem.
            foreach (var slot in (ReadOnlySpan<int>)[6, 7, 8, 9, 10, 13, 14])
                v[slot] = (nint)(delegate* unmanaged<void*, nint, nint, nint, nint, nint, nint, int>)&NotImplemented;
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

        // Extra arguments are ignored under the x64 convention.
        [UnmanagedCallersOnly]
        private static int Ok(void* self, nint a, nint b, nint c, nint d, nint e, nint f) => 0;

        [UnmanagedCallersOnly]
        private static int NotImplemented(void* self, nint a, nint b, nint c, nint d, nint e, nint f) => unchecked((int)0x80004001); // E_NOTIMPL

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
    [DllImport("shell32.dll")] private static extern int SHGetDesktopFolder(void** folder);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(void* folder, uint count, void** items, uint flags);
    [DllImport("shell32.dll")] private static extern int SHCreateItemFromParsingName(char* path, void* bindContext, Guid* iid, void** item);
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, void* item1, void* item2);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(void* memory);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(PropVariant* variant);
    [DllImport("oleaut32.dll")] private static extern uint SysStringLen(char* text);
    [DllImport("user32.dll")] private static extern int MessageBeep(uint type);
    [DllImport("kernel32.dll")] private static extern uint FormatMessageW(uint flags, void* source, uint messageId, uint languageId, char** buffer, uint size, void* arguments);
    [DllImport("kernel32.dll")] private static extern void* LocalFree(void* memory);
}

/// <summary>Chromium's <c>base::FilePath</c> rules on Windows that Electron's shell calls depend on, and
/// <c>base::CollapseWhitespace</c>: pure string code.</summary>
public static class WindowsPaths
{
    private static bool IsSeparator(char c) => c is '\\' or '/';

    /// <summary><c>NormalizePathSeparators</c>: every <c>/</c> becomes <c>\</c>.</summary>
    public static string NormalizeSeparators(string path) => path.Replace('/', '\\');

    /// <summary><c>FilePath::DirName</c>: the parent, keeping a drive's root separator and a leading <c>\\</c>, "." when
    /// there is none.</summary>
    public static string DirName(string path)
    {
        var p = StripTrailingSeparators(path);
        var letter = DriveLetter(p);
        var lastSeparator = p.LastIndexOfAny(['\\', '/']);
        if (lastSeparator < 0) p = p[..(letter + 1)];
        else if (lastSeparator == letter + 1) p = p[..(letter + 2)];
        else if (lastSeparator == letter + 2 && IsSeparator(p[letter + 1])) p = p[..(letter + 3)];
        else if (lastSeparator != 0) p = p[..lastSeparator];
        p = StripTrailingSeparators(p);
        return p.Length == 0 ? "." : p;
    }

    /// <summary><c>AsEndingWithSeparator</c>: unchanged if empty or already ending in a separator.</summary>
    public static string AsEndingWithSeparator(string path) => path.Length == 0 || IsSeparator(path[^1]) ? path : path + "\\";

    // FindDriveLetter: the index of the ':' after a leading letter, or -1 (Chromium's npos, which it adds to).
    private static int DriveLetter(string p) => p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0]) ? 1 : -1;

    private static string StripTrailingSeparators(string path)
    {
        var start = DriveLetter(path) + 2;
        var lastStripped = -1;
        var length = path.Length;
        for (var pos = path.Length; pos > start && IsSeparator(path[pos - 1]); pos--)
        {
            // Two separators at the very start are a network path's and stay, unless there were more.
            if (pos != start + 1 || lastStripped == start + 2 || !IsSeparator(path[start - 1]))
            {
                length = pos - 1;
                lastStripped = pos;
            }
        }
        return path[..length];
    }

    /// <summary><c>base::CollapseWhitespace</c> with line-break trimming: runs of whitespace become one space, and runs
    /// that hold a line break, or start or end the text, vanish.</summary>
    public static string CollapseWhitespace(string text)
    {
        var result = new StringBuilder();
        var inWhitespace = true;
        var alreadyTrimmed = true;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inWhitespace)
                {
                    inWhitespace = true;
                    result.Append(' ');
                }
                if (!alreadyTrimmed && c is '\n' or '\r')
                {
                    alreadyTrimmed = true;
                    result.Length--;
                }
            }
            else
            {
                inWhitespace = false;
                alreadyTrimmed = false;
                result.Append(c);
            }
        }
        if (inWhitespace && !alreadyTrimmed) result.Length--;
        return result.ToString();
    }
}
