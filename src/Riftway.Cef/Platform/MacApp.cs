using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>What Electron's <c>ElectronApplicationDelegate</c> reports; set by the app before <see cref="MacApp.InstallDelegate"/>.</summary>
public sealed class MacAppDelegateHandlers
{
    /// <summary><c>applicationWillFinishLaunching:</c>.</summary>
    public Action? WillFinishLaunching { get; init; }

    /// <summary><c>applicationDidFinishLaunching:</c>, with whether the launch was as a login item.</summary>
    public Action<bool, System.Text.Json.Nodes.JsonObject>? DidFinishLaunching { get; init; }

    /// <summary><c>applicationDidBecomeActive:</c>.</summary>
    public Action? DidBecomeActive { get; init; }

    /// <summary><c>applicationDidResignActive:</c>.</summary>
    public Action? DidResignActive { get; init; }

    /// <summary><c>application:openFile:</c>; returns whether the app handled the file.</summary>
    public Func<string, bool>? OpenFile { get; init; }

    /// <summary>The <c>kAEGetURL</c> Apple event's URL.</summary>
    public Action<string>? OpenUrl { get; init; }

    /// <summary><c>applicationShouldHandleReopen:hasVisibleWindows:</c>, with the flag.</summary>
    public Action<bool>? Reopen { get; init; }

    /// <summary><c>application:willContinueUserActivityWithType:</c>; returns whether the app handles it.</summary>
    public Func<string, bool>? WillContinueUserActivity { get; init; }

    /// <summary><c>application:continueUserActivity:restorationHandler:</c>, with the type, the user info and the webpage
    /// URL (null without one); returns whether the app handled it.</summary>
    public Func<string, System.Text.Json.Nodes.JsonObject, string?, bool>? ContinueUserActivity { get; init; }

    /// <summary><c>application:didFailToContinueUserActivityWithType:error:</c>, with the error's description.</summary>
    public Action<string, string>? DidFailToContinueUserActivity { get; init; }

    /// <summary><c>newWindowForTab:</c> with no window to answer it: the tab bar's + button.</summary>
    public Action? NewWindowForTab { get; init; }

    /// <summary><c>application:didRegisterForRemoteNotificationsWithDeviceToken:</c>, with the token's bytes.</summary>
    public Action<byte[]>? DidRegisterForRemoteNotifications { get; init; }

    /// <summary><c>application:didFailToRegisterForRemoteNotificationsWithError:</c>, with Electron's
    /// <c>"%ld %@ %@"</c> of the error's code, domain and user info.</summary>
    public Action<string>? DidFailToRegisterForRemoteNotifications { get; init; }

    /// <summary><c>application:didReceiveRemoteNotification:</c>, with the user info as dict_util converts it.</summary>
    public Action<System.Text.Json.Nodes.JsonObject>? DidReceiveRemoteNotification { get; init; }
}

