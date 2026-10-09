using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>What Chromium's <c>BatteryLevelProviderMac</c> reads from the <c>IOPMPowerSource</c> registry entry.</summary>
/// <param name="HasService">Whether the service exists; Macs without a battery may not have it.</param>
/// <param name="PropertiesRead">Whether its properties could be read.</param>
/// <param name="BatteryInstalled"><c>BatteryInstalled</c>, null when missing or not a number.</param>
/// <param name="ExternalConnected"><c>ExternalConnected</c>, null when missing or not a number.</param>
/// <param name="HasCapacities">Both the current and the full charge capacity are numbers.</param>
/// <param name="HasVoltage"><c>Voltage</c> is a number.</param>
public readonly record struct MacBatteryReading(bool HasService, bool PropertiesRead, bool? BatteryInstalled, bool? ExternalConnected,
    bool HasCapacities, bool HasVoltage);

/// <summary>A session notification Electron's <c>MacLockMonitor</c> observes.</summary>
public enum MacSessionNotification
{
    /// <summary><c>com.apple.screenIsLocked</c>.</summary>
    ScreenLocked,

    /// <summary><c>com.apple.screenIsUnlocked</c>.</summary>
    ScreenUnlocked,

    /// <summary><c>NSWorkspaceSessionDidBecomeActiveNotification</c>.</summary>
    SessionDidBecomeActive,

    /// <summary><c>NSWorkspaceSessionDidResignActiveNotification</c>.</summary>
    SessionDidResignActive,
}

