using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>What <see cref="MacNotifications.Add"/> delivers: Electron's <c>NotificationOptions</c> as
/// <c>CocoaNotification::Show</c> reads them.</summary>
public sealed record MacNotificationContent(string Id, string Title, string Subtitle, string Body, string ThreadId, bool Silent,
    string Sound, bool HasReply, string ReplyPlaceholder, IReadOnlyList<string> ActionTexts, string CloseButtonText, string? AttachmentPngPath);

/// <summary>A notification in Notification Center: Electron's <c>NotificationInfo</c>.</summary>
public sealed record MacDeliveredNotification(string Id, string Title, string Subtitle, string Body, string GroupId);

/// <summary>What the user did with a notification, as <c>NotificationCenterDelegate</c> tells it apart.</summary>
public enum MacNotificationResponseKind
{
    /// <summary>Nothing Electron reports.</summary>
    None,

    /// <summary>The body was clicked.</summary>
    Click,

    /// <summary>Dismissed, or its close button used.</summary>
    Dismiss,

    /// <summary>A reply was sent.</summary>
    Reply,

    /// <summary>An action button was used.</summary>
    Action,
}

/// <summary>A parsed <c>UNNotificationResponse</c>.</summary>
public readonly record struct MacNotificationResponse(MacNotificationResponseKind Kind, int ActionIndex = 0, string Reply = "");

