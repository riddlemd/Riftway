using Riftway.Asar;

namespace Riftway.Tests;

// The ArchiveIntegrity fuse's decision, from the JSON Electron's packager writes as the exe's Integrity/ElectronAsar.
public sealed class AppArchiveIntegrityTests
{
    private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "app"));
    private static readonly string Archive = Path.Combine(Base, "resources", "app.asar");
    private const string Hash = "9d1f61ea03c4bb62b4416387a521101b81151da0cfbe18c9f8c8b818c5cebfac";
    private static readonly string Json = $"[{{\"file\":\"resources\\\\app.asar\",\"alg\":\"sha256\",\"value\":\"{Hash}\"}}]";

    [Fact]
    public void FuseOff_AllowsAnything()
    {
        var policy = AppArchiveIntegrity.Parse(false, null, Base);
        Assert.False(policy.Enabled);
        Assert.Null(AppArchiveIntegrity.Check(policy, Archive, "anything"));
    }

    [Fact]
    public void AListedArchive_PassesWithItsHash()
    {
        var policy = AppArchiveIntegrity.Parse(true, Json, Base);
        Assert.Null(policy.Error);
        Assert.Null(AppArchiveIntegrity.Check(policy, Archive, Hash));
        Assert.Null(AppArchiveIntegrity.Check(policy, Archive, Hash.ToUpperInvariant()));
    }

    [Fact]
    public void AnotherHash_Fails()
        => Assert.Contains("Integrity check failed", AppArchiveIntegrity.Check(AppArchiveIntegrity.Parse(true, Json, Base), Archive, new string('0', 64)));

    [Fact]
    public void AnUnlistedArchive_Fails()
        => Assert.Contains("has no integrity", AppArchiveIntegrity.Check(AppArchiveIntegrity.Parse(true, Json, Base), Path.Combine(Base, "other.asar"), Hash));

    [Theory]
    [InlineData(null, "no archive integrity")]
    [InlineData("not json", "malformed")]
    [InlineData("[{\"file\":\"resources\\\\app.asar\",\"alg\":\"md5\",\"value\":\"x\"}]", "only sha256")]
    [InlineData("[{\"file\":\"resources\\\\app.asar\"}]", "malformed")]
    public void NoUsableIntegrity_FailsEveryArchive(string? json, string why)
    {
        var policy = AppArchiveIntegrity.Parse(true, json, Base);
        Assert.Contains(why, policy.Error);
        Assert.Equal(policy.Error, AppArchiveIntegrity.Check(policy, Archive, Hash));
    }

    [Fact]
    public void ThisTestHost_HasTheFuseOff() => Assert.False(AppArchiveIntegrity.Current.Enabled);
}