/// <summary>macOS power sources as Chromium's <c>base/power_monitor</c>, <c>ui/base/idle</c> and
/// <c>power_save_blocker_mac.cc</c> read them, and Electron's <c>MacLockMonitor</c>. Main thread unless noted.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacPower
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    /// <summary><c>kIOMessageCanSystemSleep</c>.</summary>
    public const uint CanSystemSleep = 0xE0000270;

    /// <summary><c>kIOMessageSystemWillSleep</c>.</summary>
    public const uint SystemWillSleep = 0xE0000280;

    /// <summary><c>kIOMessageSystemWillPowerOn</c>.</summary>
    public const uint SystemWillPowerOn = 0xE0000320;

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_libSystem = NativeLibrary.Load(LibSystem);

    private static Action<uint>? s_systemPower;
    private static uint s_rootPort;
    private static Action? s_powerSource;
    private static Action? s_thermal;
    private static Action? s_cpuPower;
    private static Action<MacSessionNotification>? s_session;
    private static nint s_observer;
    private static bool s_screenMonitorInstalled;
    private static volatile bool s_screensaverRunning;
    private static volatile bool s_screenLocked;

    /// <summary>Chromium's <c>PlatformInit</c> sleep and wake registration: <c>IORegisterForSystemPower</c> on the main run
    /// loop in the common modes. <paramref name="message"/> gets each message before it is allowed, as Chromium answers
    /// <c>kIOMessageCanSystemSleep</c> and <c>kIOMessageSystemWillSleep</c>; once per process.</summary>
    public static bool RegisterForSystemPower(Action<uint> message)
    {
        if (s_systemPower is not null) return true;
        s_systemPower = message;
        nint port;
        uint notifier;
        s_rootPort = IORegisterForSystemPower(0, &port, &OnSystemPower, &notifier);
        if (s_rootPort == 0) return false;
        CFRunLoopAddSource(CFRunLoopGetMain(), IONotificationPortGetRunLoopSource(port), CommonModes());
        return true;
    }

    [UnmanagedCallersOnly]
    private static void OnSystemPower(nint refcon, uint service, uint messageType, nint argument)
    {
        try { s_systemPower?.Invoke(messageType); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a power notification: {e}"); }
        // Unanswered, the system delays sleep for 30 seconds.
        if (messageType is CanSystemSleep or SystemWillSleep) IOAllowPowerChange(s_rootPort, argument);
    }

    /// <summary>Chromium's <c>BatteryLevelProviderMac::GetBatteryStateImpl</c> registry read. Any thread.</summary>
    public static MacBatteryReading ReadBattery()
    {
        var service = PowerSourceService();
        if (service == 0) return new(false, false, null, null, false, false);
        try
        {
            nint dict;
            if (IORegistryEntryCreateCFProperties(service, &dict, 0, 0) != 0 || dict == 0) return new(true, false, null, null, false, false);
            var pool = objc_autoreleasePoolPush();
            try
            {
                var installed = Number(dict, "BatteryInstalled");
                var external = Number(dict, "ExternalConnected");
                nint current, full;
                // Chromium's @available(macOS 27, *) branch: the raw capacities moved into BatteryData.
                if (OperatingSystem.IsMacOSVersionAtLeast(27))
                {
                    var data = Object(dict, "BatteryData", "NSDictionary");
                    (current, full) = data == 0 ? (0, 0) : (Number(data, "RemainingCapacity"), Number(data, "FullChargeCapacity"));
                }
                else (current, full) = (Number(dict, "AppleRawCurrentCapacity"), Number(dict, "AppleRawMaxCapacity"));
                var voltage = Number(dict, "Voltage");
                return new(true, true, installed == 0 ? null : BoolValue(installed), external == 0 ? null : BoolValue(external),
                    current != 0 && full != 0, voltage != 0);
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
                CFRelease(dict);
            }
        }
        finally { IOObjectRelease(service); }
    }

    /// <summary>Chromium's <c>IOPMPowerSourceSamplingEventSource</c>: <c>kIOGeneralInterest</c> notifications from
    /// <c>IOPMPowerSource</c> on the main queue. False when there is no such service (desktop Macs); once per process.</summary>
    public static bool WatchPowerSource(Action changed)
    {
        if (s_powerSource is not null) return true;
        var service = PowerSourceService();
        if (service == 0) return false;
        var port = IONotificationPortCreate(0);
        if (port == 0) return false;
        IONotificationPortSetDispatchQueue(port, MainQueue());
        s_powerSource = changed;
        uint notification;
        fixed (byte* interest = "IOGeneralInterest"u8)
            if (IOServiceAddInterestNotification(port, service, interest, &OnPowerSource, 0, &notification) != 0)
            {
                s_powerSource = null;
                return false;
            }
        return true;
    }

    [UnmanagedCallersOnly]
    private static void OnPowerSource(nint refcon, uint service, uint messageType, nint argument)
    {
        try { s_powerSource?.Invoke(); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a power source notification: {e}"); }
    }

    /// <summary><c>NSProcessInfo.processInfo.thermalState</c>: 0 nominal, 1 fair, 2 serious, 3 critical. Any thread.</summary>
    public static long ThermalState() => (long)Send(Send(Class("NSProcessInfo"u8), Sel("processInfo"u8)), Sel("thermalState"u8));

    /// <summary><c>NSProcessInfoThermalStateDidChangeNotification</c>, called on the thread that posts it; once per process.</summary>
    public static void ObserveThermalState(Action changed)
    {
        if (s_thermal is not null) return;
        s_thermal = changed;
        Observe(Send(Class("NSNotificationCenter"u8), Sel("defaultCenter"u8)), "thermalChanged:", "NSProcessInfoThermalStateDidChangeNotification");
    }

    /// <summary>Chromium's <c>GetCurrentSpeedLimit</c>: <c>kIOPMCPUPowerLimitProcessorSpeedKey</c> from
    /// <c>IOPMCopyCPUPowerStatus</c>, or null when it cannot be read. May block; any thread.</summary>
    public static int? CpuSpeedLimit()
    {
        nint dict;
        if (IOPMCopyCPUPowerStatus(&dict) != 0 || dict == 0) return null;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var key = NSString("CPU_Speed_Limit");
            var value = CFDictionaryGetValue(dict, key);
            int limit = -1;
            // kCFNumberIntType
            return value != 0 && CFGetTypeID(value) == CFNumberGetTypeID() && CFNumberGetValue(value, 9, &limit) != 0 ? limit : null;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
            CFRelease(dict);
        }
    }

    /// <summary>Chromium's <c>notify_register_dispatch</c> on <c>kIOPMCPUPowerNotificationKey</c>, on the main queue;
    /// false if it could not register. Once per process.</summary>
    public static bool ObserveCpuPower(Action changed)
    {
        if (s_cpuPower is not null) return true;
        s_cpuPower = changed;
        var block = (GlobalBlock*)NativeMemory.AllocZeroed((nuint)sizeof(GlobalBlock));
        var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        descriptor->Size = (nuint)sizeof(GlobalBlock);
        block->Isa = NativeLibrary.GetExport(s_libSystem, "_NSConcreteGlobalBlock");
        // BLOCK_IS_GLOBAL: never copied or freed, so it lives in unmanaged memory for the process.
        block->Flags = 1 << 28;
        block->Invoke = (delegate* unmanaged<GlobalBlock*, int, void>)&OnCpuPower;
        block->Descriptor = descriptor;
        int token;
        fixed (byte* name = "com.apple.system.power.CPU"u8)
            return notify_register_dispatch(name, &token, MainQueue(), block) == 0;
    }

    [UnmanagedCallersOnly]
    private static void OnCpuPower(GlobalBlock* block, int token)
    {
        try { s_cpuPower?.Invoke(); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a CPU power notification: {e}"); }
    }

    /// <summary>Electron's <c>MacLockMonitor</c>: screen lock and unlock (distributed notifications) and the session becoming
    /// active or inactive (NSWorkspace); once per process.</summary>
    public static void ObserveSession(Action<MacSessionNotification> notified)
    {
        if (s_session is not null) return;
        s_session = notified;
        var distributed = Send(Class("NSDistributedNotificationCenter"u8), Sel("defaultCenter"u8));
        Observe(distributed, "lockScreenLocked:", "com.apple.screenIsLocked");
        Observe(distributed, "lockScreenUnlocked:", "com.apple.screenIsUnlocked");
        var workspace = Send(Send(Class("NSWorkspace"u8), Sel("sharedWorkspace"u8)), Sel("notificationCenter"u8));
        Observe(workspace, "sessionBecameActive:", "NSWorkspaceSessionDidBecomeActiveNotification");
        Observe(workspace, "sessionResignedActive:", "NSWorkspaceSessionDidResignActiveNotification");
    }

    /// <summary>Chromium's <c>ui::InitIdleMonitor</c> (<c>MacScreenMonitor</c>): tracks the screen saver and the screen lock
    /// for <see cref="IdleLocked"/>; idempotent.</summary>
    public static void InstallScreenMonitor()
    {
        if (s_screenMonitorInstalled) return;
        s_screenMonitorInstalled = true;
        var distributed = Send(Class("NSDistributedNotificationCenter"u8), Sel("defaultCenter"u8));
        Observe(distributed, "screensaverStarted:", "com.apple.screensaver.didstart");
        Observe(distributed, "screensaverStopped:", "com.apple.screensaver.didstop");
        Observe(distributed, "idleScreenLocked:", "com.apple.screenIsLocked");
        Observe(distributed, "idleScreenUnlocked:", "com.apple.screenIsUnlocked");
    }

    /// <summary>Chromium's <c>CheckIdleStateIsLocked</c>: the screen saver runs or the screen is locked, as the screen
    /// monitor last heard; false before <see cref="InstallScreenMonitor"/>, as messaging a nil monitor.</summary>
    public static bool IdleLocked => s_screensaverRunning || s_screenLocked;

    /// <summary>Chromium's <c>CalculateIdleTime</c>: seconds since any input in the combined session state, truncated.
    /// Any thread.</summary>
    public static int IdleSeconds() => (int)CGEventSourceSecondsSinceLastEventType(0, uint.MaxValue);

    /// <summary><c>IOPMAssertionCreateWithName</c> at <c>kIOPMAssertionLevelOn</c>; the assertion id, or 0 (with
    /// Chromium's log line) when refused. Call off the main thread, as Chromium's power thread does.</summary>
    public static uint CreateAssertion(string type, string name)
    {
        uint id = 0;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var result = IOPMAssertionCreateWithName(NSString(type), 255, NSString(name), &id);
            if (result != 0) Console.Error.WriteLine($"[Oriel] IOPMAssertionCreate: {result}");
            return result == 0 ? id : 0;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary><c>IOPMAssertionRelease</c>, skipping the null assertion as Chromium's <c>RemoveBlock</c>.</summary>
    public static void ReleaseAssertion(uint id)
    {
        if (id == 0) return;
        var result = IOPMAssertionRelease(id);
        if (result != 0) Console.Error.WriteLine($"[Oriel] IOPMAssertionRelease: {result}");
    }

    /// <summary>The names of Oriel's assertions <c>IOPMCopyAssertionsByProcess</c> lists for this process with their type,
    /// as <c>pmset -g assertions</c> shows them; for tests.</summary>
    public static IReadOnlyList<(string Type, string Name)> OwnAssertions()
    {
        var result = new List<(string, string)>();
        nint byProcess;
        if (IOPMCopyAssertionsByProcess(&byProcess) != 0 || byProcess == 0) return result;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pid = Send(Send(Class("NSProcessInfo"u8), Sel("processInfo"u8)), Sel("processIdentifier"u8));
            var key = SendInt(Class("NSNumber"u8), Sel("numberWithInt:"u8), (int)pid);
            var list = Send(byProcess, Sel("objectForKey:"u8), key);
            var count = list == 0 ? 0 : (int)Send(list, Sel("count"u8));
            for (var i = 0; i < count; i++)
            {
                var entry = Send(list, Sel("objectAtIndex:"u8), i);
                result.Add((ReadString(Send(entry, Sel("objectForKey:"u8), NSString("AssertType"))),
                    ReadString(Send(entry, Sel("objectForKey:"u8), NSString("AssertName")))));
            }
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
            CFRelease(byProcess);
        }
        return result;
    }

    /// <summary>Posts a distributed notification, as the system does for the screen lock; for tests.</summary>
    public static void PostDistributed(string name)
        => ((delegate* unmanaged<nint, nint, nint, nint, nint, byte, void>)s_msgSend)(Send(Class("NSDistributedNotificationCenter"u8), Sel("defaultCenter"u8)),
            Sel("postNotificationName:object:userInfo:deliverImmediately:"u8), NSString(name), 0, 0, 1);

    private static void Observe(nint center, string selector, string name)
        => ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)s_msgSend)(center, Sel("addObserver:selector:name:object:"u8),
            Observer(), Sel(selector), NSString(name), 0);

    // One instance of a runtime NSObject subclass receives every notification.
    private static nint Observer()
    {
        if (s_observer != 0) return s_observer;
        fixed (byte* name = "OrielPowerObserver"u8)
        fixed (byte* types = "v@:@"u8)
        {
            var cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
            class_addMethod(cls, Sel("thermalChanged:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnThermal, types);
            class_addMethod(cls, Sel("lockScreenLocked:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnLockScreenLocked, types);
            class_addMethod(cls, Sel("lockScreenUnlocked:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnLockScreenUnlocked, types);
            class_addMethod(cls, Sel("sessionBecameActive:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnSessionActive, types);
            class_addMethod(cls, Sel("sessionResignedActive:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnSessionResigned, types);
            class_addMethod(cls, Sel("screensaverStarted:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnScreensaverStarted, types);
            class_addMethod(cls, Sel("screensaverStopped:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnScreensaverStopped, types);
            class_addMethod(cls, Sel("idleScreenLocked:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnIdleScreenLocked, types);
            class_addMethod(cls, Sel("idleScreenUnlocked:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnIdleScreenUnlocked, types);
            objc_registerClassPair(cls);
            s_observer = Send(Send(cls, Sel("alloc"u8)), Sel("init"u8));
        }
        return s_observer;
    }

    [UnmanagedCallersOnly]
    private static void OnThermal(nint self, nint cmd, nint notification) => Guard(s_thermal);

    [UnmanagedCallersOnly]
    private static void OnLockScreenLocked(nint self, nint cmd, nint notification) => Session(MacSessionNotification.ScreenLocked);

    [UnmanagedCallersOnly]
    private static void OnLockScreenUnlocked(nint self, nint cmd, nint notification) => Session(MacSessionNotification.ScreenUnlocked);

    [UnmanagedCallersOnly]
    private static void OnSessionActive(nint self, nint cmd, nint notification) => Session(MacSessionNotification.SessionDidBecomeActive);

    [UnmanagedCallersOnly]
    private static void OnSessionResigned(nint self, nint cmd, nint notification) => Session(MacSessionNotification.SessionDidResignActive);

    [UnmanagedCallersOnly]
    private static void OnScreensaverStarted(nint self, nint cmd, nint notification) => s_screensaverRunning = true;

    [UnmanagedCallersOnly]
    private static void OnScreensaverStopped(nint self, nint cmd, nint notification) => s_screensaverRunning = false;

    [UnmanagedCallersOnly]
    private static void OnIdleScreenLocked(nint self, nint cmd, nint notification) => s_screenLocked = true;

    [UnmanagedCallersOnly]
    private static void OnIdleScreenUnlocked(nint self, nint cmd, nint notification) => s_screenLocked = false;

    private static void Session(MacSessionNotification n)
    {
        try { s_session?.Invoke(n); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a session notification: {e}"); }
    }

    private static void Guard(Action? action)
    {
        try { action?.Invoke(); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a power notification: {e}"); }
    }

    // ObjCCast<NSNumber>: the value, or 0 when it is missing or not an NSNumber.
    private static nint Number(nint dict, string key) => Object(dict, key, "NSNumber");

    private static nint Object(nint dict, string key, string className)
    {
        var value = Send(dict, Sel("objectForKey:"u8), NSString(key));
        if (value == 0) return 0;
        var cls = Class(className);
        return ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(value, Sel("isKindOfClass:"u8), cls) != 0 ? value : 0;
    }

    private static bool BoolValue(nint number) => ((delegate* unmanaged<nint, nint, byte>)s_msgSend)(number, Sel("boolValue"u8)) != 0;

    // IOServiceGetMatchingService consumes the matching dictionary.
    private static uint PowerSourceService()
    {
        fixed (byte* p = "IOPMPowerSource"u8) return IOServiceGetMatchingService(0, IOServiceMatching(p));
    }

    private static nint MainQueue() => NativeLibrary.GetExport(s_libSystem, "_dispatch_main_q");

    private static nint CommonModes() => *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFRunLoopCommonModes");

    // Autoreleased; callers hold a pool or run inside the main run loop's.
    private static nint NSString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* p = bytes) return ((delegate* unmanaged<nint, nint, byte*, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithUTF8String:"u8), p);
    }

    private static string ReadString(nint nsString)
        => nsString == 0 ? string.Empty : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String"u8))) ?? string.Empty;

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Class(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    private static nint Sel(string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes) return sel_registerName(p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static nint SendInt(nint receiver, nint selector, int arg) => ((delegate* unmanaged<nint, nint, int, nint>)s_msgSend)(receiver, selector, arg);

    [StructLayout(LayoutKind.Sequential)]
    private struct GlobalBlock
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public delegate* unmanaged<GlobalBlock*, int, void> Invoke;
        public BlockDescriptor* Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport(ObjC)] private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjC)] private static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport(IOKit)] private static extern uint IORegisterForSystemPower(nint refcon, nint* port, delegate* unmanaged<nint, uint, uint, nint, void> callback, uint* notifier);
    [DllImport(IOKit)] private static extern int IOAllowPowerChange(uint kernelPort, nint notificationId);
    [DllImport(IOKit)] private static extern nint IONotificationPortGetRunLoopSource(nint port);
    [DllImport(IOKit)] private static extern nint IONotificationPortCreate(uint mainPort);
    [DllImport(IOKit)] private static extern void IONotificationPortSetDispatchQueue(nint port, nint queue);
    [DllImport(IOKit)] private static extern int IOServiceAddInterestNotification(nint port, uint service, byte* interestType, delegate* unmanaged<nint, uint, uint, nint, void> callback, nint refcon, uint* notification);
    [DllImport(IOKit)] private static extern nint IOServiceMatching(byte* name);
    [DllImport(IOKit)] private static extern uint IOServiceGetMatchingService(uint mainPort, nint matching);
    [DllImport(IOKit)] private static extern int IORegistryEntryCreateCFProperties(uint entry, nint* properties, nint allocator, uint options);
    [DllImport(IOKit)] private static extern int IOObjectRelease(uint obj);
    [DllImport(IOKit)] private static extern int IOPMAssertionCreateWithName(nint type, uint level, nint name, uint* id);
    [DllImport(IOKit)] private static extern int IOPMAssertionRelease(uint id);
    [DllImport(IOKit)] private static extern int IOPMCopyCPUPowerStatus(nint* status);
    [DllImport(IOKit)] private static extern int IOPMCopyAssertionsByProcess(nint* byProcess);

    [DllImport(CoreFoundation)] private static extern nint CFRunLoopGetMain();
    [DllImport(CoreFoundation)] private static extern void CFRunLoopAddSource(nint runLoop, nint source, nint mode);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint cf);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryGetValue(nint dict, nint key);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(nint cf);
    [DllImport(CoreFoundation)] private static extern nuint CFNumberGetTypeID();
    [DllImport(CoreFoundation)] private static extern byte CFNumberGetValue(nint number, nint type, void* value);

    [DllImport(CoreGraphics)] private static extern double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);

    [DllImport(LibSystem)] private static extern uint notify_register_dispatch(byte* name, int* token, nint queue, GlobalBlock* handler);
}
