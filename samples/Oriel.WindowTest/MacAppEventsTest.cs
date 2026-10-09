using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Oriel;
using Oriel.Cef;
using Oriel.Cef.Platform;

/// <summary>The app delegate's events on macOS, driven in-process: the Apple events LaunchServices would send (GURL,
/// odoc, rapp) dispatched through NSAppleEventManager, and activation through hide and focus.</summary>
[SupportedOSPlatform("macos")]
static unsafe class MacAppEventsTest
{
    private const uint GetUrl = 0x4755524C;
    private const uint CoreEvents = 0x61657674;
    private const uint OpenDocuments = 0x6F646F63;
    private const uint ReopenApplication = 0x72617070;
    private const uint DirectObject = 0x2D2D2D2D;
    private const uint CurrentProcess = 2;
    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var app = OrielApp.Current;
        var log = new List<string>();
        void Add(string entry)
        {
            lock (log) log.Add(entry);
        }
        bool Saw(string entry)
        {
            lock (log) return log.Contains(entry);
        }
        app.OpenUrl += (_, e) => Add("url:" + e.Url);
        app.OpenFile += (_, e) =>
        {
            Add("file:" + e.Path);
            e.Cancel = e.Path.EndsWith("handled.txt", StringComparison.Ordinal);
        };
        app.Activate += (_, e) => Add("activate:" + e.HasVisibleWindows);
        app.DidBecomeActive += (_, _) => Add("active");
        app.DidResignActive += (_, _) => Add("resign");

        CefThread.InvokeOnUi(() =>
        {
            Dispatch(Event(GetUrl, GetUrl, Text("oriel-test://open/path?q=1")));
            return 0;
        });
        check("open-url carries the GURL Apple event's URL", Eventually(() => Saw("url:oriel-test://open/path?q=1"), step));

        var file = Path.Combine(Path.GetTempPath(), $"oriel-open-{Guid.NewGuid():N}-handled.txt");
        File.WriteAllText(file, "x");
        try
        {
            CefThread.InvokeOnUi(() =>
        {
            Dispatch(Event(CoreEvents, OpenDocuments, FileList(file)));
            return 0;
        });
            check("open-file carries each file of an odoc Apple event", Eventually(() => Saw("file:" + file), step));
        }
        finally { File.Delete(file); }

        var visible = new BrowserWindow(new BrowserWindowOptions { Title = "Oriel window test: app events", Width = 200, Height = 120 });
        CefThread.InvokeOnUi(() =>
        {
            Dispatch(Event(CoreEvents, ReopenApplication, 0));
            return 0;
        });
        check("activate says whether windows are visible on a reopen Apple event", Eventually(() => Saw("activate:True"), step));

        app.Focus(new AppFocusOptions { Steal = true });
        Eventually(app.IsActive, step);
        app.Hide();
        check("did-resign-active fires when the app is hidden", Eventually(() => Saw("resign"), step));
        app.Show();
        app.Focus(new AppFocusOptions { Steal = true });
        check("did-become-active fires when it is focused again, and IsActive agrees", Eventually(() => Saw("active") && app.IsActive(), step));
        visible.Destroy();

        app.SetActivationPolicy(ActivationPolicy.Accessory);
        var accessory = Eventually(() => CefThread.InvokeOnUi(MacApp.ActivationPolicy) == 1, step);
        app.SetActivationPolicy(ActivationPolicy.Regular);
        check("SetActivationPolicy sets NSApp's policy", accessory && Eventually(() => CefThread.InvokeOnUi(MacApp.ActivationPolicy) == 0, step));

        app.SetSecureKeyboardEntryEnabled(true);
        var secure = app.IsSecureKeyboardEntryEnabled() && IsSecureEventInputEnabled() != 0;
        app.SetSecureKeyboardEntryEnabled(false);
        check("SetSecureKeyboardEntryEnabled turns secure event input on and off", secure && !app.IsSecureKeyboardEntryEnabled());

