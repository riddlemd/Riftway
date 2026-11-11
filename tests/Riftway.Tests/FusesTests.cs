using System.Reflection;
using Riftway.Blazor;
using Microsoft.Extensions.FileProviders;

namespace Riftway.Tests;

public sealed class FusesTests
{
    [Fact]
    public void Defaults_AreElectrons() // this test host sets none
    {
        Assert.False(Fuses.ArchiveIntegrity);
        Assert.False(Fuses.OnlyLoadFromArchive);
        Assert.True(Fuses.StartupHooks);
        Assert.True(Fuses.RemoteDebugging);
        Assert.True(Fuses.DotnetEnvironment);
    }

    [Fact]
    public void Metadata_ReadsOnlyRiftwayFuses() => Assert.Empty(Fuses.Metadata(typeof(FusesTests).Assembly));

    [Fact]
    public void Metadata_OfNoAssembly_IsEmpty() => Assert.Empty(Fuses.Metadata(null));

    [Fact]
    public void InjectingVariables_AreTheOnesSet()
    {
        var environment = new Dictionary<string, string?>
        {
            ["DOTNET_ADDITIONAL_DEPS"] = "x.deps.json",
            ["CORECLR_ENABLE_PROFILING"] = "1",
            ["DOTNET_STARTUP_HOOKS"] = "",
            ["DOTNET_gcServer"] = "1",
        };
        Assert.Equal(["DOTNET_ADDITIONAL_DEPS", "CORECLR_ENABLE_PROFILING"], Fuses.InjectingVariablesSet(n => environment.GetValueOrDefault(n)));
        Assert.Empty(Fuses.InjectingVariablesSet(_ => null));
    }

    [Fact]
    public void RemoteDebuggingSwitches_CoverPortPipeAddressAndOrigins()
        => Assert.Equal(["remote-debugging-port", "remote-debugging-pipe", "remote-debugging-address", "remote-allow-origins"], Fuses.RemoteDebuggingSwitches);

    [Fact]
    public void OnlyLoadFromArchive_DropsTheLooseFolder()
    {
        var root = Directory.CreateTempSubdirectory("riftway-fuse-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
            using (var loose = BlazorWebView.StaticFiles(new BlazorWebViewOptions(), root) as IDisposable) Assert.IsType<PhysicalFileProvider>(loose);
            Assert.IsType<NullFileProvider>(BlazorWebView.StaticFiles(new BlazorWebViewOptions(), root, onlyFromArchive: true));
            // An explicit ContentRoot is the app's own choice, not the default search the fuse closes.
            using var chosen = BlazorWebView.StaticFiles(new BlazorWebViewOptions { ContentRoot = Path.Combine(root, "wwwroot") }, root, onlyFromArchive: true) as IDisposable;
            Assert.IsType<PhysicalFileProvider>(chosen);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
