using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>Electron v44's macOS <c>ShowCertificateTrust</c> (shell/browser/ui/certificate_trust_mac.mm): an
/// <c>SFCertificateTrustPanel</c> over a basic X.509 trust for the chain. Main thread.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacCertificateTrust
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly Lazy<nint> s_securityInterface = new(() => NativeLibrary.Load("/System/Library/Frameworks/SecurityInterface.framework/SecurityInterface"));
    private static readonly nint s_arrayCallBacks = NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeArrayCallBacks");
    // Each panel's delegate with what it keeps alive until the panel ends, as Electron's TrustDelegate.
    private static readonly Dictionary<nint, (nint Panel, nint Trust, nint Chain, nint Policy, Action Done)> s_open = [];

    /// <summary>Shows the panel as a sheet on <paramref name="contentView"/>'s window, or on its own without one, for the
    /// DER <paramref name="chain"/>, leaf first. <paramref name="done"/> runs when the user closes it.</summary>
    /// <exception cref="ArgumentException">Security refused a certificate.</exception>
    public static void Show(nint contentView, IReadOnlyList<byte[]> chain, string message, Action done)
    {
        _ = s_securityInterface.Value;
        var certificates = new nint[chain.Count];
        try
        {
            for (var i = 0; i < chain.Count; i++)
            {
                fixed (byte* p = chain[i])
                {
                    var data = CFDataCreate(0, p, chain[i].Length);
                    certificates[i] = SecCertificateCreateWithData(0, data);
                    CFRelease(data);
                }
                if (certificates[i] == 0) throw new ArgumentException("Security refused the certificate.", nameof(chain));
            }
            nint array;
            fixed (nint* values = certificates) array = CFArrayCreate(0, values, certificates.Length, s_arrayCallBacks);
            var policy = SecPolicyCreateBasicX509();
            nint trust = 0;
            SecTrustCreateWithCertificates(array, policy, &trust);
            var window = contentView == 0 ? 0 : Send(contentView, Sel("window"u8));
            var panel = Send(Send(Class("SFCertificateTrustPanel"u8), Sel("alloc"u8)), Sel("init"u8));
            var trustDelegate = Send(Send(DelegateClass(), Sel("alloc"u8)), Sel("init"u8));
            s_open[trustDelegate] = (panel, trust, array, policy, done);
            ((delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, nint, void>)s_msgSend)(panel,
                Sel("beginSheetForWindow:modalDelegate:didEndSelector:contextInfo:trust:message:"u8), window, trustDelegate,
                Sel("panelDidEnd:returnCode:contextInfo:"u8), 0, trust, NSString(message));
        }
        finally
        {
            // The array holds its own references.
            foreach (var certificate in certificates) if (certificate != 0) CFRelease(certificate);
        }
    }

    private static nint DelegateClass()
    {
        fixed (byte* name = "RiftwayTrustDelegate"u8)
        {
            var cls = objc_getClass(name);
            if (cls != 0) return cls;
            cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
            fixed (byte* types = "v@:@i^v"u8)
                class_addMethod(cls, Sel("panelDidEnd:returnCode:contextInfo:"u8), (delegate* unmanaged<nint, nint, nint, int, nint, void>)&OnPanelDidEnd, types);
            objc_registerClassPair(cls);
            return cls;
        }
    }

    // Electron also tells Chromium's CertDatabase the trust store changed, which CEF does not expose.
    [UnmanagedCallersOnly]
    private static void OnPanelDidEnd(nint self, nint cmd, nint sheet, int returnCode, nint contextInfo)
    {
        if (!s_open.Remove(self, out var open)) return;
        try { open.Done(); }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception after the certificate trust panel: {e}"); }
        finally
        {
            // Security can fail to make a trust, which Electron would release anyway.
            if (open.Trust != 0) CFRelease(open.Trust);
            CFRelease(open.Chain);
            CFRelease(open.Policy);
            // The panel is still on the stack calling this; autorelease outlives its return.
            Send(open.Panel, Sel("autorelease"u8));
            Send(self, Sel("release"u8));
        }
    }

    private static nint NSString(string s)
    {
        fixed (char* p = s)
            return ((delegate* unmanaged<nint, nint, char*, nuint, nint>)s_msgSend)(Class("NSString"u8), Sel("stringWithCharacters:length:"u8), p, (nuint)s.Length);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
    [DllImport(CoreFoundation)] private static extern nint CFDataCreate(nint allocator, byte* bytes, nint length);
    [DllImport(CoreFoundation)] private static extern nint CFArrayCreate(nint allocator, nint* values, nint count, nint callBacks);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint cf);
    [DllImport(Security)] private static extern nint SecCertificateCreateWithData(nint allocator, nint data);
    [DllImport(Security)] private static extern nint SecPolicyCreateBasicX509();
    [DllImport(Security)] private static extern int SecTrustCreateWithCertificates(nint certificates, nint policies, nint* trust);
}
