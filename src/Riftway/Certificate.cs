using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Riftway;

/// <summary>Who issued or holds a certificate; Electron's <c>CertificatePrincipal</c>.</summary>
public sealed record CertificatePrincipal
{
    /// <summary>The common name.</summary>
    public string CommonName { get; init; } = "";

    /// <summary>The organizations.</summary>
    public IReadOnlyList<string> Organizations { get; init; } = [];

    /// <summary>The organization units.</summary>
    public IReadOnlyList<string> OrganizationUnits { get; init; } = [];

    /// <summary>The locality.</summary>
    public string Locality { get; init; } = "";

    /// <summary>The state or province.</summary>
    public string State { get; init; } = "";

    /// <summary>The country or region.</summary>
    public string Country { get; init; } = "";

    /// <summary>Chromium's <c>CertPrincipal::GetDisplayName</c>: the common name, else the first organization, else the
    /// first organization unit.</summary>
    internal string DisplayName => CommonName.Length > 0 ? CommonName
        : Organizations.Count > 0 ? Organizations[0]
        : OrganizationUnits.Count > 0 ? OrganizationUnits[0]
        : "";

    internal static CertificatePrincipal Of(X500DistinguishedName name)
    {
        string first(string oid) => Values(name, oid).FirstOrDefault() ?? "";
        return new CertificatePrincipal
        {
            CommonName = first("2.5.4.3"),
            Organizations = Values(name, "2.5.4.10"),
            OrganizationUnits = Values(name, "2.5.4.11"),
            Locality = first("2.5.4.7"),
            State = first("2.5.4.8"),
            Country = first("2.5.4.6"),
        };
    }

    private static List<string> Values(X500DistinguishedName name, string oid)
        => [.. name.EnumerateRelativeDistinguishedNames()
            .Where(rdn => !rdn.HasMultipleElements && rdn.GetSingleElementType().Value == oid)
            .Select(rdn => rdn.GetSingleElementValue() ?? "")];
}

/// <summary>An X.509 certificate; Electron's <c>Certificate</c>.</summary>
public sealed record Certificate
{
    /// <summary>The certificate in PEM.</summary>
    public required string Data { get; init; }

    /// <summary>Who issued it.</summary>
    public required CertificatePrincipal Issuer { get; init; }

    /// <summary>The issuer's display name.</summary>
    public required string IssuerName { get; init; }

    /// <summary>The issuer's certificate, when the chain has it.</summary>
    public Certificate? IssuerCert { get; init; }

    /// <summary>Who it was issued to.</summary>
    public required CertificatePrincipal Subject { get; init; }

    /// <summary>The subject's display name.</summary>
    public required string SubjectName { get; init; }

    /// <summary>The serial number in hex.</summary>
    public required string SerialNumber { get; init; }

    /// <summary>When it becomes valid, in seconds since the Unix epoch.</summary>
    public required double ValidStart { get; init; }

    /// <summary>When it expires, in seconds since the Unix epoch.</summary>
    public required double ValidExpiry { get; init; }

    /// <summary>The SHA-256 fingerprint, as <c>sha256/</c> and its base64.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The certificate and its issuers, from DER, the leaf first; null for an empty chain.</summary>
    internal static Certificate? FromChain(IReadOnlyList<byte[]> chain, int index = 0)
    {
        if (index >= chain.Count || chain[index].Length == 0) return null;
        var der = chain[index];
        using var x509 = X509CertificateLoader.LoadCertificate(der);
        var subject = CertificatePrincipal.Of(x509.SubjectName);
        var issuer = CertificatePrincipal.Of(x509.IssuerName);
        return new Certificate
        {
            Data = Pem(der),
            Issuer = issuer,
            IssuerName = issuer.DisplayName,
            IssuerCert = FromChain(chain, index + 1),
            Subject = subject,
            SubjectName = subject.DisplayName,
            SerialNumber = x509.SerialNumber,
            ValidStart = new DateTimeOffset(x509.NotBefore.ToUniversalTime()).ToUnixTimeSeconds(),
            ValidExpiry = new DateTimeOffset(x509.NotAfter.ToUniversalTime()).ToUnixTimeSeconds(),
            Fingerprint = "sha256/" + Convert.ToBase64String(SHA256.HashData(der)),
        };
    }

    // Chromium's X509Certificate::GetPEMEncodedFromDER: 64-character lines, each ending in a newline.
    internal static string Pem(byte[] der)
    {
        var base64 = Convert.ToBase64String(der);
        var pem = new StringBuilder("-----BEGIN CERTIFICATE-----\n");
        for (var i = 0; i < base64.Length; i += 64) pem.Append(base64, i, Math.Min(64, base64.Length - i)).Append('\n');
        return pem.Append("-----END CERTIFICATE-----\n").ToString();
    }
}
