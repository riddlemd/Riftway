using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>Why a toast went away; WinRT's <c>ToastDismissalReason</c>.</summary>
public enum ToastDismissal
{
    /// <summary>The user closed it.</summary>
    UserCanceled = 0,

    /// <summary>The app hid it.</summary>
    ApplicationHidden = 1,

    /// <summary>It timed out into the Action Center.</summary>
    TimedOut = 2,
}

/// <summary>What happened to a shown toast: activated (with its launch or action arguments and the values of its inputs),
/// dismissed, or failed.</summary>
public interface IToastEvents
{
    void Activated(string arguments, IReadOnlyList<KeyValuePair<string, string>> userInputs);
    void Dismissed(ToastDismissal reason);
    void Failed(int errorCode);
}

/// <summary>The WinRT interface IIDs Riftway computes rather than copies from the SDK.</summary>
internal static class WinRtIid
{
    /// <summary>The <c>TypedEventHandler</c> delegate IIDs for the toast's Activated, Dismissed and Failed events.</summary>
    internal static readonly Guid ActivatedHandler = HandlerIid("cinterface(IInspectable)");
    internal static readonly Guid DismissedHandler = HandlerIid("rc(Windows.UI.Notifications.ToastDismissedEventArgs;{3f89d935-d9cb-4538-a0f0-ffe7659938f8})");
    internal static readonly Guid FailedHandler = HandlerIid("rc(Windows.UI.Notifications.ToastFailedEventArgs;{35176862-cfd4-44f8-ad64-f500fd896c3b})");

    /// <summary>A WinRT parameterized interface's IID: a version-5 UUID, SHA-1 over WinRT's namespace GUID and the type
    /// signature, as the MIDL compiler computes it.</summary>
    internal static Guid ParameterizedIid(string signature)
    {
        byte[] ns = [0x11, 0xF4, 0x7A, 0xD5, 0x7B, 0x73, 0x42, 0xC0, 0xAB, 0xAE, 0x87, 0x8B, 0x1E, 0x16, 0xAD, 0xEE];
        // WinRT's IID algorithm is fixed to SHA-1; this is an identifier, not security.
#pragma warning disable CA5350
        var hash = SHA1.HashData([.. ns, .. Encoding.UTF8.GetBytes(signature)]);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        // The hash bytes are the GUID in big-endian field order.
        return new Guid(BitConverter.ToUInt32([hash[3], hash[2], hash[1], hash[0]]), BitConverter.ToUInt16([hash[5], hash[4]]),
            BitConverter.ToUInt16([hash[7], hash[6]]), hash[8], hash[9], hash[10], hash[11], hash[12], hash[13], hash[14], hash[15]);
    }

    private static Guid HandlerIid(string argsSignature)
        => ParameterizedIid("pinterface({9de1c534-6ae1-11e0-84e1-18a905bcc53f};rc(Windows.UI.Notifications.ToastNotification;{997e2675-059e-4e60-8b06-1760917c8b80});" + argsSignature + ")");
}

