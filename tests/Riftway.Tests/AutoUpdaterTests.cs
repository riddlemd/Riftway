namespace Riftway.Tests;

public sealed class AutoUpdaterTests
{
    private sealed class FakeClient(UpdateRelease? release, Exception? checkFails = null) : IUpdateClient
    {
        public List<string> Calls { get; } = [];

        public Task<UpdateRelease?> CheckAsync()
        {
            Calls.Add("check");
            return checkFails is null ? Task.FromResult(release) : Task.FromException<UpdateRelease?>(checkFails);
        }

        public Task DownloadAsync(UpdateRelease r)
        {
            Calls.Add($"download {r.Version}");
            return Task.CompletedTask;
        }

        public void ApplyAfterExit(UpdateRelease r) => Calls.Add($"apply {r.Version}");
    }

    private static (AutoUpdater Updater, List<string> Log) Watched(FakeClient client, List<string>? quits = null)
    {
        var log = new List<string>();
        var updater = new AutoUpdater(url =>
        {
            log.Add($"client {url}");
            return client;
        }, () => (quits ?? log).Add("quit"));
        updater.CheckingForUpdate += (_, _) => log.Add("checking");
        updater.UpdateAvailable += (_, _) => log.Add("available");
        updater.UpdateNotAvailable += (_, _) => log.Add("not-available");
        updater.UpdateDownloaded += (_, e) => log.Add($"downloaded {e.ReleaseName} '{e.ReleaseNotes}' {e.ReleaseDate?.ToString() ?? "no-date"} {e.UpdateUrl}");
        updater.Error += (_, e) => log.Add($"error {e.Message}");
        updater.BeforeQuitForUpdate += (_, _) => log.Add("before-quit");
        return (updater, log);
    }

    [Fact]
    public async Task CheckForUpdates_WithoutAFeed_RaisesElectronsErrorAndFaults()
    {
        var (updater, log) = Watched(new FakeClient(null));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(updater.CheckForUpdatesAsync);
        Assert.Equal("Update URL is not set", e.Message);
        Assert.Equal(["error Update URL is not set"], log);
    }

    [Fact]
    public async Task CheckForUpdates_NothingNewer_RaisesCheckingThenNotAvailable()
    {
        var client = new FakeClient(null);
        var (updater, log) = Watched(client);
        updater.SetFeedUrl("https://feed.test/app");
        await updater.CheckForUpdatesAsync();
        Assert.Equal(["checking", "client https://feed.test/app", "not-available"], log);
        Assert.Equal(["check"], client.Calls);
    }

    [Fact]
    public async Task CheckForUpdates_Newer_DownloadsThenRaisesDownloadedWithItsNotesAndName()
    {
        var client = new FakeClient(new UpdateRelease("2.0.0", "notes", new object()));
        var (updater, log) = Watched(client);
        updater.SetFeedUrl(@"C:\feed");
        await updater.CheckForUpdatesAsync();
        Assert.Equal(["checking", @"client C:\feed", "available", @"downloaded 2.0.0 'notes' no-date C:\feed"], log);
        Assert.Equal(["check", "download 2.0.0"], client.Calls);
    }

    [Fact]
    public async Task CheckForUpdates_FeedFails_RaisesErrorAndFaults()
    {
        var (updater, log) = Watched(new FakeClient(null, new IOException("offline")));
        updater.SetFeedUrl("https://feed.test");
        await Assert.ThrowsAsync<IOException>(updater.CheckForUpdatesAsync);
        Assert.Equal(["checking", "client https://feed.test", "error offline"], log);
    }

    [Fact]
    public void QuitAndInstall_BeforeADownload_Throws()
    {
        var (updater, log) = Watched(new FakeClient(null));
        Assert.Throws<InvalidOperationException>(updater.QuitAndInstall);
        Assert.Empty(log);
    }

    [Fact]
    public async Task QuitAndInstall_AfterADownload_RaisesBeforeQuitHandsTheReleaseToTheUpdaterThenQuits()
    {
        var client = new FakeClient(new UpdateRelease("2.0.0", "", new object()));
        var (updater, log) = Watched(client);
        updater.SetFeedUrl("https://feed.test");
        await updater.CheckForUpdatesAsync();
        log.Clear();
        updater.QuitAndInstall();
        Assert.Equal(["before-quit", "quit"], log);
        Assert.Equal(["check", "download 2.0.0", "apply 2.0.0"], client.Calls);
    }

    [Fact]
    public void FeedUrl_IsEmptyUntilSet()
    {
        var (updater, _) = Watched(new FakeClient(null));
        Assert.Equal("", updater.GetFeedUrl());
        updater.SetFeedUrl("https://feed.test");
        Assert.Equal("https://feed.test", updater.GetFeedUrl());
        Assert.Throws<ArgumentException>(() => updater.SetFeedUrl(""));
    }

    [Theory]
    [InlineData("https://github.com/riddlemd/KHost")]
    [InlineData("https://github.com/riddlemd/KHost/")]
    [InlineData("https://github.com/riddlemd/KHost.git")]
    [InlineData("https://GitHub.com/riddlemd/KHost")]
    [InlineData("https://www.github.com/riddlemd/KHost")]
    [InlineData("http://github.com/riddlemd/KHost")]
    [InlineData("https://github.com/riddlemd/KHost?tab=readme-ov-file#install")]
    public void FeedSource_GitHubRepository_ReadsItsReleasesThroughTheGitHubApi(string url)
    {
        var source = Assert.IsType<Velopack.Sources.GithubSource>(VelopackClient.CreateSource(url));
        Assert.Equal(new Uri("https://github.com/riddlemd/KHost"), source.RepoUri);
        Assert.False(source.Prerelease);
    }

    [Theory]
    [InlineData("https://github.com/riddlemd/KHost/releases/latest/download")]
    [InlineData("https://github.com/riddlemd/KHost/releases/download/v1.2.3")]
    [InlineData("https://github.com/riddlemd")]
    [InlineData("https://feed.test/app")]
    [InlineData("https://github.example.com/riddlemd/KHost")]
    public void FeedSource_AnyOtherUrl_IsAPlainWebFeed(string url)
        => Assert.IsType<Velopack.Sources.SimpleWebSource>(VelopackClient.CreateSource(url));

    [Fact]
    public void FeedSource_AFolder_IsAFileFeed()
        => Assert.IsType<Velopack.Sources.SimpleFileSource>(VelopackClient.CreateSource(Path.GetTempPath()));
}
