using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Riftway.Cef.Platform;

/// <summary>Electron's Handoff: <c>AtomApplication</c>'s current <c>NSUserActivity</c> and its
/// <c>NSUserActivityDelegate</c> methods (electron_application.mm), with dict_util.mm's conversions. The setters belong
/// on the main thread.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacUserActivity
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_libSystem = NativeLibrary.Load(LibSystem);
    private static readonly Lazy<bool> s_foundation = new(() => NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out _));
    // @encode(BOOL): a signed char on x86-64, a C bool on arm64.
    private static readonly string s_boolEncoding = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B" : "c";
    private static readonly object s_handoffLock = new();
    private static bool s_updateReceived;
    private static nint s_current;
    private static nint s_delegate;

    /// <summary><c>userActivityWillSave:</c>, on the main thread, with the activity's type and user info; true makes the
    /// save wait (a second at most per wake) for <see cref="UpdateCurrent"/>, as Electron's <c>update-activity-state</c>
    /// <c>preventDefault()</c> does.</summary>
    public static Func<string, JsonObject, bool>? WillSave { get; set; }

    /// <summary><c>userActivityWasContinued:</c>, posted to the main thread, with the activity's type and user info.</summary>
    public static Action<string, JsonObject>? WasContinued { get; set; }

    // dispatch_sync_main; replaceable so tests need no main run loop.
    internal static Action<Action> RunOnMainSync { get; set; } = DispatchSyncMain;

    internal static Action<Action> PostToMain { get; set; } = DispatchAsyncMain;

    // The calls that reach the user-activity system (and through it Handoff on the user's other devices): becomeCurrent,
    // setNeedsSave:, resignCurrent and invalidate. Replaceable so tests never advertise an activity.
    internal static Action<nint, string> Announce { get; set; } = AnnounceToSystem;

    private static void AnnounceToSystem(nint activity, string call)
    {
        switch (call)
        {
            case "becomeCurrent": Send(activity, Sel("becomeCurrent"u8)); break;
            case "setNeedsSave:": ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(activity, Sel("setNeedsSave:"u8), 1); break;
            case "resignCurrent": Send(activity, Sel("resignCurrent"u8)); break;
            case "invalidate": Send(activity, Sel("invalidate"u8)); break;
        }
    }

    /// <summary><c>setCurrentActivity:withUserInfo:withWebpageURL:</c>: a new activity, current and needing a save.
    /// <paramref name="userInfo"/> as <see cref="ToNSDictionary"/> takes it; <paramref name="webpageUrl"/> an http or https
    /// URL, or null.</summary>
    public static void SetCurrent(string type, JsonObject userInfo, string? webpageUrl)
    {
        _ = s_foundation.Value;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var activity = Send(Send(Class("NSUserActivity"u8), Sel("alloc"u8)), Sel("initWithActivityType:"u8), NSString(type));
            // ARC releases the previous activity as currentActivity_ is reassigned, without invalidating it.
            if (s_current != 0) Send(s_current, Sel("release"u8));
            s_current = activity;
            Send(activity, Sel("setUserInfo:"u8), ToNSDictionary(userInfo));
            Send(activity, Sel("setWebpageURL:"u8), webpageUrl is null ? 0 : Send(Class("NSURL"u8), Sel("URLWithString:"u8), NSString(webpageUrl)));
            Send(activity, Sel("setDelegate:"u8), Delegate());
            Announce(activity, "becomeCurrent");
            Announce(activity, "setNeedsSave:");
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    // The current NSUserActivity, for tests.
    internal static nint Current => s_current;

    /// <summary>The current activity's type; empty without one.</summary>
    public static string CurrentType() => s_current == 0 ? "" : ReadString(Send(s_current, Sel("activityType"u8)));

    /// <summary>The current activity's user info; empty without one.</summary>
    public static JsonObject CurrentUserInfo()
    {
        var pool = objc_autoreleasePoolPush();
        try { return s_current == 0 ? [] : ToJson(Send(s_current, Sel("userInfo"u8))); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary><c>invalidateCurrentActivity</c>: invalidates and drops the current activity.</summary>
    public static void InvalidateCurrent()
    {
        if (s_current == 0) return;
        Announce(s_current, "invalidate");
        Send(s_current, Sel("release"u8));
        s_current = 0;
    }

    /// <summary><c>resignCurrentActivity</c>.</summary>
    public static void ResignCurrent()
    {
        if (s_current != 0) Announce(s_current, "resignCurrent");
    }

    /// <summary><c>updateCurrentActivity:withUserInfo:</c>: merges <paramref name="userInfo"/> into the current activity's,
    /// then releases a save waiting in <see cref="WillSave"/>, current activity or not.</summary>
    public static void UpdateCurrent(JsonObject userInfo)
    {
        if (s_current != 0)
        {
            var pool = objc_autoreleasePoolPush();
            try { Send(s_current, Sel("addUserInfoEntriesFromDictionary:"u8), ToNSDictionary(userInfo)); }
            finally { objc_autoreleasePoolPop(pool); }
        }
        lock (s_handoffLock)
        {
            s_updateReceived = true;
            Monitor.Pulse(s_handoffLock);
        }
    }

    /// <summary>dict_util's <c>DictionaryValueToNSDictionary</c>: through JSON, autoreleased; nil (0) when the JSON
    /// writer refuses a value. Numbers are written as V8 hands them to <c>base::Value</c>: an int32 as an integer, any
    /// other number as a double.</summary>
    public static nint ToNSDictionary(JsonObject value)
    {
        return WriteJson(value) is { } json ? FromJsonBytes(json) : 0;
    }

    // NSJSONSerialization's object for json if it is a dictionary, else nil.
    internal static nint FromJsonBytes(byte[] json)
    {
        _ = s_foundation.Value;
        fixed (byte* p = json)
        {
            var data = ((delegate* unmanaged<nint, nint, byte*, nuint, nint>)s_msgSend)(Class("NSData"u8), Sel("dataWithBytes:length:"u8), p, (nuint)json.Length);
            var obj = ((delegate* unmanaged<nint, nint, nint, nuint, nint, nint>)s_msgSend)(
                Class("NSJSONSerialization"u8), Sel("JSONObjectWithData:options:error:"u8), data, 0, 0);
            return IsKind(obj, "NSDictionary"u8) ? obj : 0;
        }
    }

    /// <summary>dict_util's <c>NSDictionaryToValue</c>: strings, numbers (a BOOL or char as a bool, a double or float
    /// as a double, anything else through <c>intValue</c>), arrays and dictionaries; any other object as its
    /// description. Keys in <c>base::Value::Dict</c>'s sorted order; nil is empty.</summary>
    public static JsonObject ToJson(nint dictionary)
    {
        var result = new SortedDictionary<string, JsonNode?>(Utf8Order.Instance);
        if (dictionary == 0) return [];
        var keys = Send(dictionary, Sel("allKeys"u8));
        for (nint i = 0, n = Send(keys, Sel("count"u8)); i < n; i++)
        {
            var key = Send(keys, Sel("objectAtIndex:"u8), i);
            var name = ReadString(IsKind(key, "NSString"u8) ? key : Send(key, Sel("description"u8)));
            result[name] = ToNode(Send(dictionary, Sel("objectForKey:"u8), key));
        }
        var json = new JsonObject();
        foreach (var (k, v) in result) json[k] = v;
        return json;
    }

    private static JsonNode? ToNode(nint value)
    {
        if (IsKind(value, "NSString"u8)) return ReadString(value);
        if (IsKind(value, "NSNumber"u8))
        {
            var type = Marshal.PtrToStringUTF8(Send(value, Sel("objCType"u8))) ?? "";
            if (type == s_boolEncoding || type == "c") return ((delegate* unmanaged<nint, nint, byte>)s_msgSend)(value, Sel("boolValue"u8)) != 0;
            if (type is "d" or "f") return ((delegate* unmanaged<nint, nint, double>)s_msgSend)(value, Sel("doubleValue"u8));
            return ((delegate* unmanaged<nint, nint, int>)s_msgSend)(value, Sel("intValue"u8));
        }
        if (IsKind(value, "NSArray"u8))
        {
            var list = new JsonArray();
            for (nint i = 0, n = Send(value, Sel("count"u8)); i < n; i++) list.Add(ToNode(Send(value, Sel("objectAtIndex:"u8), i)));
            return list;
        }
        if (IsKind(value, "NSDictionary"u8)) return ToJson(value);
        return ReadString(Send(value, Sel("description"u8)));
    }

    // base::WriteJson of the dictionary V8 would have made; null where it fails (a non-finite number).
    internal static byte[]? WriteJson(JsonObject value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            if (!Write(writer, value)) return null;
        return buffer.WrittenSpan.ToArray();
    }

    private static bool Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node?.GetValueKind() ?? JsonValueKind.Null)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var (k, v) in node!.AsObject())
                {
                    writer.WritePropertyName(k);
                    if (!Write(writer, v)) return false;
                }
                writer.WriteEndObject();
                return true;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var v in node!.AsArray())
                    if (!Write(writer, v)) return false;
                writer.WriteEndArray();
                return true;
            case JsonValueKind.String: writer.WriteStringValue(node!.GetValue<string>()); return true;
            case JsonValueKind.True: writer.WriteBooleanValue(true); return true;
            case JsonValueKind.False: writer.WriteBooleanValue(false); return true;
            case JsonValueKind.Number:
                // ToJsonString reads a number of any .NET type, but throws for a non-finite double.
                var d = node is JsonValue number && number.TryGetValue<double>(out var held) ? held
                    : double.Parse(node!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!double.IsFinite(d)) return false;
                if (IsInt32(d)) writer.WriteNumberValue((int)d);
                else writer.WriteRawValue(JsonWriterDouble(d), skipInputValidation: true);
                return true;
            default: writer.WriteNullValue(); return true;
        }
    }

    // v8::Value::IsInt32: an integral value in range, not -0.
    internal static bool IsInt32(double d) => d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue && !(d == 0 && double.IsNegative(d));

    // base::JSONWriter's doubles: the shortest round-trip form, with ".0" added when it reads as an integer.
    internal static string JsonWriterDouble(double d)
    {
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        return s.AsSpan().IndexOfAny(".eE") < 0 ? s + ".0" : s;
    }

    // Keys as std::string orders them: by UTF-8 bytes.
    private sealed class Utf8Order : IComparer<string>
    {
        public static readonly Utf8Order Instance = new();

        public int Compare(string? x, string? y) => Encoding.UTF8.GetBytes(x ?? "").AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(y ?? ""));
    }

    private static nint Delegate()
    {
        if (s_delegate != 0) return s_delegate;
        fixed (byte* name = "RiftwayUserActivityDelegate"u8)
        {
            var cls = objc_getClass(name);
            if (cls == 0)
            {
                cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
                fixed (byte* types = "v@:@"u8)
                {
                    class_addMethod(cls, Sel("userActivityWillSave:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnWillSave, types);
                    class_addMethod(cls, Sel("userActivityWasContinued:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnWasContinued, types);
                }
                objc_registerClassPair(cls);
            }
            // An activity holds its delegate weakly; this one lives for the process.
            s_delegate = Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
        }
        return s_delegate;
    }

    /// <summary>The delegate's <c>userActivityWillSave:</c>, for tests.</summary>
    internal static void WillSaveActivity(nint activity)
    {
        var shouldWait = false;
        RunOnMainSync(() =>
        {
            var pool = objc_autoreleasePoolPush();
            try { shouldWait = WillSave?.Invoke(ReadString(Send(activity, Sel("activityType"u8))), ToJson(Send(activity, Sel("userInfo"u8)))) ?? false; }
            finally { objc_autoreleasePoolPop(pool); }
        });
        if (shouldWait)
        {
            lock (s_handoffLock)
            {
                s_updateReceived = false;
                while (!s_updateReceived)
                    if (!Monitor.Wait(s_handoffLock, TimeSpan.FromSeconds(1))) break;
            }
        }
        ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(activity, Sel("setNeedsSave:"u8), 1);
    }

    [UnmanagedCallersOnly]
    private static void OnWillSave(nint self, nint cmd, nint activity) => Guard(() => WillSaveActivity(activity));

    [UnmanagedCallersOnly]
    private static void OnWasContinued(nint self, nint cmd, nint activity) => Guard(() =>
    {
        // The block Electron posts keeps the activity alive until it runs.
        Send(activity, Sel("retain"u8));
        PostToMain(() =>
        {
            var pool = objc_autoreleasePoolPush();
            try { Guard(() => WasContinued?.Invoke(ReadString(Send(activity, Sel("activityType"u8))), ToJson(Send(activity, Sel("userInfo"u8))))); }
            finally
            {
                objc_autoreleasePoolPop(pool);
                Send(activity, Sel("release"u8));
            }
        });
        ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(activity, Sel("setNeedsSave:"u8), 1);
    });

    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] A user activity handler threw: {e}"); }
    }

    private static void DispatchSyncMain(Action action)
    {
        if (pthread_main_np() != 0)
        {
            action();
            return;
        }
        var handle = GCHandle.Alloc(action);
        try { dispatch_sync_f(MainQueue(), (void*)GCHandle.ToIntPtr(handle), &RunAction); }
        finally { handle.Free(); }
    }

    private static void DispatchAsyncMain(Action action)
        => dispatch_async_f(MainQueue(), (void*)GCHandle.ToIntPtr(GCHandle.Alloc(action)), &RunAndFreeAction);

    // dispatch_get_main_queue() is a macro for the address of _dispatch_main_q.
    private static nint MainQueue() => NativeLibrary.GetExport(s_libSystem, "_dispatch_main_q");

    [UnmanagedCallersOnly]
    private static void RunAction(void* context) => Guard((Action)GCHandle.FromIntPtr((nint)context).Target!);

    [UnmanagedCallersOnly]
    private static void RunAndFreeAction(void* context)
    {
        var handle = GCHandle.FromIntPtr((nint)context);
        var action = (Action)handle.Target!;
        handle.Free();
        Guard(action);
    }

    private static bool IsKind(nint obj, ReadOnlySpan<byte> className)
        => obj != 0 && ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(obj, Sel("isKindOfClass:"u8), Class(className)) != 0;

    private static string ReadString(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"u8))) ?? "";

    private static nint NSString(string s)
    {
        fixed (char* p = s)
            return ((delegate* unmanaged<nint, nint, char*, nuint, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithCharacters:length:"u8), p, (nuint)s.Length);
    }

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport(LibSystem)] private static extern int pthread_main_np();
    [DllImport(LibSystem)] private static extern void dispatch_sync_f(nint queue, void* context, delegate* unmanaged<void*, void> work);
    [DllImport(LibSystem)] private static extern void dispatch_async_f(nint queue, void* context, delegate* unmanaged<void*, void> work);
}