/// <summary>Windows toast notifications through WinRT, called as plain COM (Riftway has no Windows SDK projection).</summary>
/// <remarks>Calls run on any thread in the MTA, which CEF's UI thread is in; the toast events arrive on WinRT's own
/// threads.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsToast
{
    private static readonly Guid s_iidManagerStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
    private static readonly Guid s_iidNotificationFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");
    private static readonly Guid s_iidXmlDocumentIO = new("6CD0E74E-EE65-4489-9EBF-CA43E87BA637");
    private static readonly Guid s_iidXmlDocument = new("F7F3A506-1E87-42D6-BCFB-B8C809FA5494");
    private static readonly Guid s_iidActivatedArgs = new("E3BF92F3-C197-436F-8265-0625824F8DAC");
    private static readonly Guid s_iidActivatedArgs2 = new("AB7DA512-CC61-568E-81BE-304AC31038FA");
    private static readonly Guid s_iidDismissedArgs = new("3F89D935-D9CB-4538-A0F0-FFE7659938F8");
    private static readonly Guid s_iidFailedArgs = new("35176862-CFD4-44F8-AD64-F500FD896C3B");
    private static readonly Guid s_iidNotification2 = new("9DFB9FD1-143A-490E-90BF-B9FBA7132DE7");
    private static readonly Guid s_iidNotification4 = new("15154935-28EA-4727-88E9-C58680E2D118");
    private static readonly Guid s_iidManagerStatics2 = new("7AB93C52-0E48-4750-BA9D-1A4113981847");
    private static readonly Guid s_iidIterableStringObject = new("FE2F3D47-5D47-5499-8374-430C7CDA0204");
    private static readonly Guid s_iidPropertyValue = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
    private static readonly Guid s_iidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid s_iidAgile = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");

    /// <summary>Shows <paramref name="xml"/> as a toast from app <paramref name="appUserModelId"/>, with WinRT's
    /// <c>Tag</c>, <c>Group</c> and (when <paramref name="highPriority"/>) <c>Priority</c> set as Electron sets them, and
    /// returns a handle for <see cref="Hide"/> and <see cref="Release"/>. Throws <see cref="COMException"/> whose message
    /// is Electron's <c>failed</c> text for the step that WinRT refused.</summary>
    public static nint Show(string appUserModelId, string xml, string tag, string group, bool highPriority, IToastEvents events)
    {
        RoInitialize(1 /* RO_INIT_MULTITHREADED */);
        void* doc = null, docIO = null, xmlDoc = null, factory = null, toast = null, toast2 = null, toast4 = null, notifier = null;
        try
        {
            const string InvalidXml = "XML: Invalid XML";
            using (var name = new HString("Windows.Data.Xml.Dom.XmlDocument")) Check(RoActivateInstance(name.Handle, &doc), InvalidXml);
            Check(QueryHr(doc, s_iidXmlDocumentIO, &docIO), InvalidXml);
            using (var h = new HString(xml)) Check(((delegate* unmanaged<void*, nint, int>)Slot(docIO, 6))(docIO, h.Handle), InvalidXml);
            Check(QueryHr(doc, s_iidXmlDocument, &xmlDoc), InvalidXml);

            var iid = s_iidNotificationFactory;
            using (var name = new HString("Windows.UI.Notifications.ToastNotification"))
                Check(RoGetActivationFactory(name.Handle, &iid, &factory), "WinAPI: GetActivationFactory failed");
            Check(((delegate* unmanaged<void*, void*, void**, int>)Slot(factory, 6))(factory, xmlDoc, &toast), "WinAPI: CreateToastNotification failed");
            Check(QueryHr(toast, s_iidNotification2, &toast2), "WinAPI: Getting Notification interface failed");
            using (var h = new HString(group)) Check(((delegate* unmanaged<void*, nint, int>)Slot(toast2, 8))(toast2, h.Handle), "WinAPI: Setting group failed");
            using (var h = new HString(tag)) Check(((delegate* unmanaged<void*, nint, int>)Slot(toast2, 6))(toast2, h.Handle), "WinAPI: Setting tag failed");
            // IToastNotification4 is missing before Windows 10 1703; Electron then leaves the priority alone.
            if (QueryHr(toast, s_iidNotification4, &toast4) >= 0)
                Check(((delegate* unmanaged<void*, int, int>)Slot(toast4, 9))(toast4, highPriority ? 1 : 0), "WinAPI: Setting priority failed");

            Advise(toast, 11, Handler.Create(WinRtIid.ActivatedHandler, events, Handler.Kind.Activated));
            Advise(toast, 9, Handler.Create(WinRtIid.DismissedHandler, events, Handler.Kind.Dismissed));
            Advise(toast, 13, Handler.Create(WinRtIid.FailedHandler, events, Handler.Kind.Failed));
            notifier = Notifier(appUserModelId);
            Check(((delegate* unmanaged<void*, void*, int>)Slot(notifier, 6))(notifier, toast), "WinAPI: Show failed");
            var handle = new Toast { Notifier = (nint)notifier, Notification = (nint)toast };
            notifier = null;
            toast = null;
            return (nint)GCHandle.Alloc(handle);
        }
        finally
        {
            Release(notifier);
            Release(toast4);
            Release(toast2);
            Release(toast);
            Release(factory);
            Release(xmlDoc);
            Release(docIO);
            Release(doc);
        }
    }

    /// <summary>Removes the app's toast with <paramref name="tag"/> in <paramref name="group"/> from the Action Center;
    /// Electron's <c>WindowsToastNotification::Remove()</c>. Failures are ignored, as there.</summary>
    public static void RemoveFromHistory(string appUserModelId, string tag, string group)
    {
        RoInitialize(1);
        void* factory = null, statics = null, history = null;
        try
        {
            var iid = s_iidManagerStatics2;
            using (var name = new HString("Windows.UI.Notifications.ToastNotificationManager"))
                if (RoGetActivationFactory(name.Handle, &iid, &factory) < 0) return;
            statics = factory;
            factory = null;
            if (((delegate* unmanaged<void*, void**, int>)Slot(statics, 6))(statics, &history) < 0 || history is null) return;
            using HString t = new(tag), g = new(group), a = new(appUserModelId);
            ((delegate* unmanaged<void*, nint, nint, nint, int>)Slot(history, 8))(history, t.Handle, g.Handle, a.Handle);
        }
        finally
        {
            Release(history);
            Release(statics);
            Release(factory);
        }
    }

    /// <summary>Electron's text for a failed toast's HRESULT: "Notification failed - {reason} (HRESULT: {n})" for the
    /// notification platform's known errors, else just " (HRESULT: {n})".</summary>
    public static string FailureResultToString(int hr)
    {
        var reason = hr switch
        {
            -2143420143 => "Settings prevent the notification from being delivered.",
            -2143420142 => "Application capabilities prevent the notification from being delivered.",
            -2143420140 => "Settings prevent the notification type from being delivered.",
            -2143420139 => "The size of the notification content is too large.",
            -2143420138 => "The size of the notification tag is too large.",
            -2143420155 => "The notification platform is unavailable.",
            -2143420154 => "The notification has already been posted.",
            -2143420153 => "The notification has already been hidden.",
            -2143420128 => "The size of the developer id for scheduled notification is too large.",
            -2143420118 => "The notification tag is not alphanumeric.",
            -2143419897 => "Toast Notification was dropped without being displayed to the user.",
            -2143419896 => "The notification platform does not have the proper privileges to complete the request.",
            _ => null,
        };
        var code = $" (HRESULT: {hr.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
        return reason is null ? code : "Notification failed - " + reason + code;
    }

    /// <summary>Takes a shown toast away (Dismissed with <see cref="ToastDismissal.ApplicationHidden"/>).</summary>
    public static void Hide(nint handle)
    {
        RoInitialize(1);
        var t = (Toast)GCHandle.FromIntPtr(handle).Target!;
        ((delegate* unmanaged<void*, void*, int>)Slot((void*)t.Notifier, 7))((void*)t.Notifier, (void*)t.Notification);
    }

    /// <summary>Releases a handle from <see cref="Show"/>; the toast's events stop being delivered once WinRT lets go.</summary>
    public static void Release(nint handle)
    {
        var gc = GCHandle.FromIntPtr(handle);
        var t = (Toast)gc.Target!;
        Release((void*)t.Notification);
        Release((void*)t.Notifier);
        gc.Free();
    }

    /// <summary>Whether WinRT toasts can be created here at all (Windows 8 and later with the notification platform).</summary>
    public static bool IsSupported()
    {
        try
        {
            RoInitialize(1);
            var statics = Factory("Windows.UI.Notifications.ToastNotificationManager", s_iidManagerStatics);
            Release(statics);
            return true;
        }
        catch (COMException) { return false; }
    }

    /// <summary>Sets the process's explicit AppUserModelID: taskbar grouping, jump lists and toasts go by it. False if
    /// the shell refused it (longer than 128 characters, or with spaces).</summary>
    public static bool SetProcessAppUserModelId(string id)
    {
        fixed (char* p = id) return SetCurrentProcessExplicitAppUserModelID(p) >= 0;
    }

    /// <summary>The process's explicit AppUserModelID, or null when none was set.</summary>
    public static string? GetProcessAppUserModelId()
    {
        char* p;
        if (GetCurrentProcessExplicitAppUserModelID(&p) < 0) return null;
        var id = new string(p);
        Marshal.FreeCoTaskMem((nint)p);
        return id;
    }

    private sealed class Toast
    {
        public nint Notifier;
        public nint Notification;
    }

    private static void* Notifier(string appUserModelId)
    {
        var statics = Factory("Windows.UI.Notifications.ToastNotificationManager", s_iidManagerStatics);
        try
        {
            void* notifier;
            using var h = new HString(appUserModelId);
            Check(((delegate* unmanaged<void*, nint, void**, int>)Slot(statics, 7))(statics, h.Handle, &notifier));
            return notifier;
        }
        finally { Release(statics); }
    }

    private static void Advise(void* toast, int slot, void* handler)
    {
        long token;
        try { Check(((delegate* unmanaged<void*, void*, long*, int>)Slot(toast, slot))(toast, handler, &token)); }
        // The event source keeps its own reference.
        finally { Release(handler); }
    }

    private static void* Factory(string runtimeClass, Guid iid)
    {
        void* factory;
        using var h = new HString(runtimeClass);
        Check(RoGetActivationFactory(h.Handle, &iid, &factory));
        return factory;
    }

    private static int QueryHr(void* com, Guid iid, void** result)
        => ((delegate* unmanaged<void*, Guid*, void**, int>)Slot(com, 0))(com, &iid, result);

    private static void Check(int hr)
    {
        if (hr < 0) throw new COMException($"WinRT call failed (0x{hr:X8}).", hr);
    }

    private static void Check(int hr, string step)
    {
        if (hr < 0) throw new COMException(step + ", ERROR " + FailureResultToString(hr), hr);
    }

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    private static void Release(void* com)
    {
        if (com is not null) ((delegate* unmanaged<void*, uint>)Slot(com, 2))(com);
    }

    private static string? ReadHString(nint h)
    {
        if (h == 0) return "";
        uint length;
        var raw = WindowsGetStringRawBuffer(h, &length);
        var s = new string(raw, 0, (int)length);
        WindowsDeleteString(h);
        return s;
    }

    private readonly struct HString : IDisposable
    {
        public readonly nint Handle;

        public HString(string s)
        {
            nint h;
            fixed (char* p = s) Check(WindowsCreateString(p, (uint)s.Length, &h));
            Handle = h;
        }

        public void Dispose() => WindowsDeleteString(Handle);
    }

    /// <summary>A <c>TypedEventHandler</c> as a native COM object: vtable, reference count, the delegate IID, the kind, and a
    /// handle to the managed events.</summary>
    private static class Handler
    {
        internal enum Kind { Activated, Dismissed, Failed }

        [StructLayout(LayoutKind.Sequential)]
        private struct Object
        {
            public nint Vtable;
            public long RefCount;
            public Guid Iid;
            public Kind Kind;
            public nint Events;
        }

        private static readonly nint* s_vtable = CreateVtable();

        internal static void* Create(Guid iid, IToastEvents events, Kind kind)
        {
            var o = (Object*)NativeMemory.AllocZeroed((nuint)sizeof(Object));
            o->Vtable = (nint)s_vtable;
            o->RefCount = 1;
            o->Iid = iid;
            o->Kind = kind;
            o->Events = (nint)GCHandle.Alloc(events);
            return o;
        }

        private static nint* CreateVtable()
        {
            var v = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
            v[0] = (nint)(delegate* unmanaged<Object*, Guid*, void**, int>)&QueryInterface;
            v[1] = (nint)(delegate* unmanaged<Object*, uint>)&AddRef;
            v[2] = (nint)(delegate* unmanaged<Object*, uint>)&ReleaseHandler;
            v[3] = (nint)(delegate* unmanaged<Object*, void*, void*, int>)&Invoke;
            return v;
        }

        // Agile: WinRT may call the handler from any thread without marshalling it.
        [UnmanagedCallersOnly]
        private static int QueryInterface(Object* self, Guid* iid, void** result)
        {
            if (*iid == self->Iid || *iid == s_iidUnknown || *iid == s_iidAgile)
            {
                Interlocked.Increment(ref self->RefCount);
                *result = self;
                return 0;
            }
            *result = null;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly]
        private static uint AddRef(Object* self) => (uint)Interlocked.Increment(ref self->RefCount);

        [UnmanagedCallersOnly]
        private static uint ReleaseHandler(Object* self)
        {
            var count = Interlocked.Decrement(ref self->RefCount);
            if (count == 0)
            {
                GCHandle.FromIntPtr(self->Events).Free();
                NativeMemory.Free(self);
            }
            return (uint)count;
        }

        [UnmanagedCallersOnly]
        private static int Invoke(Object* self, void* sender, void* args)
        {
            try
            {
                var events = (IToastEvents)GCHandle.FromIntPtr(self->Events).Target!;
                switch (self->Kind)
                {
                    case Kind.Activated:
                        var arguments = "";
                        var a = TryQuery(args, s_iidActivatedArgs);
                        if (a is not null)
                        {
                            nint h;
                            if (((delegate* unmanaged<void*, nint*, int>)Slot(a, 6))(a, &h) >= 0) arguments = ReadHString(h) ?? "";
                            Release(a);
                        }
                        events.Activated(arguments, UserInputs(args));
                        break;
                    case Kind.Dismissed:
                        var reason = ToastDismissal.UserCanceled;
                        var d = TryQuery(args, s_iidDismissedArgs);
                        if (d is not null)
                        {
                            int r;
                            if (((delegate* unmanaged<void*, int*, int>)Slot(d, 6))(d, &r) >= 0) reason = (ToastDismissal)r;
                            Release(d);
                        }
                        events.Dismissed(reason);
                        break;
                    default:
                        var error = 0;
                        var f = TryQuery(args, s_iidFailedArgs);
                        if (f is not null)
                        {
                            int e;
                            if (((delegate* unmanaged<void*, int*, int>)Slot(f, 6))(f, &e) >= 0) error = e;
                            Release(f);
                        }
                        events.Failed(error);
                        break;
                }
            }
            // An exception must not cross into WinRT.
            catch (Exception e) { Console.Error.WriteLine($"[Riftway] A toast event handler threw: {e}"); }
            return 0;
        }

        private static void* TryQuery(void* com, Guid iid)
        {
            if (com is null) return null;
            void* result;
            return ((delegate* unmanaged<void*, Guid*, void**, int>)Slot(com, 0))(com, &iid, &result) < 0 ? null : result;
        }

        /// <summary>The string values of the toast's inputs (the reply box, selections) from
        /// <c>IToastActivatedEventArgs2.UserInput</c>, as Electron's <c>ExtractUserInputs()</c> reads them.</summary>
        private static List<KeyValuePair<string, string>> UserInputs(void* args)
        {
            var inputs = new List<KeyValuePair<string, string>>();
            void* args2 = TryQuery(args, s_iidActivatedArgs2), set = null, iterable = null, iterator = null;
            try
            {
                if (args2 is null || ((delegate* unmanaged<void*, void**, int>)Slot(args2, 6))(args2, &set) < 0 || set is null) return inputs;
                iterable = TryQuery(set, s_iidIterableStringObject);
                if (iterable is null || ((delegate* unmanaged<void*, void**, int>)Slot(iterable, 6))(iterable, &iterator) < 0 || iterator is null) return inputs;
                byte hasCurrent;
                if (((delegate* unmanaged<void*, byte*, int>)Slot(iterator, 7))(iterator, &hasCurrent) < 0) return inputs;
                while (hasCurrent != 0)
                {
                    void* pair = null;
                    if (((delegate* unmanaged<void*, void**, int>)Slot(iterator, 6))(iterator, &pair) < 0 || pair is null) break;
                    nint key;
                    void* value = null;
                    if (((delegate* unmanaged<void*, nint*, int>)Slot(pair, 6))(pair, &key) >= 0)
                    {
                        var name = ReadHString(key) ?? "";
                        if (((delegate* unmanaged<void*, void**, int>)Slot(pair, 7))(pair, &value) >= 0 && value is not null)
                        {
                            var property = TryQuery(value, s_iidPropertyValue);
                            nint text;
                            if (property is not null && ((delegate* unmanaged<void*, nint*, int>)Slot(property, 19))(property, &text) >= 0)
                                inputs.Add(new(name, ReadHString(text) ?? ""));
                            Release(property);
                            Release(value);
                        }
                    }
                    Release(pair);
                    if (((delegate* unmanaged<void*, byte*, int>)Slot(iterator, 8))(iterator, &hasCurrent) < 0) break;
                }
                return inputs;
            }
            finally
            {
                Release(iterator);
                Release(iterable);
                Release(set);
                Release(args2);
            }
        }
    }

    [DllImport("shell32.dll")] private static extern int SetCurrentProcessExplicitAppUserModelID(char* id);
    [DllImport("shell32.dll")] private static extern int GetCurrentProcessExplicitAppUserModelID(char** id);
    [DllImport("combase.dll")] private static extern int RoInitialize(int type);
    [DllImport("combase.dll")] private static extern int RoActivateInstance(nint classId, void** instance);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, Guid* iid, void** factory);
    [DllImport("combase.dll")] private static extern int WindowsCreateString(char* source, uint length, nint* result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint s);
    [DllImport("combase.dll")] private static extern char* WindowsGetStringRawBuffer(nint s, uint* length);
}