        app.SetAboutPanelOptions(new AboutPanelOptions { ApplicationName = "Oriel About", ApplicationVersion = "9.9", Credits = "credits" });
        app.ShowAboutPanel();
        check("ShowAboutPanel puts up AppKit's about panel", Eventually(() => CefThread.InvokeOnUi(VisiblePanel) != 0, step));
        CefThread.InvokeOnUi(() =>
        {
            if (VisiblePanel() is var panel and not 0) Send(panel, Sel("close"));
            return 0;
        });
    }

    private static bool Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    // [NSAppleEventDescriptor appleEventWithEventClass:eventID:targetDescriptor:(this process)...] with a direct object.
    private static nint Event(uint eventClass, uint eventId, nint directObject)
    {
        var psn = stackalloc uint[] { 0, CurrentProcess };
        var target = ((delegate* unmanaged<nint, nint, uint, void*, nint, nint>)s_msgSend)(Class("NSAppleEventDescriptor"), Sel("descriptorWithDescriptorType:bytes:length:"),
            0x70736E20 /* typeProcessSerialNumber 'psn ' */, psn, 8);
        var evt = ((delegate* unmanaged<nint, nint, uint, uint, nint, short, int, nint>)s_msgSend)(Class("NSAppleEventDescriptor"),
            Sel("appleEventWithEventClass:eventID:targetDescriptor:returnID:transactionID:"), eventClass, eventId, target, -1, 0);
        if (directObject != 0) ((delegate* unmanaged<nint, nint, nint, uint, void>)s_msgSend)(evt, Sel("setParamDescriptor:forKeyword:"), directObject, DirectObject);
        return evt;
    }

    private static nint Text(string value) => Send(Class("NSAppleEventDescriptor"), Sel("descriptorWithString:"), NSString(value));

    // An AE list of one file URL, as Finder sends.
    private static nint FileList(string path)
    {
        var list = Send(Class("NSAppleEventDescriptor"), Sel("listDescriptor"));
        var url = Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(path));
        var urlDescriptor = Send(Class("NSAppleEventDescriptor"), Sel("descriptorWithFileURL:"), url);
        ((delegate* unmanaged<nint, nint, nint, nint, void>)s_msgSend)(list, Sel("insertDescriptor:atIndex:"), urlDescriptor, 0);
        return list;
    }

    private static void Dispatch(nint appleEvent)
    {
        var manager = Send(Class("NSAppleEventManager"), Sel("sharedAppleEventManager"));
        var desc = Send(appleEvent, Sel("aeDesc"));
        var reply = stackalloc byte[16];
        ((delegate* unmanaged<nint, nint, nint, void*, nint, short>)s_msgSend)(manager, Sel("dispatchRawAppleEvent:withRawReply:handlerRefCon:"), desc, reply, 0);
    }

    // The about panel is the only NSPanel the run shows.
    private static nint VisiblePanel()
    {
        var windows = Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("windows"));
        for (nint i = 0, n = Send(windows, Sel("count")); i < n; i++)
        {
            var window = Send(windows, Sel("objectAtIndex:"), i);
            var content = Marshal.PtrToStringUTF8(Send(Send(window, Sel("description")), Sel("UTF8String"))) ?? "";
            if (content.Contains("NSPanel", StringComparison.Ordinal) && Send(window, Sel("isVisible")) != 0) return window;
        }
        return 0;
    }

    private static nint NSString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* p = bytes) return ((delegate* unmanaged<nint, nint, byte*, nint>)s_msgSend)(Class("NSString"), Sel("stringWithUTF8String:"), p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static nint Class(string name) => objc_getClass(name);

    private static nint Sel(string name) => sel_registerName(name);

    [DllImport("/usr/lib/libobjc.A.dylib", CharSet = CharSet.Ansi)] private static extern nint objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", CharSet = CharSet.Ansi)] private static extern nint sel_registerName(string name);
    [DllImport("/System/Library/Frameworks/Carbon.framework/Carbon")] private static extern byte IsSecureEventInputEnabled();
}
