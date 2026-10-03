using Oriel.Cef.Platform;

namespace Oriel.Tests;

/// <summary>A fact that only runs on macOS and shows as skipped elsewhere, rather than passing vacuously.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "macOS layout";
    }
}

public sealed class CefLayoutTests
{
    // The test host stands in for an app's Contents/MacOS directory.
    private static readonly string s_exeDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    private static readonly string s_contents = Path.GetDirectoryName(s_exeDirectory)!;

    [MacFact]
    public void MainProcessLibrary_IsTheFrameworkInContentsFrameworks()
    {
        Assert.Equal(
            $"{s_contents}/Frameworks/Chromium Embedded Framework.framework/Chromium Embedded Framework",
            CefLayout.MainProcessLibrary());
    }

    [MacFact]
    public void SubprocessLibrary_ClimbsOutOfTheHelperBundleToTheSameFramework()
    {
        // A helper's exe sits at <App>.app/Contents/Frameworks/<App> Helper.app/Contents/MacOS.
        var frameworks = Path.GetDirectoryName(Path.GetDirectoryName(s_contents))!;

        Assert.Equal(
            $"{frameworks}/Chromium Embedded Framework.framework/Chromium Embedded Framework",
            CefLayout.SubprocessLibrary());
    }

    [MacFact]
    public void SubprocessPath_IsLeftToCefOnMac() => Assert.Null(CefLayout.SubprocessPath());
}
