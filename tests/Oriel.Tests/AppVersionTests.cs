using System.Reflection;
using System.Reflection.Emit;

namespace Oriel.Tests;

public sealed class AppVersionTests
{
    private static Assembly Make(string? informational, Version version)
    {
        var name = new AssemblyName("V" + Guid.NewGuid().ToString("N")) { Version = version };
        var attributes = informational is null ? [] : new[] { new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [informational]) };
        return AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run, attributes);
    }

    [Fact]
    public void VersionOf_ReadsInformationalVersion_WithoutBuildMetadata()
    {
        Assert.Equal("1.4.2-beta.3", OrielApp.VersionOf(Make("1.4.2-beta.3+abc123", new Version(1, 4, 2, 0))));
        Assert.Equal("2.0.0", OrielApp.VersionOf(Make("2.0.0", new Version(9, 9, 9, 9))));
        Assert.Equal("3.1.4", OrielApp.VersionOf(Make(null, new Version(3, 1, 4, 7))));
        Assert.Equal("3.1.4", OrielApp.VersionOf(Make("", new Version(3, 1, 4, 7))));
        Assert.Equal("0.0.0", OrielApp.VersionOf(null));
    }
}
