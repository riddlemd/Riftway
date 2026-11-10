using Velopack;
using Velopack.Sources;

namespace Riftway;

/// <summary>What <see cref="AutoUpdater.UpdateDownloaded"/> carries; Electron's <c>update-downloaded</c> arguments.</summary>
public sealed class UpdateDownloadedEventArgs(string releaseNotes, string releaseName, DateTimeOffset? releaseDate, string updateUrl) : EventArgs
{
    /// <summary>The release's notes (Markdown from the Velopack feed). Electron's <c>releaseNotes</c>.</summary>
    public string ReleaseNotes { get; } = releaseNotes;

    /// <summary>The release's version. Electron's <c>releaseName</c>.</summary>
    public string ReleaseName { get; } = releaseName;

    /// <summary>Always null: a Velopack feed records no release date. Electron's <c>releaseDate</c>.</summary>
    public DateTimeOffset? ReleaseDate { get; } = releaseDate;

    /// <summary>The feed the update came from. Electron's <c>updateURL</c>.</summary>
    public string UpdateUrl { get; } = updateUrl;
}

/// <summary>Updates an app installed by a Velopack installer from its feed: a folder, a web server or GitHub releases
/// (<c>vpk upload</c>). Electron's <c>autoUpdater</c>, whose Windows Squirrel feeds Velopack replaces; on Linux it
/// updates an AppImage too.</summary>
/// <remarks>Call <see cref="HandleInstallerHooks"/> first in <c>Main</c>: the installer runs the app's exe with its
/// hook arguments, and the app must exit before it shows anything.</remarks>
public sealed class AutoUpdater
{
    private readonly Func<string, IUpdateClient> _client;
    private readonly Action _quit;
    private string? _feedUrl;
    private (IUpdateClient Client, UpdateRelease Release)? _downloaded;

    internal AutoUpdater(Func<string, IUpdateClient> client, Action quit)
    {
        _client = client;
        _quit = quit;
    }

    /// <summary>The app's updater. Electron's <c>autoUpdater</c> module.</summary>
    public static AutoUpdater Default { get; } = new(url => new VelopackClient(url), () => RiftwayApp.Current.Quit());

    /// <summary>Runs the installer's hooks when the app was started for one (install, update, uninstall) and exits;
    /// otherwise returns at once. Call it first in <c>Main</c>, before <see cref="RiftwayApp.Create"/>.</summary>
    public static void HandleInstallerHooks() => VelopackApp.Build().Run();

    /// <summary>A check started. Electron's <c>checking-for-update</c>.</summary>
    public event EventHandler? CheckingForUpdate;

    /// <summary>The feed has a newer release, which is now downloading. Electron's <c>update-available</c>.</summary>
    public event EventHandler? UpdateAvailable;

    /// <summary>The feed has nothing newer. Electron's <c>update-not-available</c>.</summary>
    public event EventHandler? UpdateNotAvailable;

    /// <summary>The newer release is downloaded; <see cref="QuitAndInstall"/> installs it. Electron's
    /// <c>update-downloaded</c>.</summary>
    public event EventHandler<UpdateDownloadedEventArgs>? UpdateDownloaded;

    /// <summary>A check or download failed. Electron's <c>error</c>.</summary>
    public event EventHandler<Exception>? Error;

    /// <summary><see cref="QuitAndInstall"/> is about to quit the app. Electron's <c>before-quit-for-update</c>.</summary>
    public event EventHandler? BeforeQuitForUpdate;

    /// <summary>Sets the feed: a folder, an http(s) URL, or a GitHub repository URL. Electron's
    /// <c>autoUpdater.setFeedURL({ url })</c>.</summary>
    public void SetFeedUrl(string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        _feedUrl = url;
    }

    /// <summary>The feed <see cref="SetFeedUrl"/> set, or an empty string. Electron's <c>autoUpdater.getFeedURL()</c>.</summary>
    public string GetFeedUrl() => _feedUrl ?? "";