/// <summary>The AppKit, LaunchServices and ServiceManagement calls behind Electron's macOS <c>app</c>
/// (<c>browser_mac.mm</c>, <c>electron_api_app_mac.mm</c>, <c>electron_application_delegate.mm</c>,
/// <c>electron_bundle_mover.mm</c>, <c>platform_util_mac.mm</c>). AppKit calls belong on the main thread.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacApp
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string LibC = "/usr/lib/libSystem.B.dylib";
    // FourCharCodes: kInternetEventClass and kAEGetURL ('GURL'), keyDirectObject ('----'), kAEOpenApplication
    // ('oapp'), keyAEPropData ('prdt'), keyAELaunchedAsLogInItem ('lgit'), kTSMDocumentEnabledInputSourcesPropertyTag ('enis').
    private const uint GetUrl = 0x4755524C;
    private const uint DirectObject = 0x2D2D2D2D;
    private const uint OpenApplication = 0x6F617070;
    private const uint PropData = 0x70726474;
    private const uint LaunchedAsLogInItem = 0x6C676974;
    private const uint EnabledInputSources = 0x656E6973;
    private const nint ApplicationDirectory = 1;
    private const nint AllDomainsMask = 0xFFFF;
    private const nint PngFileType = 4;
    private const uint LSRolesAll = 0xFFFFFFFF;

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    // NSWorkspace, NSDocumentController and NSApplication are AppKit classes, unknown to the runtime until it loads.
    private static readonly nint s_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    private static MacAppDelegateHandlers? s_handlers;
    private static bool s_delegateInstalled;
    private static bool s_secureInput;

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize
    {
        public double Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }

    /// <summary>Makes NSApp's delegate answer as Electron's <c>ElectronApplicationDelegate</c> does, and registers the
    /// <c>kAEGetURL</c> handler as its <c>registerURLHandler</c> does; idempotent. Call before NSApp finishes launching.</summary>
    public static void InstallDelegate(MacAppDelegateHandlers handlers)
    {
        s_handlers = handlers;
        if (s_delegateInstalled) return;
        s_delegateInstalled = true;
        var app = App();
        var existing = Send(app, Sel("delegate"u8));
        // A delegate that only answers the Dock menu may already be there (MacActivation's); it gains these methods.
        var cls = existing != 0 ? object_getClass(existing) : NewClass("RiftwayAppDelegate"u8);
        Add(cls, "applicationWillFinishLaunching:"u8, (delegate* unmanaged<nint, nint, nint, void>)&OnWillFinishLaunching, "v@:@"u8);
        Add(cls, "applicationDidFinishLaunching:"u8, (delegate* unmanaged<nint, nint, nint, void>)&OnDidFinishLaunching, "v@:@"u8);
        Add(cls, "applicationDidBecomeActive:"u8, (delegate* unmanaged<nint, nint, nint, void>)&OnDidBecomeActive, "v@:@"u8);
        Add(cls, "applicationDidResignActive:"u8, (delegate* unmanaged<nint, nint, nint, void>)&OnDidResignActive, "v@:@"u8);
        Add(cls, "newWindowForTab:"u8, (delegate* unmanaged<nint, nint, nint, void>)&OnNewWindowForTab, "v@:@"u8);
        Add(cls, "application:openFile:"u8, (delegate* unmanaged<nint, nint, nint, nint, byte>)&OnOpenFile, ObjCEncoding.Of("B@:@@"));
        Add(cls, "applicationShouldHandleReopen:hasVisibleWindows:"u8, (delegate* unmanaged<nint, nint, nint, byte, byte>)&OnReopen, ObjCEncoding.Of("B@:@B"));
        Add(cls, "application:willContinueUserActivityWithType:"u8, (delegate* unmanaged<nint, nint, nint, nint, byte>)&OnWillContinueUserActivity, ObjCEncoding.Of("B@:@@"));
        Add(cls, "application:continueUserActivity:restorationHandler:"u8, (delegate* unmanaged<nint, nint, nint, nint, nint, byte>)&OnContinueUserActivity, ObjCEncoding.Of("B@:@@@?"));
        Add(cls, "application:didFailToContinueUserActivityWithType:error:"u8, (delegate* unmanaged<nint, nint, nint, nint, nint, void>)&OnDidFailToContinueUserActivity, "v@:@@@"u8);
        Add(cls, "application:didRegisterForRemoteNotificationsWithDeviceToken:"u8, (delegate* unmanaged<nint, nint, nint, nint, void>)&OnDidRegisterForRemoteNotifications, "v@:@@"u8);
        Add(cls, "application:didFailToRegisterForRemoteNotificationsWithError:"u8, (delegate* unmanaged<nint, nint, nint, nint, void>)&OnDidFailToRegisterForRemoteNotifications, "v@:@@"u8);
        Add(cls, "application:didReceiveRemoteNotification:"u8, (delegate* unmanaged<nint, nint, nint, nint, void>)&OnDidReceiveRemoteNotification, "v@:@@"u8);
        Add(cls, "applicationSupportsSecureRestorableState:"u8, (delegate* unmanaged<nint, nint, nint, byte>)&SupportsSecureRestorableState, ObjCEncoding.Of("B@:@"));
        Add(cls, "riftwayHandleURLEvent:withReplyEvent:"u8, (delegate* unmanaged<nint, nint, nint, nint, void>)&OnUrlEvent, "v@:@@"u8);
        var target = existing;
        if (target == 0)
        {
            objc_registerClassPair(cls);
            // NSApp holds its delegate weakly; this one lives for the process.
            target = Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
            Send(app, Sel("setDelegate:"u8), target);
        }
        var manager = Send(Class("NSAppleEventManager"u8), Sel("sharedAppleEventManager"u8));
        ((delegate* unmanaged<nint, nint, nint, nint, uint, uint, void>)s_msgSend)(manager,
            Sel("setEventHandler:andSelector:forEventClass:andEventID:"u8), target, Sel("riftwayHandleURLEvent:withReplyEvent:"u8), GetUrl, GetUrl);
    }

    /// <summary>The class name of NSApp's delegate, or empty; for tests.</summary>
    public static string DelegateClassName()
    {
        var d = Send(App(), Sel("delegate"u8));
        return d == 0 ? "" : Marshal.PtrToStringUTF8(class_getName(object_getClass(d))) ?? "";
    }

    [UnmanagedCallersOnly]
    private static void OnWillFinishLaunching(nint self, nint cmd, nint notification) => Guard(() => s_handlers?.WillFinishLaunching?.Invoke());

    [UnmanagedCallersOnly]
    private static void OnDidFinishLaunching(nint self, nint cmd, nint notification) => Guard(() =>
    {
        var manager = Send(Class("NSAppleEventManager"u8), Sel("sharedAppleEventManager"u8));
        var appleEvent = Send(manager, Sel("currentAppleEvent"u8));
        var atLogin = false;
        if (appleEvent != 0 && (uint)Send(appleEvent, Sel("eventID"u8)) == OpenApplication)
        {
            var data = ((delegate* unmanaged<nint, nint, uint, nint>)s_msgSend)(appleEvent, Sel("paramDescriptorForKeyword:"u8), PropData);
            atLogin = data != 0 && (uint)Send(data, Sel("enumCodeValue"u8)) == LaunchedAsLogInItem;
        }
        s_finishedLaunching = true;
        s_handlers?.DidFinishLaunching?.Invoke(atLogin, LaunchInfo(notification));
    });

    private static bool s_finishedLaunching;

    /// <summary>Whether AppKit has delivered <c>applicationDidFinishLaunching:</c> to Riftway's delegate.</summary>
    public static bool HasFinishedLaunching => s_finishedLaunching;

    // Electron's applicationDidFinishLaunching: launch info: the notification the app was launched from, an
    // NSUserNotification's user info or a UNNotificationResponse's fields, through dict_util; empty for a plain launch.
    internal static System.Text.Json.Nodes.JsonObject LaunchInfo(nint notification)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var userInfo = notification == 0 ? 0 : Send(notification, Sel("userInfo"u8));
            var key = *(nint*)NativeLibrary.GetExport(s_appKit, "NSApplicationLaunchUserNotificationKey");
            var launchedFrom = userInfo == 0 ? 0 : Send(userInfo, Sel("objectForKey:"u8), key);
            if (launchedFrom == 0) return [];
            var legacy = Class("NSUserNotification"u8);
            var info = legacy != 0 && (byte)Send(launchedFrom, Sel("isKindOfClass:"u8), legacy) != 0
                ? Send(launchedFrom, Sel("userInfo"u8))
                : ResponseDictionary(launchedFrom);
            return MacUserActivity.ToJson(info);
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static bool RespondsTo(nint receiver, nint selector) => (byte)Send(receiver, Sel("respondsToSelector:"u8), selector) != 0;

    // UNNotificationResponseToNSDictionary.
    private static nint ResponseDictionary(nint response)
    {
        if (!RespondsTo(response, Sel("actionIdentifier"u8)) || !RespondsTo(response, Sel("notification"u8))) return 0;
        var result = Send(Class("NSMutableDictionary"u8), Sel("dictionary"u8));
        void Put(string name, nint value)
        {
            var nameString = NSString(name);
            // setObject:forKeyedSubscript: drops the key for nil, as Objective-C subscripting does.
            ((delegate* unmanaged<nint, nint, nint, nint, void>)s_msgSend)(result, Sel("setObject:forKeyedSubscript:"u8), value, nameString);
        }
        var shown = Send(response, Sel("notification"u8));
        var request = Send(shown, Sel("request"u8));
        Put("actionIdentifier", Send(response, Sel("actionIdentifier"u8)));
        var since1970 = ((delegate* unmanaged<nint, nint, double>)s_msgSend)(Send(shown, Sel("date"u8)), Sel("timeIntervalSince1970"u8));
        Put("date", ((delegate* unmanaged<nint, nint, double, nint>)s_msgSend)(Class("NSNumber"u8), Sel("numberWithDouble:"u8), since1970));
        Put("identifier", Send(request, Sel("identifier"u8)));
        Put("userInfo", Send(Send(request, Sel("content"u8)), Sel("userInfo"u8)));
        if (RespondsTo(response, Sel("userText"u8))) Put("userText", Send(response, Sel("userText"u8)));
        return result;
    }

    [UnmanagedCallersOnly]
    private static void OnDidBecomeActive(nint self, nint cmd, nint notification) => Guard(() => s_handlers?.DidBecomeActive?.Invoke());

    [UnmanagedCallersOnly]
    private static void OnDidResignActive(nint self, nint cmd, nint notification) => Guard(() => s_handlers?.DidResignActive?.Invoke());

    [UnmanagedCallersOnly]
    private static void OnNewWindowForTab(nint self, nint cmd, nint sender) => Guard(() => s_handlers?.NewWindowForTab?.Invoke());

    [UnmanagedCallersOnly]
    private static byte OnOpenFile(nint self, nint cmd, nint sender, nint filename)
    {
        var handled = false;
        Guard(() => handled = s_handlers?.OpenFile?.Invoke(ReadString(filename)) ?? false);
        return handled ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly]
    private static byte OnReopen(nint self, nint cmd, nint sender, byte hasVisibleWindows)
    {
        Guard(() => s_handlers?.Reopen?.Invoke(hasVisibleWindows != 0));
        return hasVisibleWindows;
    }

    [UnmanagedCallersOnly]
    private static byte SupportsSecureRestorableState(nint self, nint cmd, nint app) => 1;

    [UnmanagedCallersOnly]
    private static void OnUrlEvent(nint self, nint cmd, nint appleEvent, nint reply) => Guard(() =>
    {
        var url = ((delegate* unmanaged<nint, nint, uint, nint>)s_msgSend)(appleEvent, Sel("paramDescriptorForKeyword:"u8), DirectObject);
        s_handlers?.OpenUrl?.Invoke(url == 0 ? "" : ReadString(Send(url, Sel("stringValue"u8))));
    });

    [UnmanagedCallersOnly]
    private static byte OnWillContinueUserActivity(nint self, nint cmd, nint app, nint type)
    {
        var handled = false;
        Guard(() => handled = s_handlers?.WillContinueUserActivity?.Invoke(ReadString(type)) ?? false);
        return handled ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly]
    private static byte OnContinueUserActivity(nint self, nint cmd, nint app, nint activity, nint restorationHandler)
    {
        var handled = false;
        Guard(() =>
        {
            var url = Send(activity, Sel("webpageURL"u8));
            var userInfo = MacUserActivity.ToJson(Send(activity, Sel("userInfo"u8)));
            handled = s_handlers?.ContinueUserActivity?.Invoke(ReadString(Send(activity, Sel("activityType"u8))), userInfo,
                url == 0 ? null : ReadString(Send(url, Sel("absoluteString"u8)))) ?? false;
        });
        return handled ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly]
    private static void OnDidFailToContinueUserActivity(nint self, nint cmd, nint app, nint type, nint error)
        => Guard(() => s_handlers?.DidFailToContinueUserActivity?.Invoke(ReadString(type), ReadString(Send(error, Sel("localizedDescription"u8)))));

    [UnmanagedCallersOnly]
    private static void OnDidRegisterForRemoteNotifications(nint self, nint cmd, nint app, nint token) => Guard(() =>
    {
        var length = (int)Send(token, Sel("length"u8));
        var bytes = new byte[length];
        if (length > 0) Marshal.Copy(Send(token, Sel("bytes"u8)), bytes, 0, length);
        s_handlers?.DidRegisterForRemoteNotifications?.Invoke(bytes);
    });

    [UnmanagedCallersOnly]
    private static void OnDidFailToRegisterForRemoteNotifications(nint self, nint cmd, nint app, nint error)
        => Guard(() => s_handlers?.DidFailToRegisterForRemoteNotifications?.Invoke(RemoteNotificationError(error)));

    [UnmanagedCallersOnly]
    private static void OnDidReceiveRemoteNotification(nint self, nint cmd, nint app, nint userInfo)
        => Guard(() => s_handlers?.DidReceiveRemoteNotification?.Invoke(MacUserActivity.ToJson(userInfo)));

    /// <summary>Electron's <c>[NSString stringWithFormat:@"%ld %@ %@", error.code, error.domain, error.userInfo]</c>.</summary>
    public static string RemoteNotificationError(nint error)
        => $"{(long)Send(error, Sel("code"u8))} {ReadString(Send(Send(error, Sel("domain"u8)), Sel("description"u8)))} {ReadString(Send(Send(error, Sel("userInfo"u8)), Sel("description"u8)))}";

    /// <summary><c>[NSApp registerForRemoteNotifications]</c>.</summary>
    public static void RegisterForRemoteNotifications() => Send(App(), Sel("registerForRemoteNotifications"u8));

    /// <summary><c>[NSApp unregisterForRemoteNotifications]</c>.</summary>
    public static void UnregisterForRemoteNotifications() => Send(App(), Sel("unregisterForRemoteNotifications"u8));

    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] An app delegate handler threw: {e}"); }
    }

    /// <summary>[[NSBundle mainBundle] infoDictionary][@"CFBundleName"], or null.</summary>
    public static string? BundleName()
    {
        var info = Send(Send(Class("NSBundle"u8), Sel("mainBundle"u8)), Sel("infoDictionary"u8));
        var name = info == 0 ? 0 : Send(info, Sel("objectForKey:"u8), NSString("CFBundleName"));
        return name == 0 ? null : ReadString(name);
    }

    /// <summary>[[NSBundle mainBundle] bundleIdentifier], or null.</summary>
    public static string? BundleIdentifier()
    {
        var id = Send(Send(Class("NSBundle"u8), Sel("mainBundle"u8)), Sel("bundleIdentifier"u8));
        return id == 0 ? null : ReadString(id);
    }

    /// <summary>[[NSBundle mainBundle] bundlePath].</summary>
    public static string BundlePath() => ReadString(Send(Send(Class("NSBundle"u8), Sel("mainBundle"u8)), Sel("bundlePath"u8)));

    /// <summary>NSSearchPathForDirectoriesInDomains(NSApplicationDirectory, NSAllDomainsMask, YES).</summary>
    public static IReadOnlyList<string> ApplicationDirectories() => ReadArray(NSSearchPathForDirectoriesInDomains(ApplicationDirectory, AllDomainsMask, 1), ReadString);

    /// <summary>[NSApp isActive].</summary>
    public static bool IsActive() => (byte)Send(App(), Sel("isActive"u8)) != 0;

    /// <summary><c>[NSApp currentSystemPresentationOptions]</c>.</summary>
    public static nuint CurrentSystemPresentationOptions() => (nuint)Send(App(), Sel("currentSystemPresentationOptions"u8));

    /// <summary><c>[NSApp setPresentationOptions:]</c>.</summary>
    public static void SetPresentationOptions(nuint options) => Send(App(), Sel("setPresentationOptions:"u8), (nint)options);

    /// <summary>[NSApp setActivationPolicy:]: 0 regular, 1 accessory, 2 prohibited.</summary>
    public static void SetActivationPolicy(nint policy) => Send(App(), Sel("setActivationPolicy:"u8), policy);

    /// <summary>[NSApp activationPolicy].</summary>
    public static nint ActivationPolicy() => Send(App(), Sel("activationPolicy"u8));

    /// <summary>[NSApp orderFrontCharacterPalette:nil].</summary>
    public static void ShowCharacterPalette() => Send(App(), Sel("orderFrontCharacterPalette:"u8), 0);

    /// <summary>[NSApp orderFrontStandardAboutPanelWithOptions:] with string options, Credits as an attributed string in
    /// the text colour, as Electron's <c>Browser::ShowAboutPanel</c>.</summary>
    public static void ShowAboutPanel(IReadOnlyDictionary<string, string> options)
    {
        var dictionary = Send(Send(Class("NSMutableDictionary"u8), Sel("alloc"u8)), Sel("init"u8));
        foreach (var (key, value) in options)
        {
            var objectValue = NSString(value);
            if (key == "Credits")
            {
                var attributes = Send(Class("NSDictionary"u8), Sel("dictionaryWithObject:forKey:"u8),
                    Send(Class("NSColor"u8), Sel("textColor"u8)), NSString("NSColor"));
                objectValue = Send(Send(Send(Class("NSAttributedString"u8), Sel("alloc"u8)), Sel("initWithString:attributes:"u8), objectValue, attributes), Sel("autorelease"u8));
            }
            Send(dictionary, Sel("setObject:forKey:"u8), objectValue, NSString(key));
        }
        Send(App(), Sel("orderFrontStandardAboutPanelWithOptions:"u8), dictionary);
        Send(dictionary, Sel("release"u8));
    }

    /// <summary>ui::ScopedPasswordInputEnabler's switch: secure event input with only ASCII-capable input sources,
    /// or neither.</summary>
    public static void SetSecureKeyboardEntry(bool enabled)
    {
        if (enabled == s_secureInput) return;
        s_secureInput = enabled;
        if (enabled)
        {
            _ = EnableSecureEventInput();
            var sources = TISCreateASCIICapableInputSourceList();
            _ = TSMSetDocumentProperty(0, EnabledInputSources, (uint)sizeof(nint), &sources);
            CFRelease(sources);
        }
        else
        {
            _ = TSMRemoveDocumentProperty(0, EnabledInputSources);
            _ = DisableSecureEventInput();
        }
    }

    /// <summary>Whether this process has turned secure keyboard entry on.</summary>
    public static bool SecureKeyboardEntry() => s_secureInput;

    /// <summary>sysctl.proc_translated is 1: the process runs under Rosetta.</summary>
    public static bool RunningUnderTranslation()
    {
        int translated = 0;
        nuint size = sizeof(int);
        fixed (byte* name = "sysctl.proc_translated\0"u8)
            return sysctlbyname(name, &translated, &size, null, 0) != -1 && translated == 1;
    }

    /// <summary>[[NSDocumentController sharedDocumentController] noteNewRecentDocumentURL:] for a file URL; false when
    /// no URL can be made, which Electron only logs.</summary>
    public static bool NoteRecentDocument(string path)
    {
        var url = Send(Class("NSURL"u8), Sel("fileURLWithPath:"u8), NSString(path));
        if (url == 0) return false;
        Send(DocumentController(), Sel("noteNewRecentDocumentURL:"u8), url);
        return true;
    }

    /// <summary>[[NSDocumentController sharedDocumentController] clearRecentDocuments:nil].</summary>
    public static void ClearRecentDocuments() => Send(DocumentController(), Sel("clearRecentDocuments:"u8), 0);

    /// <summary>The paths of [[NSDocumentController sharedDocumentController] recentDocumentURLs].</summary>
    public static IReadOnlyList<string> RecentDocuments() => ReadArray(Send(DocumentController(), Sel("recentDocumentURLs"u8)), url => ReadString(Send(url, Sel("path"u8))));

    /// <summary>LSSetDefaultHandlerForURLScheme; true on noErr.</summary>
    public static bool SetDefaultHandler(string scheme, string bundleId)
    {
        var cfScheme = CFString(scheme);
        var cfId = CFString(bundleId);
        try { return LSSetDefaultHandlerForURLScheme(cfScheme, cfId) == 0; }
        finally
        {
            CFRelease(cfScheme);
            CFRelease(cfId);
        }
    }

    /// <summary>The bundle identifier of [NSWorkspace URLForApplicationToOpenURL:] for <c>scheme:</c>, or null.</summary>
    public static string? DefaultHandlerBundleId(string scheme)
    {
        var url = SchemeUrl(scheme);
        if (url == 0) return null;
        var app = Send(Workspace(), Sel("URLForApplicationToOpenURL:"u8), url);
        return app == 0 ? null : BundleIdAt(app);
    }

    /// <summary>The bundle identifiers of [NSWorkspace URLsForApplicationsToOpenURL:] for <c>scheme:</c>, in order;
    /// null where an app has none, and null for the whole list when no URL can be made.</summary>
    public static IReadOnlyList<string?>? HandlerBundleIds(string scheme)
    {
        var url = SchemeUrl(scheme);
        if (url == 0) return null;
        return ReadArray(Send(Workspace(), Sel("URLsForApplicationsToOpenURL:"u8), url), BundleIdAt);
    }

    /// <summary>Electron's <c>GetAppPathForProtocol</c>: LSCopyDefaultApplicationURLForURL's path, or null.</summary>
    public static string? AppPathForUrl(string urlSpec)
    {
        var url = Send(Class("NSURL"u8), Sel("URLWithString:"u8), NSString(urlSpec));
        if (url == 0) return null;
        nint error = 0;
        var app = LSCopyDefaultApplicationURLForURL(url, LSRolesAll, &error);
        if (error != 0)
        {
            CFRelease(error);
            if (app != 0) CFRelease(app);
            return null;
        }
        if (app == 0) return null;
        try { return ReadString(Send(app, Sel("path"u8))); }
        finally { CFRelease(app); }
    }

    /// <summary>[[NSFileManager defaultManager] displayNameAtPath:].</summary>
    public static string DisplayName(string path) => ReadString(Send(Send(Class("NSFileManager"u8), Sel("defaultManager"u8)), Sel("displayNameAtPath:"u8), NSString(path)));

    /// <summary>[[NSWorkspace sharedWorkspace] iconForFile:] drawn at its point size for each scale, as PNGs.</summary>
    public static IReadOnlyList<(byte[] Png, double Scale)> IconForFile(string path, IReadOnlyList<double> scales)
    {
        var image = Send(Workspace(), Sel("iconForFile:"u8), NSString(path));
        var result = new List<(byte[], double)>();
        if (image == 0) return result;
        var size = ((delegate* unmanaged<nint, nint, NSSize>)s_msgSend)(image, Sel("size"u8));
        foreach (var scale in scales)
        {
            var rect = new NSRect { Width = size.Width * scale, Height = size.Height * scale };
            var cg = ((delegate* unmanaged<nint, nint, NSRect*, nint, nint, nint>)s_msgSend)(image, Sel("CGImageForProposedRect:context:hints:"u8), &rect, 0, 0);
            if (cg == 0) continue;
            var rep = Send(Send(Class("NSBitmapImageRep"u8), Sel("alloc"u8)), Sel("initWithCGImage:"u8), cg);
            var png = Send(rep, Sel("representationUsingType:properties:"u8), PngFileType, Send(Class("NSDictionary"u8), Sel("dictionary"u8)));
            if (png != 0)
            {
                var length = (int)Send(png, Sel("length"u8));
                var bytes = new byte[length];
                Marshal.Copy(Send(png, Sel("bytes"u8)), bytes, 0, length);
                result.Add((bytes, scale));
            }
            Send(rep, Sel("release"u8));
        }
        return result;
    }

    /// <summary>Electron's <c>GetLoginItemEnabled</c>: the SMAppService's status as <c>not-registered</c>, <c>enabled</c>,
    /// <c>requires-approval</c> or <c>not-found</c>.</summary>
    public static string LoginItemStatus(string type, string serviceName)
    {
        var service = LoginService(type, serviceName);
        return service == 0 ? "not-registered" : LoginStatusName(Send(service, Sel("status"u8)));
    }

    // SMAppServiceStatus to Electron's names; an unknown value reads as not registered, as Electron's else branch.
    internal static string LoginStatusName(nint status) => status switch
    {
        1 => "enabled",
        2 => "requires-approval",
        3 => "not-found",
        _ => "not-registered",
    };

    /// <summary>Electron's <c>SetLoginItemEnabled</c>: registers or unregisters the SMAppService; the error's
    /// description when it fails, else null.</summary>
    public static string? SetLoginItem(string type, string serviceName, bool enabled)
    {
        var service = LoginService(type, serviceName);
        if (service == 0) return "No SMAppService for this type";
        nint error = 0;
        var ok = ((delegate* unmanaged<nint, nint, nint*, byte>)s_msgSend)(service, Sel(enabled ? "registerAndReturnError:"u8 : "unregisterAndReturnError:"u8), &error) != 0;
        return error != 0 ? ReadString(Send(error, Sel("localizedDescription"u8))) : ok ? null : "Unknown error";
    }

    // Electron's GetServiceForType.
    private static nint LoginService(string type, string serviceName)
    {
        NativeLibrary.Load("/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement");
        var cls = Class("SMAppService"u8);
        if (cls == 0) return 0;
        return type switch
        {
            "agentService" => Send(cls, Sel("agentServiceWithPlistName:"u8), NSString(serviceName)),
            "daemonService" => Send(cls, Sel("daemonServiceWithPlistName:"u8), NSString(serviceName)),
            "loginItemService" => Send(cls, Sel("loginItemServiceWithIdentifier:"u8), NSString(serviceName)),
            _ => Send(cls, Sel("mainAppService"u8)),
        };
    }

    private static string? BundleIdAt(nint appUrl)
    {
        var bundle = Send(Class("NSBundle"u8), Sel("bundleWithURL:"u8), appUrl);
        var id = bundle == 0 ? 0 : Send(bundle, Sel("bundleIdentifier"u8));
        return id == 0 ? null : ReadString(id);
    }

    private static nint SchemeUrl(string scheme) => Send(Class("NSURL"u8), Sel("URLWithString:"u8), NSString(scheme + ":"));

    private static nint DocumentController() => Send(Class("NSDocumentController"u8), Sel("sharedDocumentController"u8));

    private static nint Workspace() => Send(Class("NSWorkspace"u8), Sel("sharedWorkspace"u8));

    private static nint App() => Send(Class("NSApplication"u8), Sel("sharedApplication"u8));

    private static IReadOnlyList<T> ReadArray<T>(nint array, Func<nint, T> read)
    {
        var items = new List<T>();
        if (array == 0) return items;
        for (nint i = 0, n = Send(array, Sel("count"u8)); i < n; i++) items.Add(read(Send(array, Sel("objectAtIndex:"u8), i)));
        return items;
    }

    private static nint NewClass(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_allocateClassPair(Class("NSObject"u8), p, 0);
    }

    private static void Add(nint cls, ReadOnlySpan<byte> selector, void* imp, ReadOnlySpan<byte> types)
    {
        fixed (byte* t = types) class_replaceMethod(cls, Sel(selector), imp, t);
    }

    private static string ReadString(nint value) => value == 0 ? "" : Marshal.PtrToStringUTF8(Send(value, Sel("UTF8String"u8))) ?? "";

    private static nint NSString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* p = bytes) return ((delegate* unmanaged<nint, nint, byte*, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithUTF8String:"u8), p);
    }

    private static nint CFString(string value)
    {
        fixed (char* p = value) return CFStringCreateWithCharacters(0, p, value.Length);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static nint Send(nint receiver, nint selector, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_msgSend)(receiver, selector, a, b);

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint object_getClass(nint obj);
    [DllImport(ObjC)] private static extern nint class_getName(nint cls);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern nint class_replaceMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport("/System/Library/Frameworks/Foundation.framework/Foundation")]
    private static extern nint NSSearchPathForDirectoriesInDomains(nint directory, nint domainMask, byte expandTilde);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
    [DllImport(CoreFoundation)] private static extern nint CFStringCreateWithCharacters(nint allocator, char* chars, nint length);
    [DllImport(CoreServices)] private static extern int LSSetDefaultHandlerForURLScheme(nint scheme, nint bundleId);
    [DllImport(CoreServices)] private static extern nint LSCopyDefaultApplicationURLForURL(nint url, uint roles, nint* error);
    [DllImport(Carbon)] private static extern int EnableSecureEventInput();
    [DllImport(Carbon)] private static extern int DisableSecureEventInput();
    [DllImport(Carbon)] private static extern nint TISCreateASCIICapableInputSourceList();
    [DllImport(Carbon)] private static extern int TSMSetDocumentProperty(nint document, uint tag, uint size, void* data);
    [DllImport(Carbon)] private static extern int TSMRemoveDocumentProperty(nint document, uint tag);
    [DllImport(LibC)] private static extern int sysctlbyname(byte* name, void* oldValue, nuint* oldLength, void* newValue, nuint newLength);
}
