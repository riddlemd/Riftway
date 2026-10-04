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
    private static nint s_target;
    private static nint s_chosenSelector;

    /// <summary>An item sent to the target was chosen; the argument is its tag.</summary>
    public static Action<nint>? Chosen { get; set; }

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
    public static nint AddItem(nint menu, string title, nint tag, string keyEquivalent, ulong modifierMask, string? selector)
    {
        var name = NewString(title);
        var key = NewString(keyEquivalent);
        var action = selector is null ? ChosenSelector() : Sel(selector);
        var item = Send(Send(Class("NSMenuItem"u8), Sel("alloc"u8)), Sel("initWithTitle:action:keyEquivalent:"u8), name, action, key);
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

    /// <summary>Replaces the menu bar; NSApp retains <paramref name="menu"/>, and 0 clears it.</summary>
    public static void SetMainMenu(nint menu) => Send(App(), Sel("setMainMenu:"u8), menu);

    public static void SetServicesMenu(nint menu) => Send(App(), Sel("setServicesMenu:"u8), menu);

    public static void SetWindowsMenu(nint menu) => Send(App(), Sel("setWindowsMenu:"u8), menu);

    public static void SetHelpMenu(nint menu) => Send(App(), Sel("setHelpMenu:"u8), menu);

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
        fixed (byte* name = "OrielMenuTarget"u8)
        fixed (byte* types = "v@:@"u8)
        {
            var cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
            class_addMethod(cls, ChosenSelector(), (delegate* unmanaged<nint, nint, nint, void>)&OnChosen, types);
            class_addMethod(cls, PerformSelector(), (delegate* unmanaged<nint, nint, nint, void>)&OnPerform, types);
            objc_registerClassPair(cls);
            s_target = Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
        }
        return s_target;
    }

    [UnmanagedCallersOnly]
    private static void OnChosen(nint self, nint cmd, nint sender)
    {
        var tag = Send(sender, Sel("tag"u8));
        try { Chosen?.Invoke(tag); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a menu action: {e}"); }
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