/// <summary>Electron's macOS notifications over <c>UNUserNotificationCenter</c> (notification_presenter_mac.mm,
/// cocoa_notification.mm, notification_center_delegate.mm). The center needs a bundle identifier: without one AppKit
/// raises, so callers check <see cref="HasBundleIdentifier"/> first.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacNotifications
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string UserNotifications = "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";
    // IDS_MAC_NOTIFICATION_INLINE_REPLY_BUTTON and IDS_NOTIFICATION_SHOW_BUTTON in en-US.
    internal const string ReplyTitle = "Reply";
    internal const string ShowTitle = "Show";
    // UNNotificationPresentationOptionList | Banner | Sound.
    private const nuint ForegroundPresentation = (1 << 3) | (1 << 4) | (1 << 1);
    // UNAuthorizationOptionAlert | Sound | Badge.
    private const nuint AuthorizationOptions = 4 | 2 | 1;
    private const nuint CategoryOptionCustomDismissAction = 1;

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_libSystem = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
    private static readonly Lazy<nint> s_framework = new(() => NativeLibrary.Load(UserNotifications));
    private static nint s_delegate;
    // Notifications can be made on any thread; the delegate and the authorization request happen once.
    private static readonly Lock s_prepareLock = new();

    /// <summary>The user answered a notification: its identifier and what was done. Called on the center's thread.</summary>
    public static Action<string, MacNotificationResponse>? Responded { get; set; }

    /// <summary>Whether the main bundle has an identifier, without which <c>UNUserNotificationCenter</c> raises.</summary>
    public static bool HasBundleIdentifier()
    {
        var pool = objc_autoreleasePoolPush();
        try { return Send(Send(Class("NSBundle"u8), Sel("mainBundle"u8)), Sel("bundleIdentifier"u8)) != 0; }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>The main bundle's identifier, or null; Chromium's <c>base::apple::BaseBundleID()</c> once Electron sets it.</summary>
    public static string? BundleIdentifier()
    {
        var pool = objc_autoreleasePoolPush();
        try { return Send(Send(Class("NSBundle"u8), Sel("mainBundle"u8)), Sel("bundleIdentifier"u8)) is var id and not 0 ? ReadString(id) : null; }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>The <c>NotificationPresenterMac</c> constructor: becomes the center's delegate and asks for alert, sound
    /// and badge authorization, once per process.</summary>
    public static void Prepare()
    {
        lock (s_prepareLock) PrepareLocked();
    }

    private static void PrepareLocked()
    {
        if (s_delegate != 0) return;
        _ = s_framework.Value;
        var pool = objc_autoreleasePoolPush();
        try
        {
            s_delegate = NewDelegate();
            var center = Center();
            Send(center, Sel("setDelegate:"u8), s_delegate);
            ((delegate* unmanaged<nint, nint, nuint, Block*, void>)s_msgSend)(center, Sel("requestAuthorizationWithOptions:completionHandler:"u8),
                AuthorizationOptions, NewBlock((_, _) => { }));
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary><c>CocoaNotification::Show</c> and <c>ScheduleNotification</c>: builds the content, registers its action
    /// category, and adds the request. <paramref name="done"/> gets null once it is delivered, or the error's description;
    /// on the center's thread.</summary>
    public static void Add(MacNotificationContent c, Action<string?> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        Prepare();
        var pool = objc_autoreleasePoolPush();
        try
        {
            var center = Center();
            var content = Send(Send(Class("UNMutableNotificationContent"u8), Sel("alloc"u8)), Sel("init"u8));
            Send(content, Sel("setTitle:"u8), NSString(c.Title));
            Send(content, Sel("setSubtitle:"u8), NSString(c.Subtitle));
            Send(content, Sel("setBody:"u8), NSString(c.Body));
            if (c.ThreadId.Length > 0) Send(content, Sel("setThreadIdentifier:"u8), NSString(c.ThreadId));
            Send(content, Sel("setSound:"u8), c.Silent ? 0
                : c.Sound.Length == 0 ? Send(Class("UNNotificationSound"u8), Sel("defaultSound"u8))
                : Send(Class("UNNotificationSound"u8), Sel("soundNamed:"u8), NSString(c.Sound)));

            if (c.HasReply || c.ActionTexts.Count > 0 || c.CloseButtonText.Length > 0)
            {
                var actions = Send(Class("NSMutableArray"u8), Sel("array"u8));
                if (c.HasReply)
                {
                    var title = NSString(ReplyTitle);
                    var reply = ((delegate* unmanaged<nint, nint, nint, nint, nuint, nint, nint, nint>)s_msgSend)(Class("UNTextInputNotificationAction"u8),
                        Sel("actionWithIdentifier:title:options:textInputButtonTitle:textInputPlaceholder:"u8),
                        NSString("REPLY_ACTION"), title, 0, title, NSString(c.ReplyPlaceholder));
                    Send(actions, Sel("addObject:"u8), reply);
                }
                for (var i = 0; i < c.ActionTexts.Count; i++)
                    Send(actions, Sel("addObject:"u8), Action($"ACTION_{i}", c.ActionTexts[i].Length == 0 ? ShowTitle : c.ActionTexts[i]));
                if (c.CloseButtonText.Length > 0) Send(actions, Sel("addObject:"u8), Action("CLOSE_ACTION", c.CloseButtonText));

                var categoryId = NSString(CategoryIdentifier(ActionString(c.HasReply, c.ReplyPlaceholder, c.ActionTexts, c.CloseButtonText)));
                var category = ((delegate* unmanaged<nint, nint, nint, nint, nint, nuint, nint>)s_msgSend)(Class("UNNotificationCategory"u8),
                    Sel("categoryWithIdentifier:actions:intentIdentifiers:options:"u8), categoryId, actions,
                    Send(Class("NSArray"u8), Sel("array"u8)), CategoryOptionCustomDismissAction);
                Send(category, Sel("retain"u8));
                ((delegate* unmanaged<nint, nint, Block*, void>)s_msgSend)(center, Sel("getNotificationCategoriesWithCompletionHandler:"u8), NewBlock((existing, _) =>
                {
                    var p = objc_autoreleasePoolPush();
                    try
                    {
                        if (((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(existing, Sel("containsObject:"u8), category) == 0)
                        {
                            var updated = Send(existing, Sel("mutableCopy"u8));
                            Send(updated, Sel("addObject:"u8), category);
                            Send(center, Sel("setNotificationCategories:"u8), updated);
                            Send(updated, Sel("release"u8));
                        }
                    }
                    finally
                    {
                        objc_autoreleasePoolPop(p);
                        Send(category, Sel("release"u8));
                    }
                }));
                Send(content, Sel("setCategoryIdentifier:"u8), categoryId);
            }

            if (c.AttachmentPngPath is { } path && Attachment(path) is var attachment and not 0)
                Send(content, Sel("setAttachments:"u8), Send(Class("NSArray"u8), Sel("arrayWithObject:"u8), attachment));

            var request = ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)s_msgSend)(Class("UNNotificationRequest"u8),
                Sel("requestWithIdentifier:content:trigger:"u8), NSString(c.Id), content, 0);
            Send(content, Sel("release"u8));
            ((delegate* unmanaged<nint, nint, nint, Block*, void>)s_msgSend)(center, Sel("addNotificationRequest:withCompletionHandler:"u8), request,
                NewBlock((error, _) => done(error == 0 ? null : ReadString(Send(error, Sel("localizedDescription"u8))))));
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary><c>removeDeliveredNotificationsWithIdentifiers:</c>.</summary>
    public static void RemoveDelivered(IReadOnlyList<string> identifiers)
    {
        _ = s_framework.Value;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var ids = Send(Class("NSMutableArray"u8), Sel("array"u8));
            foreach (var id in identifiers) Send(ids, Sel("addObject:"u8), NSString(id));
            Send(Center(), Sel("removeDeliveredNotificationsWithIdentifiers:"u8), ids);
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary><c>removeAllDeliveredNotifications</c>.</summary>
    public static void RemoveAllDelivered()
    {
        _ = s_framework.Value;
        var pool = objc_autoreleasePoolPush();
        try { Send(Center(), Sel("removeAllDeliveredNotifications"u8)); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary><c>getDeliveredNotificationsWithCompletionHandler:</c>; <paramref name="done"/> runs on the center's thread.</summary>
    public static void GetDelivered(Action<IReadOnlyList<MacDeliveredNotification>> done)
    {
        ArgumentNullException.ThrowIfNull(done);
        _ = s_framework.Value;
        var pool = objc_autoreleasePoolPush();
        try
        {
            ((delegate* unmanaged<nint, nint, Block*, void>)s_msgSend)(Center(), Sel("getDeliveredNotificationsWithCompletionHandler:"u8), NewBlock((list, _) =>
            {
                var result = new List<MacDeliveredNotification>();
                var p = objc_autoreleasePoolPush();
                try
                {
                    for (nint i = 0, n = list == 0 ? 0 : Send(list, Sel("count"u8)); i < n; i++)
                    {
                        var request = Send(SendWithIndex(list, Sel("objectAtIndex:"u8), i), Sel("request"u8));
                        var content = Send(request, Sel("content"u8));
                        result.Add(new MacDeliveredNotification(ReadString(Send(request, Sel("identifier"u8))), ReadString(Send(content, Sel("title"u8))),
                            ReadString(Send(content, Sel("subtitle"u8))), ReadString(Send(content, Sel("body"u8))), ReadString(Send(content, Sel("threadIdentifier"u8)))));
                    }
                }
                finally { objc_autoreleasePoolPop(p); }
                done(result);
            }));
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>The string <c>CocoaNotification::Show</c> builds from the actions, whose hash names their category.</summary>
    public static string ActionString(bool hasReply, string replyPlaceholder, IReadOnlyList<string> actionTexts, string closeButtonText)
    {
        var s = new System.Text.StringBuilder();
        if (hasReply) s.Append("REPLY_").Append(ReplyTitle).Append('_').Append(replyPlaceholder);
        for (var i = 0; i < actionTexts.Count; i++) s.Append("ACTION_").Append(i).Append('_').Append(actionTexts[i].Length == 0 ? ShowTitle : actionTexts[i]);
        if (closeButtonText.Length > 0) s.Append("CLOSE_").Append(closeButtonText);
        return s.ToString();
    }

    /// <summary><c>CATEGORY_%lu</c> of the action string's <c>-[NSString hash]</c>.</summary>
    public static string CategoryIdentifier(string actionString)
    {
        var pool = objc_autoreleasePoolPush();
        try { return "CATEGORY_" + ((nuint)Send(NSString(actionString), Sel("hash"u8))).ToString(System.Globalization.CultureInfo.InvariantCulture); }
        finally { objc_autoreleasePoolPop(pool); }
    }

    private static readonly MacNotificationResponse None = new(MacNotificationResponseKind.None);

    /// <summary><c>didReceiveNotificationResponse:</c>'s dispatch on the action identifier.</summary>
    public static MacNotificationResponse ParseResponse(string actionIdentifier, string categoryIdentifier, bool isTextInput, string userText)
    {
        if (actionIdentifier == "com.apple.UNNotificationDefaultActionIdentifier") return new(MacNotificationResponseKind.Click);
        if (actionIdentifier == "com.apple.UNNotificationDismissActionIdentifier") return new(MacNotificationResponseKind.Dismiss);
        if (!categoryIdentifier.StartsWith("CATEGORY_", StringComparison.Ordinal)) return None;
        if (actionIdentifier == "REPLY_ACTION") return isTextInput ? new(MacNotificationResponseKind.Reply, Reply: userText) : None;
        if (actionIdentifier.StartsWith("ACTION_", StringComparison.Ordinal)) return new(MacNotificationResponseKind.Action, IntegerValue(actionIdentifier["ACTION_".Length..]));
        if (actionIdentifier == "CLOSE_ACTION") return new(MacNotificationResponseKind.Dismiss);
        return None;
    }

    // -[NSString integerValue] (leading whitespace, a sign, digits, saturating at NSIntegerMax/Min; 0 without digits)
    // truncated to int, as Electron's static_cast does.
    internal static int IntegerValue(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        var negative = i < s.Length && s[i] == '-';
        if (i < s.Length && s[i] is '-' or '+') i++;
        Int128 value = 0;
        for (; i < s.Length && char.IsAsciiDigit(s[i]); i++)
            value = Int128.Min(value * 10 + (s[i] - '0'), (Int128)long.MaxValue + 1);
        if (negative) value = -value;
        var saturated = (long)Int128.Clamp(value, long.MinValue, long.MaxValue);
        return unchecked((int)saturated);
    }

    private static nint Action(string identifier, string title)
        => ((delegate* unmanaged<nint, nint, nint, nint, nuint, nint>)s_msgSend)(Class("UNNotificationAction"u8),
            Sel("actionWithIdentifier:title:options:"u8), NSString(identifier), NSString(title), 0);

    // A PNG attachment; nil when UserNotifications refuses the file.
    private static nint Attachment(string path)
    {
        var key = *(nint*)NativeLibrary.GetExport(s_framework.Value, "UNNotificationAttachmentOptionsTypeHintKey");
        var options = Send(Class("NSDictionary"u8), Sel("dictionaryWithObject:forKey:"u8), NSString("public.png"), key);
        var uuid = Send(Send(Class("NSUUID"u8), Sel("UUID"u8)), Sel("UUIDString"u8));
        nint error = 0;
        var attachment = ((delegate* unmanaged<nint, nint, nint, nint, nint, nint*, nint>)s_msgSend)(Class("UNNotificationAttachment"u8),
            Sel("attachmentWithIdentifier:URL:options:error:"u8), uuid, Send(Class("NSURL"u8), Sel("fileURLWithPath:"u8), NSString(path)), options, &error);
        return error != 0 ? 0 : attachment;
    }

    private static nint Center() => Send(Class("UNUserNotificationCenter"u8), Sel("currentNotificationCenter"u8));

    private static nint NewDelegate()
    {
        fixed (byte* name = "OrielNotificationCenterDelegate"u8)
        {
            var cls = objc_getClass(name);
            if (cls == 0)
            {
                cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
                fixed (byte* types = "v@:@@@?"u8)
                {
                    class_addMethod(cls, Sel("userNotificationCenter:willPresentNotification:withCompletionHandler:"u8),
                        (delegate* unmanaged<nint, nint, nint, nint, Block*, void>)&OnWillPresent, types);
                    class_addMethod(cls, Sel("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"u8),
                        (delegate* unmanaged<nint, nint, nint, nint, Block*, void>)&OnResponse, types);
                }
                objc_registerClassPair(cls);
            }
            // The center holds its delegate weakly; this one lives for the process.
            return Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
        }
    }

    // Foreground notifications show as in the background.
    [UnmanagedCallersOnly]
    private static void OnWillPresent(nint self, nint cmd, nint center, nint notification, Block* completion)
        => ((delegate* unmanaged<Block*, nuint, void>)completion->Invoke)(completion, ForegroundPresentation);

    [UnmanagedCallersOnly]
    private static void OnResponse(nint self, nint cmd, nint center, nint response, Block* completion)
    {
        try
        {
            var request = Send(Send(response, Sel("notification"u8)), Sel("request"u8));
            var isTextInput = ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(response, Sel("isKindOfClass:"u8), Class("UNTextInputNotificationResponse"u8)) != 0;
            var parsed = ParseResponse(ReadString(Send(response, Sel("actionIdentifier"u8))),
                ReadString(Send(Send(request, Sel("content"u8)), Sel("categoryIdentifier"u8))), isTextInput,
                isTextInput ? ReadString(Send(response, Sel("userText"u8))) : "");
            Responded?.Invoke(ReadString(Send(request, Sel("identifier"u8))), parsed);
        }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a notification response: {e}"); }
        ((delegate* unmanaged<Block*, void>)completion->Invoke)(completion);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Block
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public void* Invoke;
        public BlockDescriptor* Descriptor;
        public nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    // A one-shot global-class block carrying a GCHandle (Block_copy hands back the same memory); its two pointer-sized
    // arguments cover every completion handler here, a BOOL arriving in the low byte of the first.
    private static Block* NewBlock(Action<nint, nint> callback)
    {
        var block = (Block*)NativeMemory.AllocZeroed((nuint)(sizeof(Block) + sizeof(BlockDescriptor)));
        var descriptor = (BlockDescriptor*)(block + 1);
        descriptor->Size = (nuint)sizeof(Block);
        block->Isa = NativeLibrary.GetExport(s_libSystem, "_NSConcreteGlobalBlock");
        // BLOCK_IS_GLOBAL.
        block->Flags = 1 << 28;
        block->Invoke = (delegate* unmanaged<Block*, nint, nint, void>)&OnBlock;
        block->Descriptor = descriptor;
        block->Context = GCHandle.ToIntPtr(GCHandle.Alloc(callback));
        return block;
    }

    [UnmanagedCallersOnly]
    private static void OnBlock(Block* block, nint a, nint b)
    {
        var handle = GCHandle.FromIntPtr(block->Context);
        var callback = (Action<nint, nint>)handle.Target!;
        handle.Free();
        try { callback(a, b); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a notification callback: {e}"); }
        // Never freed: a copy of a global block is the block itself, so whoever holds it may read its flags at any time.
    }

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

    private static nint Send(nint receiver, nint selector, nint a, nint b) => ((delegate* unmanaged<nint, nint, nint, nint, nint>)s_msgSend)(receiver, selector, a, b);

    private static nint SendWithIndex(nint receiver, nint selector, nint index) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, index);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
}
