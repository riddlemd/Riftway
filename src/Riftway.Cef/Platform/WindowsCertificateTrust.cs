using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary><c>dialog.showCertificateTrustDialog</c> on Windows, as Electron's <c>certificate_trust_win.cc</c>: build the
/// certificate's chain, and when its only problem is an untrusted root, add it to the current user's Root store, which
/// makes Windows ask the user.</summary>
public static unsafe class WindowsCertificateTrust
{
    private const uint X509AsnEncoding = 0x1; // X509_ASN_ENCODING
    private const uint PkcsAsnEncoding = 0x10000; // PKCS_7_ASN_ENCODING
    private const nint StoreProvMemory = 2; // CERT_STORE_PROV_MEMORY
    private const nint StoreProvSystem = 10; // CERT_STORE_PROV_SYSTEM_W
    private const uint SystemStoreCurrentUser = 0x10000; // CERT_SYSTEM_STORE_CURRENT_USER
    private const uint AddAlways = 4; // CERT_STORE_ADD_ALWAYS
    private const uint AddReplaceExisting = 3; // CERT_STORE_ADD_REPLACE_EXISTING
    private const uint CloseForce = 1; // CERT_CLOSE_STORE_FORCE_FLAG
    private const uint UsageMatchAnd = 0; // USAGE_MATCH_TYPE_AND

    // Electron's comparison values. In dwErrorStatus 0x8 is CERT_TRUST_IS_NOT_SIGNATURE_VALID; Electron names it
    // CERT_TRUST_IS_SELF_SIGNED, the dwInfoStatus flag with the same value, and compares against it as is.
    private const uint TrustIsSelfSigned = 0x8; // CERT_TRUST_IS_SELF_SIGNED
    private const uint TrustIsUntrustedRoot = 0x20; // CERT_TRUST_IS_UNTRUSTED_ROOT

    [StructLayout(LayoutKind.Sequential)]
    private struct CertEnhKeyUsage
    {
        public uint UsageIdentifierCount;
        public nint UsageIdentifiers;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertUsageMatch
    {
        public uint Type;
        public CertEnhKeyUsage Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertChainPara
    {
        public uint Size;
        public CertUsageMatch RequestedUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertTrustStatus
    {
        public uint ErrorStatus;
        public uint InfoStatus;
    }

    // The head of CERT_CHAIN_CONTEXT, up to the field read here.
    [StructLayout(LayoutKind.Sequential)]
    private struct CertChainContext
    {
        public uint Size;
        public CertTrustStatus TrustStatus;
    }

    [DllImport("crypt32.dll")]
    private static extern nint CertOpenStore(nint provider, uint encoding, nint cryptProv, uint flags, char* parameter);

    [DllImport("crypt32.dll")]
    private static extern int CertCloseStore(nint store, uint flags);

    [DllImport("crypt32.dll")]
    private static extern int CertAddEncodedCertificateToStore(nint store, uint encoding, byte* encoded, uint length, uint disposition, nint* added);

    [DllImport("crypt32.dll")]
    private static extern int CertAddCertificateContextToStore(nint store, nint context, uint disposition, nint* added);

    [DllImport("crypt32.dll")]
    private static extern int CertFreeCertificateContext(nint context);

    [DllImport("crypt32.dll")]
    private static extern int CertGetCertificateChain(nint engine, nint context, nint time, nint additionalStore, CertChainPara* para, uint flags, nint reserved, CertChainContext** chain);

    [DllImport("crypt32.dll")]
    private static extern void CertFreeCertificateChain(CertChainContext* chain);

    /// <summary>Whether Electron adds a certificate whose chain has <paramref name="errorStatus"/>: only an untrusted
    /// root, or Electron's self-signed value, on its own.</summary>
    public static bool ShouldAdd(uint errorStatus) => errorStatus is TrustIsSelfSigned or TrustIsUntrustedRoot;

    /// <summary>The chain's <c>TrustStatus.dwErrorStatus</c> as Electron builds it: the leaf's context in a memory store
    /// that also holds the rest of <paramref name="chain"/> (Chromium's <c>CreateCertContextWithChain</c>), any usage
    /// over the whole chain, no revocation check. Null when the chain cannot be built.</summary>
    [SupportedOSPlatform("windows")]
    public static uint? ChainErrorStatus(IReadOnlyList<byte[]> chain)
    {
        var leaf = LeafContext(chain, out var memory);
        if (leaf == 0) return null;
        try
        {
            // Electron's GetCertificateChainParameters: an empty usage list, matched with AND across the chain.
            var para = new CertChainPara { Size = (uint)sizeof(CertChainPara), RequestedUsage = new CertUsageMatch { Type = UsageMatchAnd } };
            CertChainContext* context;
            if (CertGetCertificateChain(0, leaf, 0, 0, &para, 0, 0, &context) == 0) return null;
            try { return context->TrustStatus.ErrorStatus; }
            finally { CertFreeCertificateChain(context); }
        }
        finally
        {
            CertFreeCertificateContext(leaf);
            CertCloseStore(memory, CloseForce);
        }
    }

    /// <summary>Electron's <c>AddToTrustedRootStore</c>: adds <paramref name="der"/> to the current user's Root store,
    /// replacing an existing copy. Windows shows its security warning first; false when the user declines or the store
    /// cannot be opened.</summary>
    [SupportedOSPlatform("windows")]
    public static bool AddToTrustedRootStore(byte[] der)
    {
        var leaf = LeafContext([der], out var memory);
        if (leaf == 0) return false;
        try
        {
            nint root;
            fixed (char* name = "Root") root = CertOpenStore(StoreProvSystem, 0, 0, SystemStoreCurrentUser, name);
            if (root == 0) return false;
            try { return CertAddCertificateContextToStore(root, leaf, AddReplaceExisting, null) != 0; }
            finally { CertCloseStore(root, CloseForce); }
        }
        finally
        {
            CertFreeCertificateContext(leaf);
            CertCloseStore(memory, CloseForce);
        }
    }

    // A memory store holding every certificate of the chain, and the leaf's context in it (0 if any is not a certificate).
    private static nint LeafContext(IReadOnlyList<byte[]> chain, out nint memory)
    {
        memory = CertOpenStore(StoreProvMemory, 0, 0, 0, null);
        if (memory == 0) return 0;
        if (chain.Count == 0)
        {
            CertCloseStore(memory, CloseForce);
            memory = 0;
            return 0;
        }
        nint leaf = 0;
        for (var i = 0; i < chain.Count; i++)
        {
            nint added = 0;
            fixed (byte* der = chain[i])
            {
                if (CertAddEncodedCertificateToStore(memory, X509AsnEncoding | PkcsAsnEncoding, der, (uint)chain[i].Length, AddAlways, i == 0 ? &added : null) == 0)
                {
                    if (leaf != 0) CertFreeCertificateContext(leaf);
                    CertCloseStore(memory, CloseForce);
                    memory = 0;
                    return 0;
                }
            }
            if (i == 0) leaf = added;
        }
        return leaf;
    }
}
