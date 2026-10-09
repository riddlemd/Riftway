using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oriel.Tests;

public sealed class SelectClientCertificateTests
{
    private static Certificate Make(string name)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return Certificate.FromChain([cert.RawData])!;
    }

    private static readonly Certificate A = Make("a"), B = Make("b"), Other = Make("other");

    private static SelectClientCertificateEventArgs Args(params Certificate[] list) => new(null!, "127.0.0.1:443", list);

    [Fact]
    public async Task Answer_WithoutPreventDefault_IsTheFirst()
    {
        var args = Args(A, B);
        args.Callback(B);
        Assert.Equal(0, await args.Answer);
    }

    [Fact]
    public async Task Answer_WithoutPreventDefault_OfAnEmptyList_IsNone()
        => Assert.Null(await Args().Answer);

    [Fact]
    public async Task Answer_AfterPreventDefault_IsTheChosensIndex()
    {
        var args = Args(A, B);
        args.Cancel = true;
        args.Callback(B);
        Assert.Equal(1, await args.Answer);
    }

    [Fact]
    public async Task Answer_MatchesAnEqualCertificateByFingerprint()
    {
        var args = Args(A, B);
        args.Cancel = true;
        args.Callback(B with { SubjectName = "rebuilt" });
        Assert.Equal(1, await args.Answer);
    }

    [Fact]
    public async Task Answer_OfNoneOrAStranger_IsNone()
    {
        var none = Args(A, B);
        none.Cancel = true;
        none.Callback();
        Assert.Null(await none.Answer);
        var stranger = Args(A, B);
        stranger.Cancel = true;
        stranger.Callback(Other);
        Assert.Null(await stranger.Answer);
    }

    [Fact]
    public async Task Answer_AfterPreventDefault_WaitsForTheCallback()
    {
        var args = Args(A, B);
        args.Cancel = true;
        var answer = args.Answer;
        await Task.Delay(50);
        Assert.False(answer.IsCompleted);
        args.Callback(A);
        Assert.Equal(0, await answer.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
