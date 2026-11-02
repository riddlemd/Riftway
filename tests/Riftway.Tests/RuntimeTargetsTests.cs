using System.Diagnostics;
using System.Text.Json;

namespace Riftway.Tests;

/// <summary>Evaluates eng/runtime/Riftway.Runtime.targets the way NuGet imports it from a runtime package:
/// as buildTransitive/Riftway.Runtime.&lt;rid&gt;.targets, so the RID comes from the file name.</summary>
public sealed class RuntimeTargetsTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs) Directory.Delete(dir, recursive: true);
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    public void Given_ARidLessBuildOnTheRuntimesOwnOs_When_TheTargetsEvaluate_Then_NativeAssetsAreFlattenedBesideTheApp(string rid)
    {
        // Given
        var project = WriteProjectImporting(rid);

        // When
        var properties = Evaluate(project, hostRid: rid, runtimeIdentifier: null);

        // Then
        Assert.Equal(rid, properties["_RiftwayRuntimeRid"]);
        Assert.Equal("true", properties["_RiftwayRuntimeFlatten"]);
    }

    [Fact]
    public void Given_ARidLessBuildOnAMac_When_TheOsxTargetsEvaluate_Then_NothingIsFlattenedBecauseTheAppBundleTakesIt()
    {
        // Given
        var project = WriteProjectImporting("osx-arm64");

        // When
        var properties = Evaluate(project, hostRid: "osx-arm64", runtimeIdentifier: null);

        // Then
        Assert.Equal("osx-arm64", properties["_RiftwayRuntimeRid"]);
        Assert.Equal("", properties["_RiftwayRuntimeFlatten"]);
    }

    [Fact]
    public void Given_ARidLessBuildOnAnotherOs_When_TheTargetsEvaluate_Then_NothingIsFlattened()
    {
        // Given
        var project = WriteProjectImporting("win-x64");

        // When
        var properties = Evaluate(project, hostRid: "linux-x64", runtimeIdentifier: null);

        // Then
        Assert.Equal("", properties["_RiftwayRuntimeFlatten"]);
    }

    [Fact]
    public void Given_ARidBuild_When_TheTargetsEvaluate_Then_NothingIsFlattenedBecauseNuGetAlreadyPlacesTheAssets()
    {
        // Given
        var project = WriteProjectImporting("win-x64");

        // When
        var properties = Evaluate(project, hostRid: "win-x64", runtimeIdentifier: "win-x64");

        // Then
        Assert.Equal("", properties["_RiftwayRuntimeFlatten"]);
    }

    private string WriteProjectImporting(string rid)
    {
        var dir = Directory.CreateTempSubdirectory("riftway-runtime-targets-").FullName;
        _tempDirs.Add(dir);
        var buildTransitive = Directory.CreateDirectory(Path.Combine(dir, "buildTransitive")).FullName;
        File.Copy(Path.Combine(RepoRoot(), "eng", "runtime", "Riftway.Runtime.targets"),
            Path.Combine(buildTransitive, $"Riftway.Runtime.{rid}.targets"));
        var project = Path.Combine(dir, "probe.proj");
        File.WriteAllText(project, $"<Project><Import Project=\"buildTransitive/Riftway.Runtime.{rid}.targets\" /></Project>");
        return project;
    }

    private static Dictionary<string, string> Evaluate(string project, string hostRid, string? runtimeIdentifier)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(project)!,
        };
        foreach (var arg in new[]
        {
            "msbuild", project, "-nologo", "-nodeReuse:false",
            "-getProperty:_RiftwayRuntimeRid", "-getProperty:_RiftwayRuntimeFlatten",
            $"-p:NETCoreSdkRuntimeIdentifier={hostRid}", $"-p:RuntimeIdentifier={runtimeIdentifier}",
        })
        {
            start.ArgumentList.Add(arg);
        }
        // The test host's own MSBuild variables would point the child at the test SDK's internals.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "dotnet msbuild did not finish within 60s");
        Assert.True(process.ExitCode == 0, $"dotnet msbuild exited {process.ExitCode}: {stdout.Result}{stderr.Result}");

        using var json = JsonDocument.Parse(stdout.Result);
        return json.RootElement.GetProperty("Properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "eng", "runtime", "Riftway.Runtime.targets"))) return dir.FullName;
        }
        throw new InvalidOperationException($"No eng/runtime/Riftway.Runtime.targets above {AppContext.BaseDirectory}");
    }
}
