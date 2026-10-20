using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oriel.Tests;

// Electron v44's certificate conversion for dialog.showCertificateTrustDialog, and the dialog's own steps.
[Collection(nameof(CertificateTrustCollection))]
public sealed class CertificateTrustTests : IDisposable
{
    private readonly Action<nint, IReadOnlyList<byte[]>, string, Action> _saved = Dialog.ShowTrustPanel;
    private readonly List<(nint View, IReadOnlyList<byte[]> Chain, string Message, Action Done)> _shown = [];

    public CertificateTrustTests() => Dialog.ShowTrustPanel = (view, chain, message, done) => _shown.Add((view, chain, message, done));

    public void Dispose() => Dialog.ShowTrustPanel = _saved;

    // A root, an intermediate it signed and a leaf the intermediate signed, as DER.
    private static (byte[] Leaf, byte[] Intermediate, byte[] Root) Chain()
    {
        var now = DateTimeOffset.UtcNow;
        using var rootKey = ECDsa.Create();
        var rootRequest = new CertificateRequest("CN=Root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
        using var midKey = ECDsa.Create();
        var midRequest = new CertificateRequest("CN=Intermediate", midKey, HashAlgorithmName.SHA256);
        midRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var mid = midRequest.Create(root, now.AddDays(-1), now.AddDays(20), [1]).CopyWithPrivateKey(midKey);
        using var leafKey = ECDsa.Create();
        using var leaf = new CertificateRequest("CN=leaf", leafKey, HashAlgorithmName.SHA256).Create(mid, now.AddDays(-1), now.AddDays(10), [2]);
        return (leaf.RawData, mid.RawData, root.RawData);
    }

    private static Certificate Of(params byte[][] chain) => Certificate.FromChain(chain)!;

    [Fact]
    public void ThePemLeafComesFirst_ThenOnlyItsDirectIssuer()
    {
        var (leaf, mid, root) = Chain();
        var chain = CertificateTrust.Chain(Of(leaf, mid, root));
        Assert.Equal(2, chain.Count);
        Assert.Equal(leaf, chain[0]);
        Assert.Equal(mid, chain[1]);
    }

    [Fact]
    public void WithoutAnIssuerTheChainIsTheLeaf()
    {
        var (leaf, _, _) = Chain();
        Assert.Equal(leaf, Assert.Single(CertificateTrust.Chain(Of(leaf))));
    }

    [Fact]
    public void AnIssuerWhoseOwnIssuerIsBadFailsTheWholeConversion()
    {
        var (leaf, mid, root) = Chain();
        var certificate = Of(leaf, mid, root);
        var broken = certificate with { IssuerCert = certificate.IssuerCert! with { IssuerCert = certificate.IssuerCert.IssuerCert! with { Data = "junk" } } };
        Assert.Throws<ArgumentException>(() => CertificateTrust.Chain(broken));
    }

    [Fact]
    public void OnlyTheFirstCertificateBlockCounts_EvenWhenItDoesNotParse()
    {
        var (leaf, mid, _) = Chain();
        var pem = Certificate.Pem(leaf);
        Assert.Equal(leaf, CertificateTrust.Der("-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n" + pem));
        Assert.Equal(leaf, CertificateTrust.Der(pem + Certificate.Pem(mid)));
        Assert.Null(CertificateTrust.Der("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n" + pem));
    }

    [Fact]
    public void DataThatIsNoCertificateIsRefused()
    {
        Assert.Null(CertificateTrust.Der(""));
        Assert.Null(CertificateTrust.Der("not a certificate"));
        var (leaf, _, _) = Chain();
        Assert.Throws<ArgumentException>(() => CertificateTrust.Chain(Of(leaf) with { Data = "junk" }));
    }

    [Fact]
    public async Task ThePanelGetsTheChainAndMessage_AndTheTaskEndsWithIt()
    {
        var (leaf, mid, _) = Chain();
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Throws<PlatformNotSupportedException>(() => { _ = Dialog.ShowCertificateTrustDialogAsync(new CertificateTrustDialogOptions { Certificate = Of(leaf) }); });
            return;
        }
        var task = Dialog.ShowCertificateTrustDialogAsync(new CertificateTrustDialogOptions { Certificate = Of(leaf, mid), Message = "Trust it?" });
        var (view, chain, message, done) = Assert.Single(_shown);
        Assert.Equal((0, 2, "Trust it?"), (view, chain.Count, message));
        Assert.False(task.IsCompleted);
        done();
        await task;
    }

    [Fact]
    public async Task APanelThatFailsFaultsTheTask()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Dialog.ShowTrustPanel = (_, _, _, _) => throw new ArgumentException("Security refused the certificate.");
        var (leaf, _, _) = Chain();
        var task = Dialog.ShowCertificateTrustDialogAsync(new CertificateTrustDialogOptions { Certificate = Of(leaf) });
        await Assert.ThrowsAsync<ArgumentException>(() => task);
    }

    [Fact]
    public void BadDataThrowsBeforeAnyPanel()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var (leaf, _, _) = Chain();
        Assert.Throws<ArgumentException>(() => { _ = Dialog.ShowCertificateTrustDialogAsync(new CertificateTrustDialogOptions { Certificate = Of(leaf) with { Data = "junk" } }); });
        Assert.Empty(_shown);
    }
}

[CollectionDefinition(nameof(CertificateTrustCollection), DisableParallelization = true)]
public sealed class CertificateTrustCollection;