    /// <summary>Asks the feed for a newer release and downloads it, raising <see cref="CheckingForUpdate"/>, then
    /// <see cref="UpdateNotAvailable"/>, or <see cref="UpdateAvailable"/> and <see cref="UpdateDownloaded"/>; a failure
    /// raises <see cref="Error"/> and faults the task. Electron's <c>autoUpdater.checkForUpdates()</c>.</summary>
    public async Task CheckForUpdatesAsync()
    {
        try
        {
            var url = _feedUrl ?? throw new InvalidOperationException("Update URL is not set");
            CheckingForUpdate?.Invoke(this, EventArgs.Empty);
            var client = _client(url);
            if (await client.CheckAsync().ConfigureAwait(false) is not { } release)
            {
                UpdateNotAvailable?.Invoke(this, EventArgs.Empty);
                return;
            }
            UpdateAvailable?.Invoke(this, EventArgs.Empty);
            await client.DownloadAsync(release).ConfigureAwait(false);
            _downloaded = (client, release);
            UpdateDownloaded?.Invoke(this, new UpdateDownloadedEventArgs(release.Notes, release.Version, null, url));
        }
        catch (Exception e)
        {
            Error?.Invoke(this, e);
            throw;
        }
    }

    /// <summary>Quits the app, installs the downloaded update and starts the new version. Electron's
    /// <c>autoUpdater.quitAndInstall()</c>.</summary>
    /// <exception cref="InvalidOperationException">No update has been downloaded.</exception>
    public void QuitAndInstall()
    {
        if (_downloaded is not var (client, release)) throw new InvalidOperationException("No update has been downloaded: wait for UpdateDownloaded.");
        BeforeQuitForUpdate?.Invoke(this, EventArgs.Empty);
        // Velopack's updater waits for this process to exit, then replaces the install and restarts it.
        client.ApplyAfterExit(release);
        _quit();
    }
}

/// <summary>A release a feed offers.</summary>
internal sealed record UpdateRelease(string Version, string Notes, object Handle);

/// <summary>The feed operations <see cref="AutoUpdater"/> needs, so tests can stand in for Velopack.</summary>
internal interface IUpdateClient
{
    Task<UpdateRelease?> CheckAsync();
    Task DownloadAsync(UpdateRelease release);
    void ApplyAfterExit(UpdateRelease release);
}

internal sealed class VelopackClient(string url) : IUpdateClient
{
    private readonly UpdateManager _manager = new(CreateSource(url));

    /// <summary>The Velopack source for a feed: a GitHub repository URL (<c>https://github.com/owner/repo</c>) reads its
    /// published releases through the GitHub API, as <c>vpk upload github</c> lays them out; any other http(s) URL is a
    /// plain web feed and anything else a folder, as <c>new UpdateManager(urlOrPath)</c> decides.</summary>
    internal static IUpdateSource CreateSource(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new SimpleFileSource(new DirectoryInfo(url));
        return GitHubRepository(uri) is { } repo ? new GithubSource(repo, accessToken: null, prerelease: false) : new SimpleWebSource(uri);
    }

    /// <summary><c>https://github.com/owner/repo</c> for a repository's own URL (any case, <c>www.</c>, a trailing slash
    /// or <c>.git</c>), or null for any other URL, such as a <c>releases/latest/download</c> folder.</summary>
    private static string? GitHubRepository(Uri uri)
    {
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase))
            return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return repo.Length == 0 ? null : $"https://github.com/{parts[0]}/{repo}";
    }

    public async Task<UpdateRelease?> CheckAsync()
        => await _manager.CheckForUpdatesAsync().ConfigureAwait(false) is { } info
            ? new UpdateRelease(info.TargetFullRelease.Version.ToString(), info.TargetFullRelease.NotesMarkdown ?? "", info)
            : null;

    public Task DownloadAsync(UpdateRelease release) => _manager.DownloadUpdatesAsync((UpdateInfo)release.Handle);

    public void ApplyAfterExit(UpdateRelease release)
        => _manager.WaitExitThenApplyUpdates(((UpdateInfo)release.Handle).TargetFullRelease, silent: false, restart: true);
}
