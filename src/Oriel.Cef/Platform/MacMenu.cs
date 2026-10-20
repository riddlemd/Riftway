using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>NSMenu and NSMenuItem through the Objective-C runtime: building the menu bar, and reading it back.</summary>
/// <remarks>Main thread only. Items without an AppKit action are sent to one target object, which reports
/// the item's tag through <see cref="Chosen"/>.</remarks>
[SupportedOSPlatform("macos")]
public static unsafe class MacMenu
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // NSEventModifierFlags.
    public const ulong ShiftKeyMask = 1 << 17;
    public const ulong ControlKeyMask = 1 << 18;
    public const ulong OptionKeyMask = 1 << 19;
    public const ulong CommandKeyMask = 1 << 20;

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    // NSMenu, NSMenuItem and NSSharingService are AppKit classes, unknown to the runtime until it loads.
    private static readonly nint s_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    private static nint s_target;
    private static nint s_chosenSelector;

    /// <summary>An item sent to the target was chosen; the argument is its tag.</summary>
    public static Action<nint>? Chosen { get; set; }

    /// <summary>A menu whose delegate is the target is opening (<c>menuWillOpen:</c>); the argument is the NSMenu.</summary>
    public static Action<nint>? Opened { get; set; }

    /// <summary>A menu whose delegate is the target closed (<c>menuDidClose:</c>); the argument is the NSMenu.</summary>
    public static Action<nint>? Closed { get; set; }

    /// <summary>AppKit asks a menu whose delegate is the target to update before it shows it or matches a key equivalent
    /// in it (<c>menuNeedsUpdate:</c>); the argument is the NSMenu.</summary>
    public static Action<nint>? NeedsUpdate { get; set; }

    /// <summary>The NSWindow a sharing service attaches its sheet to (<c>sharingService:sourceWindowForShareItems:sharingContentScope:</c>),
    /// or 0; Electron's is the focused window.</summary>
    public static Func<nint>? ShareSourceWindow { get; set; }

    /// <summary>Electron's <c>createShareMenuForItem:</c>: a new NSMenu (+1, owned by the caller) with an item per
    /// <c>NSSharingService</c> that can share the texts, files and URLs, each running its service when chosen; with
    /// nothing to share, <c>MakeEmptySubmenu</c>'s single disabled "(empty)" item.</summary>
    public static nint CreateShareMenu(IReadOnlyList<string> texts, IReadOnlyList<string> filePaths, IReadOnlyList<string> urls)
    {
        var pool = PushPool();
        try
        {
            var items = Send(Class("NSMutableArray"u8), Sel("array"u8));
            foreach (var text in texts) Send(items, Sel("addObject:"u8), Autorelease(NewString(text)));
            foreach (var path in filePaths) Send(items, Sel("addObject:"u8), Send(Class("NSURL"u8), Sel("fileURLWithPath:"u8), Autorelease(NewString(path))));
            foreach (var url in urls) Send(items, Sel("addObject:"u8), Send(Class("NSURL"u8), Sel("URLWithString:"u8), Autorelease(NewString(url))));
            var menu = CreateMenu(string.Empty);
            if (Send(items, Sel("count"u8)) == 0)
            {
                var empty = AddItem(menu, "(empty)", 0, string.Empty, 0, null);
                Send(empty, Sel("setAction:"u8), 0);
                Send(empty, Sel("setTarget:"u8), 0);
                SetEnabled(empty, false);
                return menu;
            }
            // +[NSSharingService sharingServicesForItems:] is deprecated, but its replacement shows a picker of its own.
            var services = Send(Class("NSSharingService"u8), Sel("sharingServicesForItems:"u8), items);
            for (nint i = 0, n = Send(services, Sel("count"u8)); i < n; i++)
            {
                var service = SendWithNInt(services, Sel("objectAtIndex:"u8), i);
                var item = Send(Send(Class("NSMenuItem"u8), Sel("alloc"u8)), Sel("initWithTitle:action:keyEquivalent:"u8),
                    Send(service, Sel("menuItemTitle"u8)), ShareSelector(), Autorelease(NewString(string.Empty)));
                Send(item, Sel("setTarget:"u8), Target());
                Send(item, Sel("setImage:"u8), Send(service, Sel("image"u8)));
                Send(item, Sel("setRepresentedObject:"u8), PairOf(service, items));
                Send(menu, Sel("addItem:"u8), item);
                Release(item);
            }
            SetDelegate(menu);
            return menu;
        }
        finally { PopPool(pool); }
    }

    /// <summary>Electron's <c>MenuMac::PopupOnUI</c> without a positioning item or frame: pops <paramref name="menu"/> up in
    /// <paramref name="contentView"/> at (<paramref name="x"/>, <paramref name="y"/>) from its top left, or at the pointer,
    /// placed by <see cref="PopupPosition"/>. Blocks in AppKit's menu loop until the menu closes.</summary>
    public static void PopUp(nint menu, nint contentView, int? x, int? y)
    {
        var window = Send(contentView, Sel("window"u8));
        var mouse = ((delegate* unmanaged<nint, nint, NSPoint>)s_msgSend)(window, Sel("mouseLocationOutsideOfEventStream"u8));
        var inView = ((delegate* unmanaged<nint, nint, NSPoint, nint, NSPoint>)s_msgSend)(contentView, Sel("convertPoint:fromView:"u8), mouse, 0);
        var size = ((delegate* unmanaged<nint, nint, NSSize>)s_msgSend)(menu, Sel("size"u8));
        var screen = Send(window, Sel("screen"u8));
        var visible = screen == 0 ? default : Rect(screen, Sel("visibleFrame"u8));
        var frame = Rect(window, Sel("frame"u8));
        var (px, py) = PopupPosition(x, y, (inView.X, inView.Y), Rect(contentView, Sel("frame"u8)).Height, (frame.X, frame.Y),
            (size.Width, size.Height), (visible.Y, visible.X + visible.Width));
        ((delegate* unmanaged<nint, nint, nint, NSPoint, nint, byte>)s_msgSend)(menu, Sel("popUpMenuPositioningItem:atLocation:inView:"u8),
            0, new NSPoint { X = px, Y = py }, contentView);
    }

    /// <summary><c>PopupOnUI</c>'s placement in view coordinates (origin bottom left): the given point from the top left,
    /// or the pointer; raised to clear the screen's visible bottom by 4 points if the menu would run below it, and
    /// moved left by the menu's width if it would run off the visible right.</summary>
    public static (double X, double Y) PopupPosition(int? x, int? y, (double X, double Y) mouseInView, double viewHeight,
        (double MinX, double MinY) windowFrame, (double Width, double Height) menuSize, (double MinY, double MaxX) screenVisible)
    {
        var (px, py) = x is { } ax && y is { } ay ? (ax, viewHeight - ay) : mouseInView;
        var distanceFromBottom = windowFrame.MinY + py - menuSize.Height - screenVisible.MinY;
        if (distanceFromBottom < 0) py = py - distanceFromBottom + 4;
        if (windowFrame.MinX + px + menuSize.Width > screenVisible.MaxX) px -= menuSize.Width;
        return (px, py);
    }

    /// <summary>Ends <paramref name="menu"/>'s tracking (<c>cancelTracking</c>).</summary>
    public static void CancelTracking(nint menu) => Send(menu, Sel("cancelTracking"u8));

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint
    {
        public double X, Y;
    }

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

    // A 32-byte struct comes back in registers on arm64, through objc_msgSend_stret on x86-64.
    private static NSRect Rect(nint receiver, nint selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return ((delegate* unmanaged<nint, nint, NSRect>)s_msgSend)(receiver, selector);
        NSRect rect;
        ((delegate* unmanaged<NSRect*, nint, nint, void>)s_msgSendStret.Value)(&rect, receiver, selector);
        return rect;
    }

    private static readonly Lazy<nint> s_msgSendStret = new(() => NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend_stret"));

    // The representedObject: [service, items], as Electron's @{@"service": service, @"items": items}.
    private static nint PairOf(nint service, nint items)
    {
        var objects = stackalloc nint[2] { service, items };
        return SendArray(Class("NSArray"u8), Sel("arrayWithObjects:count:"u8), objects, 2);
    }

    private static nint s_shareSelector;

    private static nint ShareSelector() => s_shareSelector != 0 ? s_shareSelector : s_shareSelector = Sel("orielPerformShare:"u8);

    /// <summary>The <c>NSSharingService</c> an item of <see cref="CreateShareMenu"/> runs, or 0; for tests.</summary>
    public static nint SharingServiceOf(nint item)
    {
        var pair = Send(item, Sel("representedObject"u8));
        return pair == 0 ? 0 : SendWithNInt(pair, Sel("objectAtIndex:"u8), 0);
    }

    /// <summary>The items an item of <see cref="CreateShareMenu"/> shares, as their descriptions; for tests.</summary>
    public static IReadOnlyList<string> SharedItemsOf(nint item)
    {
        var pair = Send(item, Sel("representedObject"u8));
        if (pair == 0) return [];
        var items = SendWithNInt(pair, Sel("objectAtIndex:"u8), 1);
        var result = new List<string>();
        for (nint i = 0, n = Send(items, Sel("count"u8)); i < n; i++) result.Add(ReadString(Send(SendWithNInt(items, Sel("objectAtIndex:"u8), i), Sel("description"u8))));
        return result;
    }

    /// <summary>Makes <paramref name="menu"/> AppKit's recent documents menu, which <c>NSDocumentController</c> fills: the
    /// name a nib's Open Recent menu carries, set through AppKit's private <c>_setMenuName:</c> as nib loading does;
    /// false where AppKit has no such method.</summary>
    public static bool MarkRecentDocumentsMenu(nint menu)
    {
        if (((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(menu, Sel("respondsToSelector:"u8), Sel("_setMenuName:"u8)) == 0) return false;
        var name = NewString("NSRecentDocumentsMenu");
        Send(menu, Sel("_setMenuName:"u8), name);
        Release(name);
        return true;
    }

    /// <summary>AppKit's private menu name; for tests.</summary>
    public static string MenuName(nint menu) => ReadString(Send(menu, Sel("_menuName"u8)));

    /// <summary>The menu's delegate, or 0; for tests.</summary>
    public static nint Delegate(nint menu) => Send(menu, Sel("delegate"u8));

    /// <summary>Makes the target the menu's delegate, for <see cref="Opened"/> and <see cref="Closed"/>. The delegate is
    /// a weak reference; the target lives for the process.</summary>
    public static void SetDelegate(nint menu) => Send(menu, Sel("setDelegate:"u8), Target());

    /// <summary>The menu that holds <paramref name="menu"/> as a submenu, or 0.</summary>
    public static nint Supermenu(nint menu) => Send(menu, Sel("supermenu"u8));

    /// <summary>Sends <paramref name="selector"/> (<c>menuNeedsUpdate:</c>, <c>menuWillOpen:</c>, <c>menuDidClose:</c>) with the menu to the menu's
    /// delegate, as AppKit does around tracking; false if it has none. For tests, as Electron's
    /// <c>SimulateSubmenuCloseSequenceForTesting</c>.</summary>
    public static bool SendToDelegate(nint menu, string selector)
    {
        var target = Send(menu, Sel("delegate"u8));
        if (target == 0) return false;
        Send(target, Sel(selector), menu);
        return true;
    }

    public static void RemoveItem(nint menu, nint item) => Send(menu, Sel("removeItem:"u8), item);

    /// <summary>Opens an autorelease pool for the objects AppKit hands back autoreleased; pass the result to <see cref="PopPool"/>.</summary>
    public static nint PushPool() => objc_autoreleasePoolPush();

    public static void PopPool(nint pool) => objc_autoreleasePoolPop(pool);

    /// <summary>A new NSMenu (+1, owned by the caller) that leaves enabling items to the caller.</summary>
    public static nint CreateMenu(string title)
    {
        var name = NewString(title);
        var menu = Send(Send(Class("NSMenu"u8), Sel("alloc"u8)), Sel("initWithTitle:"u8), name);
        Release(name);
        SendBool(menu, Sel("setAutoenablesItems:"u8), false);
        return menu;
    }

    /// <summary>Appends an item to <paramref name="menu"/> and returns it (owned by the menu).</summary>
    /// <param name="selector">An AppKit action sent along the responder chain, or null to send the item to
    /// the target and <see cref="Chosen"/>.</param>
    /// <param name="sectionHeader">Electron's <c>header</c>: AppKit's <c>sectionHeaderWithTitle:</c> where it exists
    /// (macOS 14 and later), else a normal item.</param>
    public static nint AddItem(nint menu, string title, nint tag, string keyEquivalent, ulong modifierMask, string? selector, bool sectionHeader = false)
    {
        var name = NewString(title);
        var key = NewString(keyEquivalent);
        var action = selector is null ? ChosenSelector() : Sel(selector);
        nint item;
        if (sectionHeader && RespondsTo(Class("NSMenuItem"u8), Sel("sectionHeaderWithTitle:"u8)))
        {
            // Autoreleased: retained here to match the alloc path's release below.
            item = Send(Send(Class("NSMenuItem"u8), Sel("sectionHeaderWithTitle:"u8), name), Sel("retain"u8));
            Send(item, Sel("setAction:"u8), action);
            Send(item, Sel("setKeyEquivalent:"u8), key);
        }
        else item = Send(Send(Class("NSMenuItem"u8), Sel("alloc"u8)), Sel("initWithTitle:action:keyEquivalent:"u8), name, action, key);
        Release(name);
        Release(key);
        if (selector is null) Send(item, Sel("setTarget:"u8), Target());
        SendNInt(item, Sel("setTag:"u8), tag);
        SendNUInt(item, Sel("setKeyEquivalentModifierMask:"u8), (nuint)modifierMask);
        Send(menu, Sel("addItem:"u8), item);
        Release(item);
        return item;
    }

    public static void AddSeparator(nint menu) => Send(menu, Sel("addItem:"u8), Send(Class("NSMenuItem"u8), Sel("separatorItem"u8)));

    /// <summary>Makes <paramref name="submenu"/> the item's submenu; the item retains it.</summary>
    public static void SetSubmenu(nint item, nint submenu) => Send(item, Sel("setSubmenu:"u8), submenu);

    public static void SetEnabled(nint item, bool enabled) => SendBool(item, Sel("setEnabled:"u8), enabled);

    public static void SetChecked(nint item, bool on) => SendNInt(item, Sel("setState:"u8), on ? 1 : 0);

    public static void SetHidden(nint item, bool hidden) => SendBool(item, Sel("setHidden:"u8), hidden);

    public static void SetTitle(nint item, string title)
    {
        var name = NewString(title);
        Send(item, Sel("setTitle:"u8), name);
        Release(name);
    }

    public static nint Tag(nint item) => Send(item, Sel("tag"u8));

    /// <summary>Electron's <c>CreateBadge</c> on the item (macOS 14 and later): text for <paramref name="kind"/> 0, else
    /// AppKit's alerts (1), updates (2) or new items (3) badge with <paramref name="count"/>; null clears it.</summary>
    public static void SetBadge(nint item, int? kind, long count, string? content)
    {
        if (!RespondsTo(item, Sel("setBadge:"u8))) return;
        nint badge = 0;
        var cls = Class("NSMenuItemBadge"u8);
        if (kind == 0 && content is not null)
        {
            var text = NewString(content);
            badge = Send(Send(Send(cls, Sel("alloc"u8)), Sel("initWithString:"u8), text), Sel("autorelease"u8));
            Release(text);
        }
        else if (kind is 1 or 2 or 3)
        {
            var selector = kind switch { 1 => Sel("alertsWithCount:"u8), 2 => Sel("updatesWithCount:"u8), _ => Sel("newItemsWithCount:"u8) };
            badge = ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(cls, selector, (nint)count);
        }
        Send(item, Sel("setBadge:"u8), badge);
    }

    /// <summary>Electron's <c>palette</c>: <c>NSMenuPresentationStylePalette</c> where it exists (macOS 14 and later).</summary>
    public static void SetPalette(nint menu)
    {
        if (RespondsTo(menu, Sel("setPresentationStyle:"u8))) SendNInt(menu, Sel("setPresentationStyle:"u8), 1);
    }

    private static bool RespondsTo(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(receiver, Sel("respondsToSelector:"u8), selector) != 0;

    /// <summary><c>setSubtitle:</c>, which only macOS 14.4 and later have.</summary>
    public static void SetSubtitle(nint item, string subtitle)
    {
        if (((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(item, Sel("respondsToSelector:"u8), Sel("setSubtitle:"u8)) == 0) return;
        var name = NewString(subtitle);
        Send(item, Sel("setSubtitle:"u8), name);
        Release(name);
    }

    public static void SetToolTip(nint item, string toolTip)
    {
        var name = NewString(toolTip);
        Send(item, Sel("setToolTip:"u8), name);
        Release(name);
    }

    /// <summary><c>setImage:</c>; the item retains the image.</summary>
    public static void SetImage(nint item, nint image) => Send(item, Sel("setImage:"u8), image);

    /// <summary>Clears the item's image, then sets its action to nil and back, as Electron's <c>applyStateToMenuItem</c>:
    /// AppKit restores a role item's system icon (macOS 26) only when its action changes.</summary>
    public static void ClearImage(nint item, string? roleSelector)
    {
        Send(item, Sel("setImage:"u8), 0);
        if (roleSelector is null) return;
        Send(item, Sel("setAction:"u8), 0);
        Send(item, Sel("setAction:"u8), Sel(roleSelector));
    }

    /// <summary>Replaces the menu bar; NSApp retains <paramref name="menu"/>, and 0 clears it.</summary>
    /// <summary>The modifier flags of the event AppKit is handling, and whether it is a key press.</summary>
    public static (ulong ModifierFlags, bool IsKeyDown) CurrentEvent()
    {
        var e = Send(App(), Sel("currentEvent"u8));
        if (e == 0) return (0, false);
        // NSEventTypeKeyDown
        return ((ulong)Send(e, Sel("modifierFlags"u8)), Send(e, Sel("type"u8)) == 10);
    }

    public static void SetMainMenu(nint menu) => Send(App(), Sel("setMainMenu:"u8), menu);

    public static void SetServicesMenu(nint menu) => Send(App(), Sel("setServicesMenu:"u8), menu);

    public static void SetWindowsMenu(nint menu) => Send(App(), Sel("setWindowsMenu:"u8), menu);

    public static void SetHelpMenu(nint menu) => Send(App(), Sel("setHelpMenu:"u8), menu);

    /// <summary>[obj autorelease]; returns obj.</summary>
    public static nint Autorelease(nint obj) => Send(obj, Sel("autorelease"u8));

    public static void Release(nint obj)
    {
        if (obj != 0) Send(obj, Sel("release"u8));
    }

    /// <summary>The menu bar, or 0.</summary>
    public static nint MainMenu() => Send(App(), Sel("mainMenu"u8));

    public static nint WindowsMenu() => Send(App(), Sel("windowsMenu"u8));

    public static int ItemCount(nint menu) => (int)Send(menu, Sel("numberOfItems"u8));

    public static nint ItemAt(nint menu, int index) => SendWithNInt(menu, Sel("itemAtIndex:"u8), index);

    public static string Title(nint item) => ReadString(Send(item, Sel("title"u8)));

    public static string KeyEquivalent(nint item) => ReadString(Send(item, Sel("keyEquivalent"u8)));

    public static ulong ModifierMask(nint item) => (ulong)Send(item, Sel("keyEquivalentModifierMask"u8));

    public static nint Submenu(nint item) => Send(item, Sel("submenu"u8));

    public static bool IsEnabled(nint item) => (byte)Send(item, Sel("isEnabled"u8)) != 0;

    public static bool IsChecked(nint item) => Send(item, Sel("state"u8)) == 1;

    public static bool IsHidden(nint item) => (byte)Send(item, Sel("isHidden"u8)) != 0;

    public static bool IsSeparator(nint item) => (byte)Send(item, Sel("isSeparatorItem"u8)) != 0;

    /// <summary>The item's action selector name, e.g. <c>copy:</c>.</summary>
    public static string ActionName(nint item)
    {
        var action = Send(item, Sel("action"u8));
        return action == 0 ? string.Empty : Marshal.PtrToStringUTF8((nint)sel_getName(action)) ?? string.Empty;
    }

    /// <summary>Chooses the item at <paramref name="index"/> as a click would, through AppKit's own dispatch.</summary>
    public static void PerformAction(nint menu, int index) => SendWithNInt(menu, Sel("performActionForItemAtIndex:"u8), index);

    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> s_mainThreadActions = new();

    /// <summary>Runs <paramref name="action"/> on the main thread even while a menu is open: AppKit's menu loop
    /// services only its tracking run-loop modes, where CEF's posted tasks wait until the menu closes.</summary>
    public static void PerformOnMainThread(Action action)
    {
        s_mainThreadActions.Enqueue(action);
        var pool = PushPool();
        try
        {
            ReadOnlySpan<string> names = ["kCFRunLoopCommonModes", "NSEventTrackingRunLoopMode", "NSModalPanelRunLoopMode"];
            var modes = stackalloc nint[names.Length];
            for (var i = 0; i < names.Length; i++) modes[i] = NewString(names[i]);
            var array = SendArray(Class("NSArray"u8), Sel("arrayWithObjects:count:"u8), modes, (nuint)names.Length);
            for (var i = 0; i < names.Length; i++) Release(modes[i]);
            SendPerform(Target(), Sel("performSelectorOnMainThread:withObject:waitUntilDone:modes:"u8), PerformSelector(), 0, 0, array);
        }
        finally { PopPool(pool); }
    }

    private static nint s_performSelector;

    private static nint PerformSelector() => s_performSelector != 0 ? s_performSelector : s_performSelector = Sel("orielPerform:"u8);

    [UnmanagedCallersOnly]
    private static void OnPerform(nint self, nint cmd, nint arg)
    {
        while (s_mainThreadActions.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a main-thread action: {e}"); }
        }
    }

    private static nint App() => Send(Class("NSApplication"u8), Sel("sharedApplication"u8));

    private static nint ChosenSelector() => s_chosenSelector != 0 ? s_chosenSelector : s_chosenSelector = Sel("orielMenuItemChosen:"u8);

    // One instance of a runtime-built NSObject subclass receives every non-AppKit item.
    private static nint Target()
    {
        if (s_target != 0) return s_target;
        // PerformOnMainThread can be the first caller, from any thread.
        lock (s_targetLock) return s_target != 0 ? s_target : s_target = NewTarget();
    }

    private static readonly Lock s_targetLock = new();

    private static nint NewTarget()
    {
        fixed (byte* name = "OrielMenuTarget"u8)
        fixed (byte* types = "v@:@"u8)
        {
            var cls = objc_getClass(name);
            if (cls != 0) return Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
            cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
            class_addMethod(cls, ChosenSelector(), (delegate* unmanaged<nint, nint, nint, void>)&OnChosen, types);
            class_addMethod(cls, PerformSelector(), (delegate* unmanaged<nint, nint, nint, void>)&OnPerform, types);
            class_addMethod(cls, Sel("menuWillOpen:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnMenuWillOpen, types);
            class_addMethod(cls, Sel("menuDidClose:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnMenuDidClose, types);
            class_addMethod(cls, Sel("menuNeedsUpdate:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnMenuNeedsUpdate, types);
            class_addMethod(cls, ShareSelector(), (delegate* unmanaged<nint, nint, nint, void>)&OnPerformShare, types);
            fixed (byte* windowTypes = "@@:@@^q"u8)
                class_addMethod(cls, Sel("sharingService:sourceWindowForShareItems:sharingContentScope:"u8),
                    (delegate* unmanaged<nint, nint, nint, nint, nint*, nint>)&OnSourceWindow, windowTypes);
            objc_registerClassPair(cls);
            return Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
        }
    }

    [UnmanagedCallersOnly]
    private static void OnChosen(nint self, nint cmd, nint sender)
    {
        var tag = Send(sender, Sel("tag"u8));
        try { Chosen?.Invoke(tag); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a menu action: {e}"); }
    }

    // Electron's performShare:.
    [UnmanagedCallersOnly]
    private static void OnPerformShare(nint self, nint cmd, nint sender)
    {
        var pair = Send(sender, Sel("representedObject"u8));
        if (pair == 0) return;
        var service = SendWithNInt(pair, Sel("objectAtIndex:"u8), 0);
        Send(service, Sel("setDelegate:"u8), self);
        Send(service, Sel("performWithItems:"u8), SendWithNInt(pair, Sel("objectAtIndex:"u8), 1));
    }

    [UnmanagedCallersOnly]
    private static nint OnSourceWindow(nint self, nint cmd, nint service, nint items, nint* scope)
    {
        try { return ShareSourceWindow?.Invoke() ?? 0; }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Oriel] Unhandled exception finding a share sheet's window: {e}");
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnMenuWillOpen(nint self, nint cmd, nint menu)
    {
        try { Opened?.Invoke(menu); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in menuWillOpen: {e}"); }
    }

    [UnmanagedCallersOnly]
    private static void OnMenuDidClose(nint self, nint cmd, nint menu)
    {
        try { Closed?.Invoke(menu); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in menuDidClose: {e}"); }
    }

    [UnmanagedCallersOnly]
    private static void OnMenuNeedsUpdate(nint self, nint cmd, nint menu)
    {
        try { NeedsUpdate?.Invoke(menu); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in menuNeedsUpdate: {e}"); }
    }

    private static nint NewString(string value)
    {
        fixed (char* chars = value)
            return SendChars(Send(Class("NSString"u8), Sel("alloc"u8)), Sel("initWithCharacters:length:"u8), chars, (nuint)value.Length);
    }

    private static string ReadString(nint nsString)
        => nsString == 0 ? string.Empty : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String"u8))) ?? string.Empty;

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    private static nint Sel(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + '\0');
        fixed (byte* p = bytes) return sel_registerName(p);
    }

    // objc_msgSend must be called through a pointer of the method's exact C signature.
    private static nint Send(nint receiver, nint selector)
        => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg)
        => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static nint Send(nint receiver, nint selector, nint a, nint b, nint c)
        => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)s_msgSend)(receiver, selector, a, b, c);

    private static nint SendWithNInt(nint receiver, nint selector, nint arg)
        => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static void SendBool(nint receiver, nint selector, bool value)
        => ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(receiver, selector, value ? (byte)1 : (byte)0);

    private static void SendNInt(nint receiver, nint selector, nint value)
        => ((delegate* unmanaged<nint, nint, nint, void>)s_msgSend)(receiver, selector, value);

    private static void SendNUInt(nint receiver, nint selector, nuint value)
        => ((delegate* unmanaged<nint, nint, nuint, void>)s_msgSend)(receiver, selector, value);

    private static nint SendArray(nint receiver, nint selector, nint* objects, nuint count)
        => ((delegate* unmanaged<nint, nint, nint*, nuint, nint>)s_msgSend)(receiver, selector, objects, count);

    private static void SendPerform(nint receiver, nint selector, nint action, nint arg, byte wait, nint modes)
        => ((delegate* unmanaged<nint, nint, nint, nint, byte, nint, void>)s_msgSend)(receiver, selector, action, arg, wait, modes);

    private static nint SendChars(nint receiver, nint selector, char* chars, nuint length)
        => ((delegate* unmanaged<nint, nint, char*, nuint, nint>)s_msgSend)(receiver, selector, chars, length);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern byte* sel_getName(nint selector);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);
}
