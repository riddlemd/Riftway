using System.Runtime.InteropServices;

namespace Riftway.Tests;

public sealed class UserAgentMetadataTests
{
    [Fact]
    public void Brands_FollowChromiumsGreaseAndOrder_ForItsMajorVersion()
    {
        // Chromium 154 reports [Not A(Brand 99, Chromium 154]: seed 154 picks " " and "(", version "99", GREASE first.
        Assert.Equal("""[{"brand":"Not A(Brand","version":"99"},{"brand":"Chromium","version":"154"}]""",
            UserAgentMetadata.Brands(154, "154", fullVersion: false).ToJsonString());
        Assert.Equal("""[{"brand":"Not A(Brand","version":"99.0.0.0"},{"brand":"Chromium","version":"154.0.8037.94"}]""",
            UserAgentMetadata.Brands(154, "154.0.8037.94", fullVersion: true).ToJsonString());
        // An odd seed puts Chromium first; 155 picks "(" and ":" and version "24".
        Assert.Equal("""[{"brand":"Chromium","version":"155"},{"brand":"Not(A:Brand","version":"24"}]""",
            UserAgentMetadata.Brands(155, "155", fullVersion: false).ToJsonString());
    }

    [Fact]
    public void Build_FillsEveryFieldDevToolsAsksFor()
    {
        var metadata = UserAgentMetadata.Build("154.0.8037.94", "Linux", "", "arm", "64");
        Assert.Equal("Linux", (string?)metadata["platform"]);
        Assert.Equal("", (string?)metadata["model"]);
        Assert.False((bool)metadata["mobile"]!);
        Assert.False((bool)metadata["wow64"]!);
        Assert.Equal("""["Desktop"]""", metadata["formFactors"]!.ToJsonString());
        Assert.Equal("154.0.8037.94", (string?)metadata["fullVersionList"]![1]!["version"]);
    }

    [Theory]
    [InlineData(Architecture.Arm64, "arm", "64")]
    [InlineData(Architecture.Arm, "arm", "32")]
    [InlineData(Architecture.X64, "x86", "64")]
    [InlineData(Architecture.X86, "x86", "32")]
    public void Cpu_IsChromiumsArchitectureAndBitness(Architecture architecture, string expected, string bitness)
        => Assert.Equal((expected, bitness), UserAgentMetadata.Cpu(architecture));

    [Fact]
    public void WindowsContractVersion_IsHiwordDotLoword_ElseChromiumsHighestKnown()
    {
        Assert.Equal("19.0.0", UserAgentMetadata.WindowsContractVersion((19u << 16) | 0));
        Assert.Equal("15.2.0", UserAgentMetadata.WindowsContractVersion((15u << 16) | 2));
        Assert.Equal("19.0.0", UserAgentMetadata.WindowsContractVersion(null));
    }
}
