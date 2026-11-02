using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Riftway;

/// <summary>Chromium's default user agent client hints (<c>embedder_support::GetUserAgentMetadata</c>), as DevTools'
/// <c>Emulation.UserAgentMetadata</c>: Electron keeps them when it overrides the user agent.</summary>
internal static class UserAgentMetadata
{
    // GetGreasedUserAgentBrandVersion's GREASE characters and versions.
    private static readonly string[] s_greaseChars = [" ", "(", ":", "-", ".", "/", ")", ";", "=", "?", "_"];
    private static readonly string[] s_greaseVersions = ["8", "99", "24"];

    /// <summary>The metadata for this machine and Chromium <paramref name="chromeVersion"/> (major.minor.build.patch).</summary>
    public static JsonObject ForThisMachine(string chromeVersion)
    {
        var (architecture, bitness) = Cpu(RuntimeInformation.OSArchitecture);
        return Build(chromeVersion, Platform(), PlatformVersion(), architecture, bitness);
    }

    internal static JsonObject Build(string chromeVersion, string platform, string platformVersion, string architecture, string bitness)
    {
        var major = chromeVersion.Split('.')[0];
        var seed = int.Parse(major, System.Globalization.CultureInfo.InvariantCulture);
        return new JsonObject
        {
            ["brands"] = Brands(seed, major, fullVersion: false),
            ["fullVersionList"] = Brands(seed, chromeVersion, fullVersion: true),
            ["platform"] = platform,
            ["platformVersion"] = platformVersion,
            ["architecture"] = architecture,
            ["model"] = "",
            ["mobile"] = false,
            ["bitness"] = bitness,
            ["wow64"] = false,
            ["formFactors"] = new JsonArray("Desktop"),
        };
    }

    // GenerateBrandVersionList for a Chromium-branded build: the GREASE brand and Chromium, in ShuffleBrandList's order.
    internal static JsonArray Brands(int seed, string version, bool fullVersion)
    {
        var grease = $"Not{s_greaseChars[seed % s_greaseChars.Length]}A{s_greaseChars[(seed + 1) % s_greaseChars.Length]}Brand";
        var greaseVersion = s_greaseVersions[seed % s_greaseVersions.Length];
        var list = new[] { (grease, fullVersion ? greaseVersion + ".0.0.0" : greaseVersion), ("Chromium", version) };
        // GetRandomOrder for two brands: item i goes to position order[i].
        var order = new[] { seed % 2, (seed + 1) % 2 };
        var shuffled = new (string Brand, string Version)[2];
        for (var i = 0; i < 2; i++) shuffled[order[i]] = list[i];
        return [.. shuffled.Select(b => (JsonNode)new JsonObject { ["brand"] = b.Brand, ["version"] = b.Version })];
    }

    // GetPlatformForUAMetadata: version_info::GetOSType, but "macOS" on macOS.
    private static string Platform()
        => OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "";

    // GetPlatformVersion: empty on Linux, the OS version on macOS, the UniversalApiContract version on Windows.
    private static string PlatformVersion()
    {
        if (OperatingSystem.IsMacOS())
            return Environment.OSVersion.Version is var v ? $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}" : "";
        if (OperatingSystem.IsWindows()) return WindowsContractVersion(ReadUniversalApiContract());
        return "";
    }

    // HIWORD.LOWORD.0 of WellKnownContracts' Windows.Foundation.UniversalApiContract; Chromium's highest known (19)
    // when it is unreadable. Windows before 10 RS5 has its own table in Chromium, which Riftway does not support.
    internal static string WindowsContractVersion(uint? contract)
        => contract is { } value ? $"{value >> 16}.{value & 0xFFFF}.0" : "19.0.0";

    private static uint? ReadUniversalApiContract()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\WindowsRuntime\WellKnownContracts");
        return key?.GetValue("Windows.Foundation.UniversalApiContract") is int dword ? unchecked((uint)dword) : null;
    }

    // GetCpuArchitecture and GetCpuBitness: arm or x86, by the OS's architecture.
    internal static (string Architecture, string Bitness) Cpu(Architecture architecture) => architecture switch
    {
        Architecture.Arm64 => ("arm", "64"),
        Architecture.Arm or Architecture.Armv6 => ("arm", "32"),
        Architecture.X64 => ("x86", "64"),
        Architecture.X86 => ("x86", "32"),
        _ => ("", "64"),
    };
}
