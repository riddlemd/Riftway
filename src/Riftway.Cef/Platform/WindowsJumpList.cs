using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>A destination the user removed from the jump list, as the shell reports it.</summary>
public sealed record WindowsDestination
{
    /// <summary>True for a file (<c>IShellItem</c>), false for a task (<c>IShellLink</c>).</summary>
    public bool IsFile { get; init; }

    /// <summary>The file's path, or the task's program.</summary>
    public string? Path { get; init; }

    /// <summary>The task's command-line arguments.</summary>
    public string Arguments { get; init; } = "";

    /// <summary>The task's title.</summary>
    public string? Title { get; init; }

    /// <summary>The task's tooltip.</summary>
    public string Description { get; init; } = "";

    /// <summary>The task's icon file.</summary>
    public string? IconPath { get; init; }

    /// <summary>The icon's index in <see cref="IconPath"/>.</summary>
    public int IconIndex { get; init; }

    /// <summary>The task's working directory.</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>The taskbar jump list (<c>ICustomDestinationList</c>) and recent documents on Windows.</summary>
/// <remarks>Handles are raw COM pointers; release each with <see cref="Release"/>. Chromium's UI thread is in the MTA,
/// and these shell objects are free-threaded or proxied, so any thread works.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsJumpList
{
    private static readonly Guid s_clsidDestinationList = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    private static readonly Guid s_iidCustomDestinationList = new("6332DEBF-87B5-4670-90C0-5E57B408A49E");
    private static readonly Guid s_iidPersistFile = new("0000010B-0000-0000-C000-000000000046");
    private static readonly Guid s_folderIdRecent = new("AE50C081-EBD2-438A-8655-8A092E34987A");
    private static readonly Guid s_clsidEnumerableObjectCollection = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    private static readonly Guid s_iidObjectCollection = new("5632B1A4-E38A-400A-928A-D4CD63230295");
    private static readonly Guid s_iidObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
    private static readonly Guid s_clsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid s_iidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid s_iidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid s_iidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid s_titleFormat = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    private static readonly Guid s_appUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    // ICustomDestinationList vtable slots, after IUnknown's three.
    private const int ListSetAppId = 3;
    private const int ListBegin = 4;
    private const int ListAppendCategory = 5;
    private const int ListAppendKnownCategory = 6;
    private const int ListAddUserTasks = 7;
    private const int ListCommit = 8;
    private const int ListDelete = 10;
    private const int ListAbort = 11;

    // IPersistFile.
    private const int PersistFileLoad = 5;

    // IObjectArray, then IObjectCollection.
    private const int ArrayGetCount = 3;
    private const int ArrayGetAt = 4;
    private const int CollectionAddObject = 5;

    // IShellLinkW.
    private const int LinkGetPath = 3;
    private const int LinkGetDescription = 6;
    private const int LinkSetDescription = 7;
    private const int LinkGetWorkingDirectory = 8;
    private const int LinkSetWorkingDirectory = 9;
    private const int LinkGetArguments = 10;
    private const int LinkSetArguments = 11;
    private const int LinkGetIconLocation = 16;
    private const int LinkSetIconLocation = 17;
    private const int LinkSetPath = 20;

    // IShellItem.
    private const int ItemGetDisplayName = 5;

    // IPropertyStore.
    private const int StoreGetValue = 5;
    private const int StoreSetValue = 6;
    private const int StoreCommit = 7;

    private const uint InprocServer = 1;
    private const uint CoinitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const uint SigdnFileSysPath = 0x80058000;
    private const uint ShardAppIdInfo = 4;
    private const uint SlgpUncPriority = 2;
    private const ushort VtBool = 11;
    private const ushort VtLpwstr = 31;
    private const int MaxPath = 260;
    private const int InfoTipSize = 1024;

    /// <summary>Joins the calling thread to the MTA unless it is already in an apartment. True if the caller must
    /// balance it with <see cref="UninitializeCom"/>.</summary>
    public static bool InitializeCom()
    {
        var hr = CoInitializeEx(null, CoinitMultithreaded);
        // RPC_E_CHANGED_MODE: the thread is already an STA, which also works.
        return hr >= 0 && hr != RpcEChangedMode;
    }

    /// <summary>Balances a <see cref="InitializeCom"/> that returned true.</summary>
    public static void UninitializeCom() => CoUninitialize();

    /// <summary>The AppUserModelID the process set explicitly, or null if it set none.</summary>
    public static string? ExplicitAppUserModelId()
    {
        char* id;
        if (GetCurrentProcessExplicitAppUserModelID(&id) < 0 || id is null) return null;
        try { return new string(id); }
        finally { CoTaskMemFree(id); }
    }

    /// <summary>A new destination list bound to <paramref name="appId"/>, or the process's default id when null;
    /// 0 if the shell has none.</summary>
    public static nint CreateDestinationList(string? appId)
    {
        var list = Create(s_clsidDestinationList, s_iidCustomDestinationList);
        if (list is null || appId is null) return (nint)list;
        int hr;
        fixed (char* id = appId)
            hr = ((delegate* unmanaged<void*, char*, int>)Slot(list, ListSetAppId))(list, id);
        if (hr >= 0) return (nint)list;
        Release((nint)list);
        return 0;
    }

    /// <summary>Starts a list transaction: <c>BeginList</c>. Fills <paramref name="removed"/>, if given, with what
    /// the user removed. Returns the HRESULT.</summary>
    public static int BeginList(nint list, out int minSlots, List<WindowsDestination>? removed)
    {
        uint slots;
        void* array;
        var iid = s_iidObjectArray;
        var hr = ((delegate* unmanaged<void*, uint*, Guid*, void**, int>)Slot((void*)list, ListBegin))((void*)list, &slots, &iid, &array);
        minSlots = hr >= 0 ? (int)slots : 0;
        if (hr < 0) return hr;
        try
        {
            if (removed is not null) ReadRemoved(array, removed);
        }
        finally { Release((nint)array); }
        return hr;
    }

    /// <summary><c>AbortList</c>; returns the HRESULT.</summary>
    public static int AbortList(nint list) => ((delegate* unmanaged<void*, int>)Slot((void*)list, ListAbort))((void*)list);

    /// <summary><c>CommitList</c>; returns the HRESULT.</summary>
    public static int CommitList(nint list) => ((delegate* unmanaged<void*, int>)Slot((void*)list, ListCommit))((void*)list);

    /// <summary><c>DeleteList</c> for <paramref name="appId"/>, or the process's default id when null; returns the HRESULT.</summary>
    public static int DeleteList(nint list, string? appId)
    {
        fixed (char* id = appId)
            return ((delegate* unmanaged<void*, char*, int>)Slot((void*)list, ListDelete))((void*)list, id);
    }

    /// <summary><c>AppendKnownCategory</c>: 1 is <c>KDC_FREQUENT</c>, 2 <c>KDC_RECENT</c>. Returns the HRESULT.</summary>
    public static int AppendKnownCategory(nint list, int category) =>
        ((delegate* unmanaged<void*, int, int>)Slot((void*)list, ListAppendKnownCategory))((void*)list, category);

    /// <summary><c>AddUserTasks</c> with a collection from <see cref="CreateCollection"/>; returns the HRESULT.</summary>
    public static int AddUserTasks(nint list, nint collection) =>
        ((delegate* unmanaged<void*, void*, int>)Slot((void*)list, ListAddUserTasks))((void*)list, (void*)collection);

    /// <summary><c>AppendCategory</c> with a collection from <see cref="CreateCollection"/>; returns the HRESULT.</summary>
    public static int AppendCategory(nint list, string name, nint collection)
    {
        fixed (char* text = name)
            return ((delegate* unmanaged<void*, char*, void*, int>)Slot((void*)list, ListAppendCategory))((void*)list, text, (void*)collection);
    }

    /// <summary>An empty <c>IObjectCollection</c>, or 0.</summary>
    /// <remarks>EnumerableObjectCollection's <c>IObjectCollection</c> derives from <c>IObjectArray</c>, so the same
    /// pointer serves where the list wants an array.</remarks>
    public static nint CreateCollection() => (nint)Create(s_clsidEnumerableObjectCollection, s_iidObjectCollection);

    /// <summary>Adds a task (a shell link) to <paramref name="collection"/>. False if any part was refused.</summary>
    public static bool AddLink(nint collection, string program, string arguments, string? workingDirectory,
        string description, string? iconPath, int iconIndex, string title)
    {
        var link = Create(s_clsidShellLink, s_iidShellLinkW);
        if (link is null) return false;
        try
        {
            if (!SetString(link, LinkSetPath, program) || !SetString(link, LinkSetArguments, arguments)
                || !SetString(link, LinkSetWorkingDirectory, workingDirectory ?? "")
                || !SetString(link, LinkSetDescription, description)) return false;
            if (!string.IsNullOrEmpty(iconPath))
            {
                fixed (char* icon = iconPath)
                    if (((delegate* unmanaged<void*, char*, int, int>)Slot(link, LinkSetIconLocation))(link, icon, iconIndex) < 0) return false;
            }
            var value = new PropVariant { Vt = VtLpwstr, Pointer = (char*)Marshal.StringToCoTaskMemUni(title) };
            return SetProperty(link, new PropertyKey { Format = s_titleFormat, Id = 2 }, &value) && AddObject(collection, link);
        }
        finally { Release((nint)link); }
    }

    /// <summary>Adds a separator (a link flagged <c>PKEY_AppUserModel_IsDestListSeparator</c>). False if refused.</summary>
    public static bool AddSeparator(nint collection)
    {
        var link = Create(s_clsidShellLink, s_iidShellLinkW);
        if (link is null) return false;
        try
        {
            var value = new PropVariant { Vt = VtBool, Bool = -1 };
            return SetProperty(link, new PropertyKey { Format = s_appUserModelFormat, Id = 6 }, &value) && AddObject(collection, link);
        }
        finally { Release((nint)link); }
    }

    /// <summary>Adds the file at <paramref name="path"/> as a shell item. False if it cannot be parsed or was refused.</summary>
    public static bool AddFile(nint collection, string path)
    {
        var item = CreateItem(path);
        if (item is null) return false;
        try { return AddObject(collection, item); }
        finally { Release((nint)item); }
    }

    /// <summary>Electron's <c>ClearRecentDocuments</c>: <c>SHAddToRecentDocs(SHARD_APPIDINFO, NULL)</c>.</summary>
    public static void ClearRecentDocuments() => SHAddToRecentDocs(ShardAppIdInfo, null);

    /// <summary>Electron's <c>AddRecentDocument</c>: <paramref name="path"/> as a shell item under <paramref name="appId"/>
    /// (<c>SHARD_APPIDINFO</c>). Silently ignores paths the shell cannot parse.</summary>
    public static void AddRecentDocument(string path, string appId)
    {
        var item = CreateItem(path);
        if (item is null) return;
        try
        {
            fixed (char* id = appId)
            {
                var info = new AppIdInfo { Item = item, AppId = id };
                SHAddToRecentDocs(ShardAppIdInfo, &info);
            }
        }
        finally { Release((nint)item); }
    }

    /// <summary>Electron's <c>GetRecentDocuments</c>: the non-empty targets of the <c>*.lnk</c> files directly in the
    /// user's Recent folder (<c>FOLDERID_Recent</c>), in directory order. Needs COM on the calling thread.</summary>
    public static IReadOnlyList<string> RecentDocuments()
    {
        char* folder;
        var known = s_folderIdRecent;
        if (SHGetKnownFolderPath(&known, 0, 0, &folder) < 0 || folder is null) return [];
        string recent;
        try { recent = new string(folder); }
        finally { CoTaskMemFree(folder); }

        List<string> documents = [];
        try
        {
            // base::FileEnumerator lists hidden and system files too.
            foreach (var link in Directory.EnumerateFiles(recent, "*.lnk", new EnumerationOptions { AttributesToSkip = 0, MatchType = MatchType.Win32 }))
            {
                if (ShortcutTarget(link) is { Length: > 0 } target) documents.Add(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return documents;
    }

    // base::win::ResolveShortcut: load the link read-only and read its path, without resolving it.
    private static string? ShortcutTarget(string link)
    {
        var shellLink = Create(s_clsidShellLink, s_iidShellLinkW);
        if (shellLink is null) return null;
        try
        {
            void* persist;
            var iid = s_iidPersistFile;
            if (((delegate* unmanaged<void*, Guid*, void**, int>)Slot(shellLink, 0))(shellLink, &iid, &persist) < 0) return null;
            try
            {
                int hr;
                fixed (char* path = link)
                    hr = ((delegate* unmanaged<void*, char*, uint, int>)Slot(persist, PersistFileLoad))(persist, path, 0 /* STGM_READ */);
                if (hr < 0) return null;
            }
            finally { Release((nint)persist); }
            var buffer = stackalloc char[MaxPath];
            buffer[0] = '\0';
            return ((delegate* unmanaged<void*, char*, int, void*, uint, int>)Slot(shellLink, LinkGetPath))(shellLink, buffer, MaxPath, null, SlgpUncPriority) < 0
                ? null
                : new string(buffer);
        }
        finally { Release((nint)shellLink); }
    }

    /// <summary>Releases a COM handle from this class; 0 is ignored.</summary>
    public static void Release(nint com)
    {
        if (com != 0) ((delegate* unmanaged<void*, uint>)Slot((void*)com, 2))((void*)com);
    }

    private static void ReadRemoved(void* array, List<WindowsDestination> removed)
    {
        uint count;
        if (((delegate* unmanaged<void*, uint*, int>)Slot(array, ArrayGetCount))(array, &count) < 0) return;
        var iidItem = s_iidShellItem;
        var iidLink = s_iidShellLinkW;
        for (uint i = 0; i < count; i++)
        {
            void* obj;
            if (((delegate* unmanaged<void*, uint, Guid*, void**, int>)Slot(array, ArrayGetAt))(array, i, &iidItem, &obj) >= 0)
            {
                removed.Add(new WindowsDestination { IsFile = true, Path = FileSystemPath(obj) });
                Release((nint)obj);
            }
            else if (((delegate* unmanaged<void*, uint, Guid*, void**, int>)Slot(array, ArrayGetAt))(array, i, &iidLink, &obj) >= 0)
            {
                if (ReadLink(obj) is { } link) removed.Add(link);
                Release((nint)obj);
            }
        }
    }

    private static string? FileSystemPath(void* item)
    {
        char* name;
        if (((delegate* unmanaged<void*, uint, char**, int>)Slot(item, ItemGetDisplayName))(item, SigdnFileSysPath, &name) < 0) return null;
        try { return new string(name); }
        finally { CoTaskMemFree(name); }
    }

    private static WindowsDestination? ReadLink(void* link)
    {
        var buffer = stackalloc char[InfoTipSize];
        if (((delegate* unmanaged<void*, char*, int, void*, uint, int>)Slot(link, LinkGetPath))(link, buffer, MaxPath, null, 0) < 0) return null;
        var path = new string(buffer);
        var arguments = GetString(link, LinkGetArguments, buffer, InfoTipSize) ?? "";
        var workingDirectory = GetString(link, LinkGetWorkingDirectory, buffer, MaxPath);
        var description = GetString(link, LinkGetDescription, buffer, InfoTipSize) ?? "";
        int iconIndex;
        string? iconPath = null;
        if (((delegate* unmanaged<void*, char*, int, int*, int>)Slot(link, LinkGetIconLocation))(link, buffer, MaxPath, &iconIndex) >= 0)
            iconPath = new string(buffer);
        else iconIndex = 0;
        return new WindowsDestination
        {
            Path = path,
            Arguments = arguments,
            Title = GetTitle(link),
            Description = description,
            IconPath = iconPath,
            IconIndex = iconIndex,
            WorkingDirectory = workingDirectory,
        };
    }

    private static string? GetString(void* link, int slot, char* buffer, int size)
    {
        buffer[0] = '\0';
        return ((delegate* unmanaged<void*, char*, int, int>)Slot(link, slot))(link, buffer, size) >= 0 ? new string(buffer) : null;
    }

    private static string? GetTitle(void* link)
    {
        var store = PropertyStore(link);
        if (store is null) return null;
        try
        {
            var key = new PropertyKey { Format = s_titleFormat, Id = 2 };
            var value = default(PropVariant);
            if (((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, StoreGetValue))(store, &key, &value) < 0) return null;
            var title = value.Vt == VtLpwstr ? new string(value.Pointer) : null;
            PropVariantClear(&value);
            return title;
        }
        finally { Release((nint)store); }
    }

    private static bool SetString(void* link, int slot, string value)
    {
        fixed (char* text = value)
            return ((delegate* unmanaged<void*, char*, int>)Slot(link, slot))(link, text) >= 0;
    }

    // Takes ownership of value and clears it.
    private static bool SetProperty(void* link, PropertyKey key, PropVariant* value)
    {
        var store = PropertyStore(link);
        try
        {
            return store is not null
                && ((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, StoreSetValue))(store, &key, value) >= 0
                && ((delegate* unmanaged<void*, int>)Slot(store, StoreCommit))(store) >= 0;
        }
        finally
        {
            PropVariantClear(value);
            Release((nint)store);
        }
    }

    private static void* PropertyStore(void* com)
    {
        void* store;
        var iid = s_iidPropertyStore;
        return ((delegate* unmanaged<void*, Guid*, void**, int>)Slot(com, 0))(com, &iid, &store) < 0 ? null : store;
    }

    private static bool AddObject(nint collection, void* obj) =>
        ((delegate* unmanaged<void*, void*, int>)Slot((void*)collection, CollectionAddObject))((void*)collection, obj) >= 0;

    private static void* CreateItem(string path)
    {
        void* item;
        var iid = s_iidShellItem;
        fixed (char* p = path)
            return SHCreateItemFromParsingName(p, null, &iid, &item) < 0 ? null : item;
    }

    private static void* Create(Guid clsid, Guid iid)
    {
        void* com;
        return CoCreateInstance(&clsid, null, InprocServer, &iid, &com) < 0 ? null : com;
    }

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    // PROPVARIANT: a VARTYPE and three reserved words, then a 16-byte union.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public char* Pointer;
        [FieldOffset(8)] public short Bool;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppIdInfo
    {
        public void* Item;
        public char* AppId;
    }

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(void* reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(void* memory);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(PropVariant* variant);
    [DllImport("shell32.dll")] private static extern int GetCurrentProcessExplicitAppUserModelID(char** appId);
    [DllImport("shell32.dll")] private static extern int SHCreateItemFromParsingName(char* path, void* bindContext, Guid* iid, void** item);
    [DllImport("shell32.dll")] private static extern void SHAddToRecentDocs(uint flags, void* data);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(Guid* id, uint flags, nint token, char** path);
}
