using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace Riftway.Cef.Platform;

/// <summary>The centre a notification subscription or post uses; Electron's <c>NotificationCenterKind</c>.</summary>
public enum MacNotificationCenter
{
    /// <summary><c>NSDistributedNotificationCenter.defaultCenter</c>.</summary>
    Distributed,

    /// <summary><c>NSNotificationCenter.defaultCenter</c>.</summary>
    Local,

    /// <summary><c>NSWorkspace.sharedWorkspace.notificationCenter</c>.</summary>
    Workspace,
}

/// <summary>How Electron's <c>NSArrayToValue</c> and <c>NSDictionaryToValue</c> read an NSNumber, from its <c>objCType</c>.</summary>
public enum MacNumberKind
{
    /// <summary><c>@encode(BOOL)</c> or <c>@encode(char)</c>: <c>boolValue</c>.</summary>
    Bool,

    /// <summary><c>@encode(double)</c> or <c>@encode(float)</c>: <c>doubleValue</c>.</summary>
    Double,

    /// <summary>Anything else: <c>intValue</c>, so wider integers are cut to 32 bits.</summary>
    Int,
}

/// <summary>The value rules of <see cref="MacSystemPreferences"/>, free of native calls.</summary>
public static class MacPreferenceRules
{
    /// <summary>Skia's <c>SkColor4f::toSkColor</c>: each channel pinned to [0, 255] after <c>* 255 + 0.5</c> in float, as
    /// 0xAARRGGBB.</summary>
    public static uint SkColor4fToArgb(double r, double g, double b, double a)
    {
        static uint Channel(double c) => (uint)(byte)Math.Clamp((float)c * 255f + 0.5f, 0f, 255f);
        return (Channel(a) << 24) | (Channel(r) << 16) | (Channel(g) << 8) | Channel(b);
    }

    /// <summary>Electron's NSNumber test: <c>@encode(BOOL)</c> (<c>B</c> on arm64, <c>c</c> on x86-64) or
    /// <c>@encode(char)</c> is a bool, <c>d</c> or <c>f</c> a double, anything else an int.</summary>
    public static MacNumberKind NumberKind(string objCType) => objCType switch
    {
        "c" or "B" => MacNumberKind.Bool,
        "d" or "f" => MacNumberKind.Double,
        _ => MacNumberKind.Int,
    };
}

