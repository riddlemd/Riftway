using System.Text;
using System.Text.Json;

namespace Oriel.Tests;

public sealed class SessionNetworkTests
{
    private static string Emulation(NetworkEmulationOptions? options)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            NetworkPrefs.WriteEmulation(w, options);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void Emulation_IsOneRuleForEveryRequest_WithEachOption()
        => Assert.Equal("""{"matchedNetworkConditions":[{"urlPattern":"","offline":true,"latency":500,"downloadThroughput":6400,"uploadThroughput":3200}]}""",
            Emulation(new NetworkEmulationOptions { Offline = true, Latency = 500, DownloadThroughput = 6400, UploadThroughput = 3200 }));

    [Fact]
    public void Emulation_DefaultsToOnlineAndUnthrottled()
        => Assert.Equal("""{"matchedNetworkConditions":[{"urlPattern":"","offline":false,"latency":0,"downloadThroughput":0,"uploadThroughput":0}]}""",
            Emulation(new NetworkEmulationOptions()));

    [Fact]
    public void Emulation_NullClearsTheRules() => Assert.Equal("""{"matchedNetworkConditions":[]}""", Emulation(null));

    [Fact]
    public void Ssl_EmptyConfigIsChromiumsDefaults()
    {
        var prefs = NetworkPrefs.Ssl(new SslConfig());
        Assert.Equal(("", ""), (prefs.MinVersion, prefs.MaxVersion));
        Assert.Empty(prefs.DisabledCipherSuites);
    }

    [Theory]
    [InlineData("tls1.2", "tls1.3")]
    [InlineData("tls1.3", "tls1.2")]
    public void Ssl_KeepsBothVersions(string min, string max)
    {
        var prefs = NetworkPrefs.Ssl(new SslConfig { MinVersion = min, MaxVersion = max });
        Assert.Equal((min, max), (prefs.MinVersion, prefs.MaxVersion));
    }

    [Theory]
    [InlineData("tls1")]
    [InlineData("tls1.1")]
    [InlineData("TLS1.2")]
    public void Ssl_RefusesOtherVersions(string version)
    {
        Assert.Equal("minVersion", Assert.Throws<ArgumentException>(() => NetworkPrefs.Ssl(new SslConfig { MinVersion = version })).ParamName);
        Assert.Equal("maxVersion", Assert.Throws<ArgumentException>(() => NetworkPrefs.Ssl(new SslConfig { MaxVersion = version })).ParamName);
    }

    [Fact]
    public void Ssl_CipherSuitesAreSortedSixCharacterHex()
        => Assert.Equal(["0x0004", "0x00FF", "0xC002"], NetworkPrefs.Ssl(new SslConfig { DisabledCipherSuites = [0xC002, 0x0004, 0x00FF] }).DisabledCipherSuites);
}
