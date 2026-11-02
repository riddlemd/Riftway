namespace Riftway.Tests;

public sealed class AppPathServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("riftway-paths-").FullName;
    private string _name = "App";

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Base answers under the temp root, so the directories the providers make are throwaway.
    private AppPathService Service(bool mac = false, bool windows = false) => new(() => _name, (name, app) => name switch
    {
        "appData" => Path.Combine(_root, "appData"),
        "cache" => Path.Combine(_root, "cache"),
        "home" => Path.Combine(_root, "home"),
        "desktop" => Path.Combine(_root, "Desktop"),
        "recent" => Path.Combine(_root, "Recent"),
        "music" => Path.Combine(_root, "Music"),
        _ => null,
    }, mac, windows);

    [Fact]
    public void UserDataIsAppDataAndTheNameAndIsMade()
    {
        var path = Service().Get("userData");
        Assert.Equal(Path.Combine(_root, "appData", "App"), path);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void DerivedPathsFollowAnOverriddenUserData()
    {
        var service = Service();
        var moved = Path.Combine(_root, "moved");
        service.Set("userData", moved);
        Assert.Equal(moved, service.Get("sessionData"));
        Assert.Equal(Path.Combine(moved, "Crashpad"), service.Get("crashDumps"));
        Assert.True(Directory.Exists(Path.Combine(moved, "Crashpad")));
        Assert.Equal(Path.Combine(moved, "logs"), service.Get("logs"));
    }

    [Fact]
    public void LogsOnMacAreUnderHomeLibraryLogs()
        => Assert.Equal(Path.Combine(_root, "home", "Library", "Logs", "App"), Service(mac: true).Get("logs"));

    [Fact]
    public void UserCacheIsCacheAndTheNameAndUserDesktopIsDesktop()
    {
        var service = Service();
        Assert.Equal(Path.Combine(_root, "cache", "App"), service.Get("userCache"));
        Assert.True(Directory.Exists(Path.Combine(_root, "cache", "App")));
        Assert.Equal(Path.Combine(_root, "Desktop"), service.Get("userDesktop"));
        Assert.False(Directory.Exists(Path.Combine(_root, "Desktop")));
    }

    [Fact]
    public void RecentIsMadeOnWindows()
    {
        Assert.Equal(Path.Combine(_root, "Recent"), Service(windows: true).Get("recent"));
        Assert.True(Directory.Exists(Path.Combine(_root, "Recent")));
    }

    [Fact]
    public void AnswersAreCachedUntilAnOverride()
    {
        var service = Service();
        var first = service.Get("userData");
        _name = "Renamed";
        Assert.Equal(first, service.Get("userData"));
        service.Set("music", Path.Combine(_root, "elsewhere"));
        Assert.Equal(Path.Combine(_root, "appData", "Renamed"), service.Get("userData"));
    }

    [Fact]
    public void SetAcceptsADirectoryThatDoesNotExistAndDoesNotMakeIt()
    {
        var service = Service();
        var missing = Path.Combine(_root, "no-such-dir");
        service.Set("music", missing);
        Assert.Equal(missing, service.Get("music"));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void SetRefusesARelativePathBeforeAnUnknownName()
    {
        var service = Service();
        Assert.Equal("Path must be absolute (Parameter 'path')", Assert.Throws<ArgumentException>(() => service.Set("nope", "relative")).Message);
        Assert.Equal("Failed to set path (Parameter 'name')", Assert.Throws<ArgumentException>(() => service.Set("nope", _root)).Message);
    }

    [Fact]
    public void GetRefusesUnknownNamesAndThoseThisOsLacks()
    {
        Assert.StartsWith("Failed to get 'nope' path", Assert.Throws<ArgumentException>(() => Service().Get("nope")).Message);
        Assert.StartsWith("Failed to get 'assets' path", Assert.Throws<ArgumentException>(() => Service(mac: true).Get("assets")).Message);
        Assert.StartsWith("Failed to get 'recent' path", Assert.Throws<ArgumentException>(() => Service().Get("recent")).Message);
        Assert.Throws<ArgumentException>(() => Service(mac: true).Set("assets", _root));
    }

    [Fact]
    public void AKnownNameWithNoAnswerFails()
        => Assert.StartsWith("Failed to get 'videos' path", Assert.Throws<ArgumentException>(() => Service().Get("videos")).Message);

    [Fact]
    public void ADirectoryThatCannotBeMadeFails()
    {
        // A file where the app's directory should go.
        Directory.CreateDirectory(Path.Combine(_root, "appData"));
        File.WriteAllText(Path.Combine(_root, "appData", "App"), "");
        Assert.Throws<ArgumentException>(() => Service().Get("userData"));
    }

    [Fact]
    public void OverrideMakesTheDirectoryAndResolvesIt()
    {
        var service = Service();
        var logs = Path.Combine(_root, "made", "logs");
        Assert.True(service.Override("logs", logs));
        Assert.True(Directory.Exists(logs));
        Assert.Equal(RiftwayApp.RealPath(logs), service.Get("logs"));
    }

    [Fact]
    public void OverrideThatCannotMakeTheDirectoryKeepsTheOldAnswer()
    {
        var service = Service();
        var before = service.Get("logs");
        File.WriteAllText(Path.Combine(_root, "file"), "");
        Assert.False(service.Override("logs", Path.Combine(_root, "file", "logs")));
        Assert.Equal(before, service.Get("logs"));
    }

    [Fact]
    public void IsPathName_FollowsElectronsTable()
    {
        Assert.True(AppPathService.IsPathName("assets", mac: false, windows: false));
        Assert.False(AppPathService.IsPathName("assets", mac: true, windows: false));
        Assert.True(AppPathService.IsPathName("recent", mac: false, windows: true));
        Assert.False(AppPathService.IsPathName("recent", mac: false, windows: false));
        Assert.True(AppPathService.IsPathName("userCache", mac: true, windows: false));
        Assert.False(AppPathService.IsPathName("Home", mac: false, windows: false));
    }

    [Fact]
    public void XdgDirectory_TakesAnyValueAndTrimsSeparators()
    {
        Assert.Equal("/x/cache", RiftwayApp.XdgDirectory("/x/cache//", "/home/u", ".cache"));
        Assert.Equal("relative", RiftwayApp.XdgDirectory("relative", "/home/u", ".cache"));
        Assert.Equal(Path.Combine("/home/u", ".cache"), RiftwayApp.XdgDirectory("", "/home/u", ".cache"));
        Assert.Equal("/", RiftwayApp.XdgDirectory("///", "/home/u", ".cache"));
    }

    [Fact]
    public void MacLogsDirectory_PrintsAMissingBundleNameAsNull()
        => Assert.EndsWith(Path.Combine("Library", "Logs", "(null)"), RiftwayApp.MacLogsDirectory(null));
}
