using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Riftway.Tests;

public sealed class CertificateTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly DateTimeOffset Expiry = new(2027, 6, 7, 8, 9, 10, TimeSpan.Zero);

    // A CA and a leaf it signed, as DER: the leaf first.
    private static (byte[] Leaf, byte[] Ca) Chain()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=Riftway Test CA, O=Riftway, O=Second Org, OU=Tests, L=Town, S=State, C=NZ", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(Start.AddDays(-1), Expiry.AddDays(1));
        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=leaf.riftway.test, O=Leaf Org", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var leaf = leafRequest.Create(ca, Start, Expiry, [0x01, 0x02, 0xAB]);
        return (leaf.RawData, ca.RawData);
    }

    [Fact]
    public void FromChain_ReadsElectronsFields()
    {
        var (leaf, ca) = Chain();
        var certificate = Certificate.FromChain([leaf, ca])!;

        Assert.Equal("leaf.riftway.test", certificate.SubjectName);
        Assert.Equal(["Leaf Org"], certificate.Subject.Organizations);
        Assert.Equal("Riftway Test CA", certificate.IssuerName);
        var issuer = certificate.Issuer;
        Assert.Equal(("Riftway Test CA", "Town", "State", "NZ"), (issuer.CommonName, issuer.Locality, issuer.State, issuer.Country));
        Assert.Equal(["Riftway", "Second Org"], issuer.Organizations);
        Assert.Equal(["Tests"], issuer.OrganizationUnits);
        Assert.Equal("0102AB", certificate.SerialNumber);
        Assert.Equal(Start.ToUnixTimeSeconds(), certificate.ValidStart);
        Assert.Equal(Expiry.ToUnixTimeSeconds(), certificate.ValidExpiry);
        Assert.Equal("sha256/" + Convert.ToBase64String(SHA256.HashData(leaf)), certificate.Fingerprint);
    }

    [Fact]
    public void FromChain_LinksEachIssuer()
    {
        var (leaf, ca) = Chain();
        var certificate = Certificate.FromChain([leaf, ca])!;

        Assert.Equal("Riftway Test CA", certificate.IssuerCert?.SubjectName);
        Assert.Equal("sha256/" + Convert.ToBase64String(SHA256.HashData(ca)), certificate.IssuerCert?.Fingerprint);
        Assert.Null(certificate.IssuerCert!.IssuerCert);
        Assert.Null(Certificate.FromChain([leaf])!.IssuerCert);
    }

    [Fact]
    public void FromChain_EmptyIsNull()
    {
        Assert.Null(Certificate.FromChain([]));
        Assert.Null(Certificate.FromChain([[]]));
    }

    [Fact]
    public void Data_IsChromiumsPem()
    {
        var (leaf, _) = Chain();
        var pem = Certificate.FromChain([leaf])!.Data;

        Assert.StartsWith("-----BEGIN CERTIFICATE-----\n", pem);
        Assert.EndsWith("\n-----END CERTIFICATE-----\n", pem);
        var lines = pem.Split('\n')[1..^2];
        Assert.All(lines[..^1], line => Assert.Equal(64, line.Length));
        Assert.InRange(lines[^1].Length, 1, 64);
        Assert.Equal(leaf, X509Certificate2.CreateFromPem(pem).RawData);
    }

    [Fact]
    public void Pem_ExactLinesEndCleanly()
        => Assert.Equal("-----BEGIN CERTIFICATE-----\n" + new string('A', 64) + "\n-----END CERTIFICATE-----\n", Certificate.Pem(new byte[48]));

    [Theory]
    [InlineData("CN=Name, O=Org, OU=Unit", "Name")]
    [InlineData("O=Org, O=Other, OU=Unit", "Org")]
    [InlineData("OU=Unit", "Unit")]
    [InlineData("C=NZ", "")]
    public void DisplayName_FallsBackAsChromium(string name, string display)
        => Assert.Equal(display, CertificatePrincipal.Of(new X500DistinguishedName(name)).DisplayName);

}
