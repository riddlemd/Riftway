using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Riftway;

/// <summary>Options for <see cref="Dialog.ShowCertificateTrustDialogAsync(BaseWindow?, CertificateTrustDialogOptions)"/>;
/// Electron's <c>CertificateTrustDialogOptions</c>.</summary>
public sealed record CertificateTrustDialogOptions
{
    /// <summary>The certificate to trust or import; its <see cref="Certificate.Data"/> and, when set, its direct
    /// <see cref="Certificate.IssuerCert"/> are used.</summary>
    public required Certificate Certificate { get; init; }

    /// <summary>The message shown to the user.</summary>
    public string Message { get; init; } = "";
}

// Electron v44's Converter<scoped_refptr<net::X509Certificate>>::FromV8 (shell/common/gin_converters/net_converter.cc).
internal static class CertificateTrust
{
    /// <summary>The DER chain Electron builds: the leaf, then its direct issuer, whose own issuers must convert too but are
    /// left out.</summary>
    /// <exception cref="ArgumentException">A certificate's data is not one certificate, Electron's conversion failure.</exception>
    internal static IReadOnlyList<byte[]> Chain(Certificate certificate)
    {
        var leaf = Der(certificate.Data) ?? throw new ArgumentException("The certificate data is not a certificate.", nameof(certificate));
        return certificate.IssuerCert is { } issuer ? [leaf, Chain(issuer)[0]] : [leaf];
    }

    /// <summary><c>CertFromData</c>: Chromium's <c>CreateCertificateListFromBytes</c> with
    /// <c>FORMAT_SINGLE_CERTIFICATE</c>, the first <c>CERTIFICATE</c> PEM block, else the bytes themselves, as DER.</summary>
    internal static byte[]? Der(string data)
    {
        var text = data.AsSpan();
        while (PemEncoding.TryFind(text, out var fields))
        {
            if (text[fields.Label].SequenceEqual("CERTIFICATE"))
            {
                // Only the first certificate block counts; PEM text is never DER, so a bad block is no certificate.
                var decoded = new byte[fields.DecodedDataLength];
                return Convert.TryFromBase64Chars(text[fields.Base64Data], decoded, out var written) && Parses(decoded.AsSpan(0, written))
                    ? decoded[..written]
                    : null;
            }
            text = text[fields.Location.End..];
        }
        return FromBytes(data);
    }

    private static byte[]? FromBytes(string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        return Parses(bytes) ? bytes : null;
    }

    private static bool Parses(ReadOnlySpan<byte> der)
    {
        try
        {
            using var _ = X509CertificateLoader.LoadCertificate(der);
            return true;
        }
        catch (CryptographicException) { return false; }
    }
}