/// <summary>The AppKit, Foundation, AVFoundation, LocalAuthentication and HIServices calls behind Electron's
/// <c>electron_api_system_preferences_mac.mm</c>.</summary>
/// <remarks>Colours and the appearance resolve against the calling thread's drawing appearance, so callers use the main
/// thread for them, as Electron does. The user-default and notification calls are safe on any thread.</remarks>
[SupportedOSPlatform("macos")]
public static unsafe class MacSystemPreferences
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string AVFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
    private const string LocalAuthentication = "/System/Library/Frameworks/LocalAuthentication.framework/LocalAuthentication";
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const int BlockHasSignature = 1 << 30;
    // LAPolicyDeviceOwnerAuthenticationWithBiometricsOrWatch, LABiometryTypeTouchID, LAAccessControlOperationUseKeySign.
    private const nint BiometricsOrWatch = 4, TouchIdBiometry = 1, UseKeySign = 3;
    // kSecAccessControlPrivateKeyUsage | kSecAccessControlUserPresence.
    private const nuint TouchIdFlags = (1u << 30) | 1u;

    private static readonly nint s_msgSend = LoadMsgSend();
    private static readonly nint s_stackBlock = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_NSConcreteStackBlock");
    // BOOL is C bool on arm64 and signed char on x86-64; the block signatures carry the encoding.
    private static readonly MacFileDialog.BlockDescriptor* s_boolDescriptor =
        NewDescriptor(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "v@?B" : "v@?c");
    private static readonly MacFileDialog.BlockDescriptor* s_boolErrorDescriptor =
        NewDescriptor(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "v@?B@" : "v@?c@");

    private static readonly Lock s_gate = new();
    private static readonly Dictionary<nint, Action<string, JsonObject, string>> s_observers = [];
    private static nint s_observerClass;

    /// <summary>Electron's <c>NSSystemColorToSkColor</c> on <c>[NSColor selector]</c> as ARGB, or null when NSColor has no
    /// such colour.</summary>
    public static uint? NamedColor(string selector)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var cls = Class("NSColor");
            var sel = Sel(selector);
            if (!RespondsTo(cls, sel)) return null;
            var color = Send(cls, sel);
            return color == 0 ? null : SystemColorToArgb(color);
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    // NSSystemColorToSkColor: device RGB, else the CGColor in sRGB, else its grey and alpha.
    private static uint SystemColorToArgb(nint color)
    {
        var device = Send(color, Sel("colorUsingColorSpace:"), Send(Class("NSColorSpace"), Sel("deviceRGBColorSpace")));
        if (device != 0)
        {
            double r, g, b, a;
            ((delegate* unmanaged<nint, nint, double*, double*, double*, double*, void>)s_msgSend)(device, Sel("getRed:green:blue:alpha:"), &r, &g, &b, &a);
            return MacPreferenceRules.SkColor4fToArgb(r, g, b, a);
        }
        var cg = Send(color, Sel("CGColor"));
        if (CGColorGetNumberOfComponents(cg) == 4)
        {
            var srgb = CGColorSpaceCreateWithName(*(nint*)NativeLibrary.GetExport(NativeLibrary.Load(CoreGraphics), "kCGColorSpaceSRGB"));
            var matched = CGColorCreateCopyByMatchingToColorSpace(srgb, 0, cg, 0);
            CFRelease(srgb);
            if (matched == 0) return 0;
            try
            {
                var c = CGColorGetComponents(matched);
                return MacPreferenceRules.SkColor4fToArgb(c[0], c[1], c[2], c[3]);
            }
            finally { CFRelease(matched); }
        }
        var grey = *CGColorGetComponents(cg);
        return MacPreferenceRules.SkColor4fToArgb(grey, grey, grey, CGColorGetAlpha(cg));
    }

    /// <summary><c>[AVCaptureDevice authorizationStatusForMediaType:]</c> for video or audio: 0 not determined,
    /// 1 restricted, 2 denied, 3 authorized.</summary>
    public static long MediaAuthorizationStatus(bool video)
    {
        var pool = objc_autoreleasePoolPush();
        try { return Send(Class("AVCaptureDevice"), Sel("authorizationStatusForMediaType:"), MediaType(video)); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary><c>CGPreflightScreenCaptureAccess()</c>, Chromium's <c>IsScreenCaptureAllowed</c>.</summary>
    public static bool ScreenCaptureAllowed() => CGPreflightScreenCaptureAccess() != 0;

    /// <summary><c>[AVCaptureDevice requestAccessForMediaType:completionHandler:]</c>; <paramref name="done"/> runs once, on a
    /// queue of AVFoundation's.</summary>
    public static void RequestMediaAccess(bool video, Action<bool> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        var pool = objc_autoreleasePoolPush();
        try
        {
            var block = new MacFileDialog.Block
            {
                Isa = s_stackBlock,
                Flags = BlockHasSignature,
                Invoke = (nint)(delegate* unmanaged<MacFileDialog.Block*, byte, void>)&OnMediaAccess,
                Descriptor = s_boolDescriptor,
                Context = GCHandle.ToIntPtr(GCHandle.Alloc(done)),
            };
            ((delegate* unmanaged<nint, nint, nint, MacFileDialog.Block*, void>)s_msgSend)(
                Class("AVCaptureDevice"), Sel("requestAccessForMediaType:completionHandler:"), MediaType(video), &block);
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    // AVFoundation copies the stack block and calls the copy once.
    [UnmanagedCallersOnly]
    private static void OnMediaAccess(MacFileDialog.Block* block, byte granted)
    {
        var handle = GCHandle.FromIntPtr(block->Context);
        var done = (Action<bool>)handle.Target!;
        handle.Free();
        try { done(granted != 0); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception in askForMediaAccess's completion: {e}"); }
    }

    private static nint MediaType(bool video)
        => *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(AVFoundation), video ? "AVMediaTypeVideo" : "AVMediaTypeAudio");

    /// <summary><c>NSApp.effectiveAppearance.name</c>, or null when there is no appearance.</summary>
    public static string? EffectiveAppearanceName()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var appearance = Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("effectiveAppearance"));
            return appearance == 0 ? null : Read(Send(appearance, Sel("name")));
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary><c>AXIsProcessTrustedWithOptions(@{kAXTrustedCheckOptionPrompt: @(prompt)})</c>.</summary>
    public static bool IsTrustedAccessibilityClient(bool prompt)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var key = *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(ApplicationServices), "kAXTrustedCheckOptionPrompt");
            var number = ((delegate* unmanaged<nint, nint, byte, nint>)s_msgSend)(Class("NSNumber"), Sel("numberWithBool:"), prompt ? (byte)1 : (byte)0);
            var options = Send(Class("NSDictionary"), Sel("dictionaryWithObject:forKey:"), number, key);
            return AXIsProcessTrustedWithOptions(options) != 0;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>Electron's <c>CanPromptTouchID</c>: LocalAuthentication can evaluate biometrics-or-watch and the biometry
    /// is Touch ID.</summary>
    public static bool CanPromptTouchID()
    {
        _ = NativeLibrary.Load(LocalAuthentication);
        var context = Send(Send(Class("LAContext"), Sel("alloc")), Sel("init"));
        try
        {
            if (((delegate* unmanaged<nint, nint, nint, nint*, byte>)s_msgSend)(context, Sel("canEvaluatePolicy:error:"), BiometricsOrWatch, null) == 0)
                return false;
            return Send(context, Sel("biometryType")) == TouchIdBiometry;
        }
        finally { Send(context, Sel("release")); }
    }

    private sealed record TouchIdRequest(nint Context, Action<bool, string> Done);

    /// <summary>Electron's <c>PromptTouchID</c>: <c>evaluateAccessControl:</c> with private-key-usage and user-presence
    /// access control, for key signing. <paramref name="done"/> gets success, or failure and the error's
    /// <c>localizedDescription</c>, once, on a LocalAuthentication queue.</summary>
    public static void PromptTouchID(string reason, Action<bool, string> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        _ = NativeLibrary.Load(LocalAuthentication);
        var pool = objc_autoreleasePoolPush();
        try
        {
            var context = Send(Send(Class("LAContext"), Sel("alloc")), Sel("init"));
            var protection = *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(Security), "kSecAttrAccessibleWhenUnlockedThisDeviceOnly");
            var access = SecAccessControlCreateWithFlags(0, protection, TouchIdFlags, null);
            var block = new MacFileDialog.Block
            {
                Isa = s_stackBlock,
                Flags = BlockHasSignature,
                Invoke = (nint)(delegate* unmanaged<MacFileDialog.Block*, byte, nint, void>)&OnTouchId,
                Descriptor = s_boolErrorDescriptor,
                Context = GCHandle.ToIntPtr(GCHandle.Alloc(new TouchIdRequest(context, done))),
            };
            ((delegate* unmanaged<nint, nint, nint, nint, nint, MacFileDialog.Block*, void>)s_msgSend)(
                context, Sel("evaluateAccessControl:operation:localizedReason:reply:"), access, UseKeySign, NSString(reason), &block);
            if (access != 0) CFRelease(access);
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    // The context lives until its reply, so dropping the last reference cannot cancel the prompt.
    [UnmanagedCallersOnly]
    private static void OnTouchId(MacFileDialog.Block* block, byte success, nint error)
    {
        var handle = GCHandle.FromIntPtr(block->Context);
        var request = (TouchIdRequest)handle.Target!;
        handle.Free();
        try
        {
            var message = success != 0 || error == 0 ? "" : Read(Send(error, Sel("localizedDescription"))) ?? "";
            request.Done(success != 0, message);
        }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception in promptTouchID's reply: {e}"); }
        finally { Send(request.Context, Sel("release")); }
    }

    /// <summary><c>NSWorkspace.accessibilityDisplayShouldReduceMotion</c>.</summary>
    public static bool ReduceMotion => (byte)Send(Send(Class("NSWorkspace"), Sel("sharedWorkspace")), Sel("accessibilityDisplayShouldReduceMotion")) != 0;

    /// <summary><c>[NSEvent isSwipeTrackingFromScrollEventsEnabled]</c>.</summary>
    public static bool IsSwipeTrackingFromScrollEventsEnabled() => (byte)Send(Class("NSEvent"), Sel("isSwipeTrackingFromScrollEventsEnabled")) != 0;

    /// <summary>The standard defaults' <c>boolValue</c> for <paramref name="key"/>, or null when it has no object.</summary>
    public static bool? DefaultsFlag(string key)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var value = Send(Defaults(), Sel("objectForKey:"), NSString(key));
            return value == 0 ? null : (byte)Send(value, Sel("boolValue")) != 0;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary><c>stringForKey:</c>, "" for nil.</summary>
    public static string GetString(string key) => WithPool(() => Read(Send(Defaults(), Sel("stringForKey:"), NSString(key))) ?? "");

    /// <summary><c>boolForKey:</c>.</summary>
    public static bool GetBool(string key) => WithPool(() => (byte)Send(Defaults(), Sel("boolForKey:"), NSString(key)) != 0);

    /// <summary><c>floatForKey:</c>.</summary>
    public static float GetFloat(string key)
        => WithPool(() => ((delegate* unmanaged<nint, nint, nint, float>)s_msgSend)(Defaults(), Sel("floatForKey:"), NSString(key)));

    /// <summary><c>integerForKey:</c>.</summary>
    public static long GetInteger(string key) => WithPool(() => (long)Send(Defaults(), Sel("integerForKey:"), NSString(key)));

    /// <summary><c>doubleForKey:</c>.</summary>
    public static double GetDouble(string key)
        => WithPool(() => ((delegate* unmanaged<nint, nint, nint, double>)s_msgSend)(Defaults(), Sel("doubleForKey:"), NSString(key)));

    /// <summary><c>URLForKey:</c>'s <c>absoluteString</c>, or null for nil.</summary>
    public static string? GetUrl(string key) => WithPool(() =>
    {
        var url = Send(Defaults(), Sel("URLForKey:"), NSString(key));
        return url == 0 ? null : Read(Send(url, Sel("absoluteString")));
    });

    /// <summary>Electron's <c>NSArrayToValue([defaults arrayForKey:])</c>: empty for nil.</summary>
    public static JsonArray GetArray(string key) => WithPool(() => ToJsonArray(Send(Defaults(), Sel("arrayForKey:"), NSString(key))));

    /// <summary>Electron's <c>NSDictionaryToValue([defaults dictionaryForKey:])</c>: empty for nil.</summary>
    public static JsonObject GetDictionary(string key) => WithPool(() => ToJsonObject(Send(Defaults(), Sel("dictionaryForKey:"), NSString(key))));

    /// <summary><c>setObject:forKey:</c> with a string.</summary>
    public static void SetString(string key, string value) => WithPool(() => Send(Defaults(), Sel("setObject:forKey:"), NSString(value), NSString(key)));

    /// <summary><c>setBool:forKey:</c>.</summary>
    public static void SetBool(string key, bool value)
        => WithPool(() => ((delegate* unmanaged<nint, nint, byte, nint, void>)s_msgSend)(Defaults(), Sel("setBool:forKey:"), value ? (byte)1 : (byte)0, NSString(key)));

    /// <summary><c>setFloat:forKey:</c>.</summary>
    public static void SetFloat(string key, float value)
        => WithPool(() => ((delegate* unmanaged<nint, nint, float, nint, void>)s_msgSend)(Defaults(), Sel("setFloat:forKey:"), value, NSString(key)));

    /// <summary><c>setInteger:forKey:</c>.</summary>
    public static void SetInteger(string key, long value)
        => WithPool(() => ((delegate* unmanaged<nint, nint, nint, nint, void>)s_msgSend)(Defaults(), Sel("setInteger:forKey:"), (nint)value, NSString(key)));

    /// <summary><c>setDouble:forKey:</c>.</summary>
    public static void SetDouble(string key, double value)
        => WithPool(() => ((delegate* unmanaged<nint, nint, double, nint, void>)s_msgSend)(Defaults(), Sel("setDouble:forKey:"), value, NSString(key)));

    /// <summary><c>setURL:forKey:</c> with <c>[NSURL URLWithString:spec]</c>; false when NSURL refuses the spec.</summary>
    public static bool SetUrl(string key, string spec) => WithPool(() =>
    {
        var url = Send(Class("NSURL"), Sel("URLWithString:"), NSString(spec));
        if (url == 0) return false;
        Send(Defaults(), Sel("setURL:forKey:"), url, NSString(key));
        return true;
    });

    /// <summary><c>setObject:forKey:</c> with the NSArray or NSDictionary <c>NSJSONSerialization</c> makes of
    /// <paramref name="json"/>, as Electron's <c>ListValueToNSArray</c> and <c>DictionaryValueToNSDictionary</c>; false
    /// when it makes nothing of that kind.</summary>
    public static bool SetJson(string key, string json, bool array) => WithPool(() =>
    {
        var value = FromJson(json, array ? "NSArray" : "NSDictionary");
        if (value == 0) return false;
        Send(Defaults(), Sel("setObject:forKey:"), value, NSString(key));
        return true;
    });

    /// <summary>Electron's <c>RegisterDefaults</c>: <c>registerDefaults:</c> with the NSDictionary of
    /// <paramref name="json"/>; false, registering nothing, when there is none or a value is <c>NSNull</c>.</summary>
    public static bool RegisterDefaults(string json) => WithPool(() =>
    {
        var dict = FromJson(json, "NSDictionary");
        if (dict == 0) return false;
        var values = Send(dict, Sel("allValues"));
        var count = (long)Send(values, Sel("count"));
        var nsNull = Class("NSNull");
        for (long i = 0; i < count; i++)
            if (IsKind(Send(values, Sel("objectAtIndex:"), (nint)i), nsNull)) return false;
        Send(Defaults(), Sel("registerDefaults:"), dict);
        return true;
    });

    /// <summary><c>removeObjectForKey:</c>.</summary>
    public static void Remove(string key) => WithPool(() => Send(Defaults(), Sel("removeObjectForKey:"), NSString(key)));

    /// <summary>Observes <paramref name="name"/> (every name when null) on <paramref name="center"/> through a new observer
    /// object, returned. <paramref name="callback"/> gets the name, Electron's <c>NSDictionaryToValue</c> of the user info
    /// and the object when it is a string (else ""), on the posting thread.</summary>
    public static nint AddObserver(MacNotificationCenter center, string? name, Action<string, JsonObject, string> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var pool = objc_autoreleasePoolPush();
        try
        {
            nint observer;
            lock (s_gate)
            {
                if (s_observerClass == 0) s_observerClass = RegisterObserverClass();
                observer = Send(Send(s_observerClass, Sel("alloc")), Sel("init"));
                s_observers[observer] = callback;
            }
            ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)s_msgSend)(
                Center(center), Sel("addObserver:selector:name:object:"), observer, Sel("riftwayNotify:"), name is null ? 0 : NSString(name), 0);
            return observer;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary><c>removeObserver:</c> on <paramref name="center"/>; nothing when the observer is registered elsewhere.</summary>
    public static void RemoveObserver(MacNotificationCenter center, nint observer)
        => WithPool(() => Send(Center(center), Sel("removeObserver:"), observer));

    /// <summary>Drops the observer's callback and releases it; call after <see cref="RemoveObserver"/> on its own centre.</summary>
    public static void ReleaseObserver(nint observer)
    {
        lock (s_gate)
            if (!s_observers.Remove(observer)) return;
        Send(observer, Sel("release"));
    }

    /// <summary>Electron's <c>Post*Notification</c>: <paramref name="name"/> with no object and the user info that
    /// <c>NSJSONSerialization</c> makes of <paramref name="userInfoJson"/> (nil when it makes no dictionary).
    /// <paramref name="deliverImmediately"/> applies to the distributed centre only.</summary>
    public static void Post(MacNotificationCenter center, string name, string? userInfoJson, bool deliverImmediately)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var info = userInfoJson is null ? 0 : FromJson(userInfoJson, "NSDictionary");
            if (center == MacNotificationCenter.Distributed)
                ((delegate* unmanaged<nint, nint, nint, nint, nint, byte, void>)s_msgSend)(Center(center),
                    Sel("postNotificationName:object:userInfo:deliverImmediately:"), NSString(name), 0, info, deliverImmediately ? (byte)1 : (byte)0);
            else
                ((delegate* unmanaged<nint, nint, nint, nint, nint, void>)s_msgSend)(Center(center),
                    Sel("postNotificationName:object:userInfo:"), NSString(name), 0, info);
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    // AppKit registers NSColor, NSEvent and NSWorkspace; a process without it (a test host) has none of them.
    private static nint LoadMsgSend()
    {
        _ = NativeLibrary.Load(AppKit);
        return NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    }

    private static nint RegisterObserverClass()
    {
        var cls = Class("RiftwaySystemPreferencesObserver");
        if (cls != 0) return cls;
        fixed (byte* name = "RiftwaySystemPreferencesObserver\0"u8)
            cls = objc_allocateClassPair(Class("NSObject"), name, 0);
        fixed (byte* types = "v@:@\0"u8)
            _ = class_addMethod(cls, Sel("riftwayNotify:"), (delegate* unmanaged<nint, nint, nint, void>)&OnNotify, types);
        objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    private static void OnNotify(nint self, nint cmd, nint notification)
    {
        Action<string, JsonObject, string>? callback;
        lock (s_gate) s_observers.TryGetValue(self, out callback);
        if (callback is null) return;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var name = Read(Send(notification, Sel("name"))) ?? "";
            var sender = Send(notification, Sel("object"));
            var senderText = sender != 0 && IsKind(sender, Class("NSString")) ? Read(sender) ?? "" : "";
            callback(name, ToJsonObject(Send(notification, Sel("userInfo"))), senderText);
        }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception in a systemPreferences notification callback: {e}"); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static nint Center(MacNotificationCenter center) => center switch
    {
        MacNotificationCenter.Distributed => Send(Class("NSDistributedNotificationCenter"), Sel("defaultCenter")),
        MacNotificationCenter.Local => Send(Class("NSNotificationCenter"), Sel("defaultCenter")),
        _ => Send(Send(Class("NSWorkspace"), Sel("sharedWorkspace")), Sel("notificationCenter")),
    };

    // Electron's NSArrayToValue: an empty list for nil.
    private static JsonArray ToJsonArray(nint array)
    {
        var result = new JsonArray();
        if (array == 0) return result;
        var count = (long)Send(array, Sel("count"));
        for (long i = 0; i < count; i++) result.Add(ToJsonValue(Send(array, Sel("objectAtIndex:"), (nint)i)));
        return result;
    }

    // Electron's NSDictionaryToValue: keys as strings (or their description), sorted as base::DictValue keeps them.
    private static JsonObject ToJsonObject(nint dict)
    {
        var result = new JsonObject();
        if (dict == 0) return result;
        var keys = Send(dict, Sel("allKeys"));
        var count = (long)Send(keys, Sel("count"));
        var nsString = Class("NSString");
        var entries = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        for (long i = 0; i < count; i++)
        {
            var key = Send(keys, Sel("objectAtIndex:"), (nint)i);
            var text = Read(IsKind(key, nsString) ? key : Send(key, Sel("description"))) ?? "";
            entries[text] = ToJsonValue(Send(dict, Sel("objectForKey:"), key));
        }
        foreach (var (key, value) in entries) result[key] = value;
        return result;
    }

    private static JsonNode? ToJsonValue(nint value)
    {
        if (IsKind(value, Class("NSString"))) return JsonValue.Create(Read(value) ?? "");
        if (IsKind(value, Class("NSNumber")))
        {
            return MacPreferenceRules.NumberKind(Marshal.PtrToStringUTF8(Send(value, Sel("objCType"))) ?? "") switch
            {
                MacNumberKind.Bool => JsonValue.Create((byte)Send(value, Sel("boolValue")) != 0),
                MacNumberKind.Double => JsonValue.Create(((delegate* unmanaged<nint, nint, double>)s_msgSend)(value, Sel("doubleValue"))),
                _ => JsonValue.Create(((delegate* unmanaged<nint, nint, int>)s_msgSend)(value, Sel("intValue"))),
            };
        }
        if (IsKind(value, Class("NSArray"))) return ToJsonArray(value);
        if (IsKind(value, Class("NSDictionary"))) return ToJsonObject(value);
        return JsonValue.Create(Read(Send(value, Sel("description"))) ?? "");
    }

    // [NSJSONSerialization JSONObjectWithData:options:0 error:nil], kept only when it is of the wanted class.
    private static nint FromJson(string json, string kind)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        nint data;
        fixed (byte* p = bytes)
            data = ((delegate* unmanaged<nint, nint, byte*, nuint, nint>)s_msgSend)(Class("NSData"), Sel("dataWithBytes:length:"), p, (nuint)bytes.Length);
        var obj = ((delegate* unmanaged<nint, nint, nint, nuint, nint*, nint>)s_msgSend)(Class("NSJSONSerialization"), Sel("JSONObjectWithData:options:error:"), data, 0, null);
        return obj != 0 && IsKind(obj, Class(kind)) ? obj : 0;
    }

    private static nint Defaults() => Send(Class("NSUserDefaults"), Sel("standardUserDefaults"));

    private static bool IsKind(nint obj, nint cls) => (byte)Send(obj, Sel("isKindOfClass:"), cls) != 0;

    private static bool RespondsTo(nint obj, nint selector) => (byte)Send(obj, Sel("respondsToSelector:"), selector) != 0;

    private static T WithPool<T>(Func<T> body)
    {
        var pool = objc_autoreleasePoolPush();
        try { return body(); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static void WithPool(Action body)
    {
        var pool = objc_autoreleasePoolPush();
        try { body(); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static MacFileDialog.BlockDescriptor* NewDescriptor(string signature)
    {
        var descriptor = (MacFileDialog.BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(MacFileDialog.BlockDescriptor));
        descriptor->Reserved = 0;
        descriptor->Size = (nuint)sizeof(MacFileDialog.Block);
        descriptor->Signature = (byte*)Marshal.StringToCoTaskMemUTF8(signature);
        return descriptor;
    }

    private static nint NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static string? Read(nint nsString) => nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    private static nint Class(string name)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try { return objc_getClass((byte*)utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static nint Sel(string name)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(name);
        try { return sel_registerName((byte*)utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint a) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, a);

    private static nint Send(nint receiver, nint selector, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_msgSend)(receiver, selector, a, b);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint cf);
    [DllImport(CoreGraphics)] private static extern nuint CGColorGetNumberOfComponents(nint color);
    [DllImport(CoreGraphics)] private static extern double* CGColorGetComponents(nint color);
    [DllImport(CoreGraphics)] private static extern double CGColorGetAlpha(nint color);
    [DllImport(CoreGraphics)] private static extern nint CGColorSpaceCreateWithName(nint name);
    [DllImport(CoreGraphics)] private static extern nint CGColorCreateCopyByMatchingToColorSpace(nint space, int intent, nint color, nint options);
    [DllImport(CoreGraphics)] private static extern byte CGPreflightScreenCaptureAccess();
    [DllImport(ApplicationServices)] private static extern byte AXIsProcessTrustedWithOptions(nint options);
    [DllImport(Security)] private static extern nint SecAccessControlCreateWithFlags(nint allocator, nint protection, nuint flags, nint* error);
}
