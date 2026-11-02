using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Riftway.Cef.Platform;

/// <summary>Receives an activation: the toast's arguments and the values of its inputs (reply text, selections).</summary>
public delegate void ToastActivationHandler(string arguments, IReadOnlyList<KeyValuePair<string, string>> userInputs);

/// <summary>The toast activator COM server, as Electron's <c>windows_toast_activator.cc</c> sets it up: for an unpackaged
/// app a Start-menu shortcut carrying the AppUserModelID and activator CLSID and an HKCU <c>CLSID</c> registration, then
/// an <c>INotificationActivationCallback</c> class object that Windows calls when a toast or its button is pressed.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsToastActivator
{
    private static readonly Guid s_iidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid s_iidClassFactory = new("00000001-0000-0000-C000-000000000046");
    private static readonly Guid s_iidActivationCallback = new("53E31837-6600-4A81-9395-75CFFE746F94");
    private static readonly Guid s_clsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid s_iidShellLink = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid s_iidPersistFile = new("0000010B-0000-0000-C000-000000000046");
    private static readonly Guid s_iidPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly Guid s_appUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    private const uint AppUserModelIdProperty = 5;
    private const uint ToastActivatorClsidProperty = 26;
    private const ushort VtLpwstr = 31;
    private const ushort VtClsid = 72;

    private static int s_started;
    private static volatile bool s_registered;
    private static ToastActivationHandler? s_handler;
    private static ComObject* s_factory;
    private static ComObject* s_activator;

    /// <summary>Whether the class object is registered and Windows can deliver activations.</summary>
    public static bool IsRegistered => s_registered;

    /// <summary>Sets the server up once per process; later calls do nothing. The shortcut and registry work runs on the
    /// thread pool, as Electron runs it off the UI thread; <paramref name="handler"/> is then called on COM's threads.</summary>
    public static Task Register(ToastActivationHandler handler)
    {
        if (Interlocked.Exchange(ref s_started, 1) != 0) return Task.CompletedTask;
        s_handler = handler;
        return Task.Run(() =>
        {
            try
            {
                CoInitializeEx(null, 0 /* COINIT_MULTITHREADED */);
                if (!IsPackaged() && Environment.ProcessPath is { } exe)
                {
                    var name = WindowsAppIdentity.ProductName is { Length: > 0 } product ? product : "ElectronApp";
                    EnsureShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Programs, Environment.SpecialFolderOption.Create),
                        name, exe, WindowsAppIdentity.AppUserModelId);
                    EnsureClsidRegistry(Registry.CurrentUser, WindowsAppIdentity.ToastActivatorClsid, exe);
                }
                RegisterClassObject(Guid.Parse(WindowsAppIdentity.ToastActivatorClsid));
            }
            // A failure here only loses activations; Electron logs and carries on.
            catch (Exception e) { Console.Error.WriteLine($"[Riftway] The toast activator was not registered: {e.Message}"); }
        });
    }

    /// <summary>Writes <c>{programs}\{name}.lnk</c> pointing at the executable unless an equivalent one is there; an
    /// existing shortcut's activator CLSID is adopted first. Electron's <c>EnsureShortcut()</c>.</summary>
    internal static void EnsureShortcut(string programs, string name, string exe, string appUserModelId)
    {
        var target = ShortcutTarget(exe);
        var workingDirectory = Path.GetDirectoryName(exe) ?? "";
        Directory.CreateDirectory(programs);
        var lnk = Path.Combine(programs, name + ".lnk");
        if (File.Exists(lnk) && ReadShortcut(lnk) is { ActivatorClsid: { } existing })
            WindowsAppIdentity.SetToastActivatorClsid(existing.ToString("B"));
        var clsid = Guid.Parse(WindowsAppIdentity.ToastActivatorClsid);
        if (File.Exists(lnk) && ReadShortcut(lnk) is { } current
            && string.Equals(current.Target, target, StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.WorkingDirectory.Replace('/', '\\'), workingDirectory.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)
            && string.Equals(current.AppUserModelId, appUserModelId, StringComparison.OrdinalIgnoreCase)
            && current.ActivatorClsid == clsid)
            return;
        WriteShortcut(lnk, target, name, workingDirectory, appUserModelId, clsid);
    }

    /// <summary>Registers the CLSID under <paramref name="root"/><c>\Software\Classes\CLSID</c> with the executable as its
    /// local server. Electron's <c>EnsureCLSIDRegistry()</c>.</summary>
    internal static void EnsureClsidRegistry(RegistryKey root, string clsid, string exe)
    {
        var path = @"Software\Classes\CLSID\" + clsid;
        using (var key = root.CreateSubKey(path))
        {
            key.SetValue(null, "Electron Notification Activator");
            key.SetValue("CustomActivator", 1, RegistryValueKind.DWord);
        }
        using var server = root.CreateSubKey(path + @"\LocalServer32");
        server.SetValue(null, exe);
    }

    // Installers may keep the running exe in a versioned folder with a same-named stub one level up; the stub is the
    // stable launch path.
    private static string ShortcutTarget(string exe)
    {
        var parent = Path.GetDirectoryName(Path.GetDirectoryName(exe));
        if (parent is null) return exe;
        var stub = Path.Combine(parent, Path.GetFileName(exe));
        return File.Exists(stub) ? stub : exe;
    }

    /// <summary>A shortcut's target, working directory, AppUserModelID and activator CLSID; null if it cannot be loaded.</summary>
    internal static ShortcutInfo? ReadShortcut(string lnk)
    {
        void* link = null, file = null, store = null;
        try
        {
            if (!CreateShellLink(&link) || Query(link, s_iidPersistFile, &file) < 0) return null;
            fixed (char* p = lnk)
                if (((delegate* unmanaged<void*, char*, uint, int>)Slot(file, 5))(file, p, 0 /* STGM_READ */) < 0) return null;
            var buffer = stackalloc char[260];
            // SLGP_RAWPATH: the stored path, environment variables unexpanded, as Electron compares it.
            if (((delegate* unmanaged<void*, char*, int, void*, uint, int>)Slot(link, 3))(link, buffer, 260, null, 4) < 0) return null;
            var target = new string(buffer);
            buffer[0] = '\0';
            if (((delegate* unmanaged<void*, char*, int, int>)Slot(link, 8))(link, buffer, 260) < 0) return null;
            var workingDirectory = new string(buffer);
            if (Query(link, s_iidPropertyStore, &store) < 0) return null;
            string? appUserModelId = null;
            Guid? clsid = null;
            var key = new PropertyKey { Format = s_appUserModelFormat, Id = AppUserModelIdProperty };
            var variant = default(PropVariant);
            if (((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, 5))(store, &key, &variant) >= 0)
            {
                if (variant.Vt == VtLpwstr && variant.Pointer is not null) appUserModelId = new string((char*)variant.Pointer);
                PropVariantClear(&variant);
            }
            key.Id = ToastActivatorClsidProperty;
            variant = default;
            if (((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, 5))(store, &key, &variant) >= 0)
            {
                if (variant.Vt == VtClsid && variant.Pointer is not null) clsid = *(Guid*)variant.Pointer;
                PropVariantClear(&variant);
            }
            return new ShortcutInfo(target, workingDirectory, appUserModelId, clsid);
        }
        finally
        {
            Release(store);
            Release(file);
            Release(link);
        }
    }

    private static void WriteShortcut(string lnk, string target, string description, string workingDirectory, string appUserModelId, Guid clsid)
    {
        void* link = null, file = null, store = null;
        try
        {
            if (!CreateShellLink(&link)) return;
            fixed (char* p = target) ((delegate* unmanaged<void*, char*, int>)Slot(link, 20))(link, p);
            fixed (char* p = "") ((delegate* unmanaged<void*, char*, int>)Slot(link, 11))(link, p);
            fixed (char* p = description) ((delegate* unmanaged<void*, char*, int>)Slot(link, 7))(link, p);
            fixed (char* p = workingDirectory) ((delegate* unmanaged<void*, char*, int>)Slot(link, 9))(link, p);
            if (Query(link, s_iidPropertyStore, &store) >= 0)
            {
                var key = new PropertyKey { Format = s_appUserModelFormat, Id = AppUserModelIdProperty };
                var variant = new PropVariant { Vt = VtLpwstr, Pointer = (void*)Marshal.StringToCoTaskMemUni(appUserModelId) };
                ((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, 6))(store, &key, &variant);
                PropVariantClear(&variant);
                key.Id = ToastActivatorClsidProperty;
                var guid = (Guid*)Marshal.AllocCoTaskMem(sizeof(Guid));
                *guid = clsid;
                variant = new PropVariant { Vt = VtClsid, Pointer = guid };
                ((delegate* unmanaged<void*, PropertyKey*, PropVariant*, int>)Slot(store, 6))(store, &key, &variant);
                PropVariantClear(&variant);
                ((delegate* unmanaged<void*, int>)Slot(store, 7))(store);
            }
            if (Query(link, s_iidPersistFile, &file) >= 0)
                fixed (char* p = lnk) ((delegate* unmanaged<void*, char*, int, int>)Slot(file, 6))(file, p, 1);
        }
        finally
        {
            Release(store);
            Release(file);
            Release(link);
        }
    }

    private static void RegisterClassObject(Guid clsid)
    {
        if (s_registered) return;
        s_activator = ComObject.Create(s_iidActivationCallback, ComObject.ActivatorVtable);
        s_factory = ComObject.Create(s_iidClassFactory, ComObject.FactoryVtable);
        uint cookie;
        // CLSCTX_LOCAL_SERVER; REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED, then resumed, as Electron registers it.
        if (CoRegisterClassObject(&clsid, s_factory, 4, 1 | 4, &cookie) < 0) return;
        if (CoResumeClassObjects() < 0)
        {
            CoRevokeClassObject(cookie);
            return;
        }
        s_registered = true;
    }

    private static bool IsPackaged()
    {
        uint length = 0;
        // ERROR_INSUFFICIENT_BUFFER (122) means there is a package name to return; unpackaged gives APPMODEL_ERROR_NO_PACKAGE.
        return GetCurrentPackageFamilyName(&length, null) == 122;
    }

    private static bool CreateShellLink(void** link)
    {
        var clsid = s_clsidShellLink;
        var iid = s_iidShellLink;
        return CoCreateInstance(&clsid, null, 1 /* CLSCTX_INPROC_SERVER */, &iid, link) >= 0;
    }

    private static int Query(void* com, Guid iid, void** result)
        => ((delegate* unmanaged<void*, Guid*, void**, int>)Slot(com, 0))(com, &iid, result);

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    private static void Release(void* com)
    {
        if (com is not null) ((delegate* unmanaged<void*, uint>)Slot(com, 2))(com);
    }

    /// <summary>A shortcut's fields that decide whether Electron rewrites it.</summary>
    internal sealed record ShortcutInfo(string Target, string WorkingDirectory, string? AppUserModelId, Guid? ActivatorClsid);

    /// <summary>The class factory and the activator: static, process-lifetime COM objects (Electron leaks its factory
    /// for the same reason, so a late call never lands on freed memory). Reference counts are kept but never free.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ComObject
    {
        public nint Vtable;
        public long RefCount;
        public Guid Iid;

        internal static readonly nint* FactoryVtable = CreateFactoryVtable();
        internal static readonly nint* ActivatorVtable = CreateActivatorVtable();

        internal static ComObject* Create(Guid iid, nint* vtable)
        {
            var o = (ComObject*)NativeMemory.AllocZeroed((nuint)sizeof(ComObject));
            o->Vtable = (nint)vtable;
            o->RefCount = 1;
            o->Iid = iid;
            return o;
        }

        private static nint* CreateFactoryVtable()
        {
            var v = (nint*)NativeMemory.Alloc((nuint)(5 * sizeof(nint)));
            v[0] = (nint)(delegate* unmanaged<ComObject*, Guid*, void**, int>)&QueryInterface;
            v[1] = (nint)(delegate* unmanaged<ComObject*, uint>)&AddRef;
            v[2] = (nint)(delegate* unmanaged<ComObject*, uint>)&ReleaseObject;
            v[3] = (nint)(delegate* unmanaged<ComObject*, void*, Guid*, void**, int>)&CreateInstance;
            v[4] = (nint)(delegate* unmanaged<ComObject*, int, int>)&LockServer;
            return v;
        }

        private static nint* CreateActivatorVtable()
        {
            var v = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
            v[0] = (nint)(delegate* unmanaged<ComObject*, Guid*, void**, int>)&QueryInterface;
            v[1] = (nint)(delegate* unmanaged<ComObject*, uint>)&AddRef;
            v[2] = (nint)(delegate* unmanaged<ComObject*, uint>)&ReleaseObject;
            v[3] = (nint)(delegate* unmanaged<ComObject*, char*, char*, UserInputData*, uint, int>)&Activate;
            return v;
        }

        [UnmanagedCallersOnly]
        private static int QueryInterface(ComObject* self, Guid* iid, void** result)
        {
            if (*iid == self->Iid || *iid == s_iidUnknown)
            {
                Interlocked.Increment(ref self->RefCount);
                *result = self;
                return 0;
            }
            *result = null;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly]
        private static uint AddRef(ComObject* self) => (uint)Interlocked.Increment(ref self->RefCount);

        [UnmanagedCallersOnly]
        private static uint ReleaseObject(ComObject* self) => (uint)Math.Max(1, Interlocked.Decrement(ref self->RefCount));

        [UnmanagedCallersOnly]
        private static int CreateInstance(ComObject* self, void* outer, Guid* iid, void** result)
        {
            *result = null;
            if (outer is not null) return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
            if (*iid != s_iidActivationCallback && *iid != s_iidUnknown) return unchecked((int)0x80004002);
            Interlocked.Increment(ref s_activator->RefCount);
            *result = s_activator;
            return 0;
        }

        [UnmanagedCallersOnly]
        private static int LockServer(ComObject* self, int locked) => 0;

        [UnmanagedCallersOnly]
        private static int Activate(ComObject* self, char* appUserModelId, char* invokedArgs, UserInputData* data, uint count)
        {
            try
            {
                var inputs = new List<KeyValuePair<string, string>>((int)count);
                for (var i = 0; data is not null && i < count; i++)
                    inputs.Add(new(data[i].Key is null ? "" : new string(data[i].Key), data[i].Value is null ? "" : new string(data[i].Value)));
                s_handler?.Invoke(invokedArgs is null ? "" : new string(invokedArgs), inputs);
            }
            // An exception must not cross into COM.
            catch (Exception e) { Console.Error.WriteLine($"[Riftway] A toast activation handler threw: {e}"); }
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInputData
    {
        public char* Key;
        public char* Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    // PROPVARIANT: a VARTYPE and three reserved words, then a 16-byte union; a string or a CLSID pointer here.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public void* Pointer;
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(void* reserved, uint model);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result);
    [DllImport("ole32.dll")] private static extern int CoRegisterClassObject(Guid* clsid, void* unknown, uint context, uint flags, uint* cookie);
    [DllImport("ole32.dll")] private static extern int CoResumeClassObjects();
    [DllImport("ole32.dll")] private static extern int CoRevokeClassObject(uint cookie);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(PropVariant* variant);
    [DllImport("kernel32.dll")] private static extern int GetCurrentPackageFamilyName(uint* length, char* name);
}
