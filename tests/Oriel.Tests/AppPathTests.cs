namespace Oriel.Tests;

[Collection("Environment")]
public sealed class AppPathTests
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void DefaultPaths_FollowChromium()
    {
        Assert.Equal(Home, OrielApp.DefaultPath("home", "App"));
        Assert.Equal(Path.Combine(OrielApp.AppDataDirectory(), "App"), OrielApp.DefaultPath("userData", "App"));
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), OrielApp.DefaultPath("temp", "App"));
        Assert.Equal(Environment.ProcessPath, OrielApp.DefaultPath("exe", "App"));
        Assert.Equal(Path.Combine(OrielApp.AppDataDirectory(), "App", "Crashpad"), OrielApp.DefaultPath("crashDumps", "App"));
        Assert.Null(OrielApp.DefaultPath("nope", "App"));
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(Path.Combine(Home, "Library", "Application Support"), OrielApp.DefaultPath("appData", "App"));
            Assert.Equal(Path.Combine(Home, "Movies"), OrielApp.DefaultPath("videos", "App"));
            Assert.Equal(Path.Combine(Home, "Downloads"), OrielApp.DefaultPath("downloads", "App"));
            Assert.Equal(Path.Combine(Home, "Library", "Logs", "App"), OrielApp.DefaultPath("logs", "App"));
            Assert.Null(OrielApp.DefaultPath("recent", "App"));
        }
    }

    [Fact]
    public void XdgUserDirectory_ReadsUserDirsLikeXdgUserDir()
    {
        var config = Directory.CreateTempSubdirectory("oriel-xdg-").FullName;
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Assert.Null(OrielApp.XdgUserDirectory("DOWNLOAD", "/home/u"));
            File.WriteAllLines(Path.Combine(config, "user-dirs.dirs"),
            [
                "# comment",
                "XDG_DOWNLOAD_DIR=\"$HOME/Fetched\"",
                "XDG_MUSIC_DIR=\"/srv/music/\"",
                "XDG_VIDEOS_DIR=\"relative\"",
                "XDG_PICTURES_DIR=//srv/pics//",
                "XDG_DESKTOP_DIR=\"$HOME/First\"",
                "XDG_DESKTOP_DIR=\"$HOME/Last\"",
            ]);
            Assert.Equal("/home/u/Fetched", OrielApp.XdgUserDirectory("DOWNLOAD", "/home/u"));
            Assert.Equal("/srv/music", OrielApp.XdgUserDirectory("MUSIC", "/home/u"));
            Assert.Null(OrielApp.XdgUserDirectory("VIDEOS", "/home/u"));
            Assert.Null(OrielApp.XdgUserDirectory("PICTURES", "/home/u"));
            Assert.Equal("/home/u/Last", OrielApp.XdgUserDirectory("DESKTOP", "/home/u"));
            Assert.Null(OrielApp.XdgUserDirectory("DOCUMENTS", "/home/u"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            Directory.Delete(config, recursive: true);
        }
    }
}
