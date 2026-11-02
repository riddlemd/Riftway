using System.Runtime.Versioning;
using Riftway.Cef;
using Riftway.Cef.Platform;

namespace Riftway;

/// <summary>The <c>UNUserNotificationCenter</c> calls <see cref="MacNotificationBackend"/> makes; a seam for tests.</summary>
internal interface IMacUserNotificationCenter
{
    bool HasBundleIdentifier();
    void Prepare(Action<string, MacNotificationResponse> responded);
    void Add(MacNotificationContent content, Action<string?> done);
    void RemoveDelivered(IReadOnlyList<string> identifiers);
    void RemoveAllDelivered();
    void GetDelivered(Action<IReadOnlyList<MacDeliveredNotification>> done);

    /// <summary>The icon's 1x PNG written for an attachment, off the UI thread; null when there is none.</summary>
    Task<string?> RegisterImageAsync(NativeImage icon);
}

[SupportedOSPlatform("macos")]
internal sealed class MacUserNotificationCenter : IMacUserNotificationCenter
{
    public bool HasBundleIdentifier() => MacNotifications.HasBundleIdentifier();

    public void Prepare(Action<string, MacNotificationResponse> responded)
    {
        MacNotifications.Responded = responded;
        MacNotifications.Prepare();
    }

    public void Add(MacNotificationContent content, Action<string?> done) => MacNotifications.Add(content, done);

    public void RemoveDelivered(IReadOnlyList<string> identifiers) => MacNotifications.RemoveDelivered(identifiers);

    public void RemoveAllDelivered() => MacNotifications.RemoveAllDelivered();

    public void GetDelivered(Action<IReadOnlyList<MacDeliveredNotification>> done) => MacNotifications.GetDelivered(done);

    private NotificationImageRetainer? _images;

    // The presenter's image_retainer_ and image_task_runner_: userData's "Notification Resources", cleared of earlier
    // sessions' files as the presenter starts.
    public Task<string?> RegisterImageAsync(NativeImage icon)
    {
        var images = _images ??= StartRetainer();
        return Task.Run(() => images.Register(icon.ToPng()));
    }

    private static NotificationImageRetainer StartRetainer()
    {
        var directory = Path.Combine(RiftwayApp.CurrentOrNull?.GetPath("userData") ?? Path.GetTempPath(), "Notification Resources");
        var images = new NotificationImageRetainer(directory, MacNotifications.BundleIdentifier() ?? "");
        _ = Task.Run(images.CleanupPreviousSessions);
        return images;
    }
}

/// <summary>Chromium's <c>NotificationImageRetainer</c>: PNGs in one directory, each deleted about 12 seconds after it was
/// written (the attachment has copied it by then); files no registration of this session made are left from earlier ones.</summary>
internal sealed class NotificationImageRetainer(string directory, string bundleId)
{
    internal static readonly TimeSpan DeletionDelay = TimeSpan.FromSeconds(12);
    private const string NameCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private readonly Lock _lock = new();
    // registered_images_: names in the order written, with when.
    private readonly List<(string Name, DateTime At)> _registered = [];
    private Timer? _timer;

    internal string Directory => directory;

    /// <summary>Files written and not yet deleted; for tests.</summary>
    internal int Pending
    {
        get { lock (_lock) return _registered.Count; }
    }

    internal Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;

    // Whether Register starts the repeating deletion timer; off for tests, which call DeleteExpiredFiles.
    internal bool UseTimer { get; init; } = true;

    /// <summary><c>RegisterTemporaryImage</c>: the PNG in a new temporary file, or null when it is empty or cannot be
    /// written (a file that was made is still deleted later).</summary>
    internal string? Register(byte[] png)
    {
        if (png.Length == 0) return null;
        try { System.IO.Directory.CreateDirectory(directory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (CreateTemporaryFile() is not { } path) return null;
        lock (_lock)
        {
            _registered.Add((Path.GetFileName(path), Now()));
            if (UseTimer) _timer ??= new Timer(_ => DeleteExpiredFiles(), null, DeletionDelay, DeletionDelay);
        }
        try
        {
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary><c>CleanupFilesFromPrevSessions</c>: everything in the directory this session did not write.</summary>
    internal void CleanupPreviousSessions()
    {
        HashSet<string> mine;
        lock (_lock) mine = [.. _registered.Select(r => r.Name)];
        if (!System.IO.Directory.Exists(directory)) return;
        foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
            if (!mine.Contains(Path.GetFileName(entry))) Delete(entry);
    }

    /// <summary><c>DeleteExpiredFiles</c>: the files written at least <see cref="DeletionDelay"/> ago; the timer stops
    /// once none are left.</summary>
    internal void DeleteExpiredFiles()
    {
        List<string> expired;
        lock (_lock)
        {
            var then = Now() - DeletionDelay;
            var end = _registered.FindIndex(r => r.At > then);
            if (end < 0) end = _registered.Count;
            expired = [.. _registered.Take(end).Select(r => Path.Combine(directory, r.Name))];
            _registered.RemoveRange(0, end);
            if (_registered.Count == 0)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
        foreach (var path in expired) Delete(path);
    }

    // base::CreateTemporaryFileInDir on Apple: a hidden ".<bundle id>.XXXXXX" made exclusively, as mkstemp does.
    private string? CreateTemporaryFile()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var suffix = string.Create(6, 0, (span, _) =>
            {
                for (var i = 0; i < span.Length; i++) span[i] = NameCharacters[Random.Shared.Next(NameCharacters.Length)];
            });
            var path = Path.Combine(directory, (bundleId.Length > 0 ? "." + bundleId + "." : ".") + suffix);
            try
            {
                using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                return path;
            }
            catch (IOException) when (File.Exists(path)) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    private static void Delete(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Electron's <c>NotificationPresenterMac</c> and <c>CocoaNotification</c>: <c>show</c> or <c>failed</c> once the
/// center has the request; a click ends the notification and takes it out of Notification Center, a dismissal or the
/// close button raises <c>close</c> and keeps it; <see cref="Notification.Close"/> takes it out and raises <c>close</c>.</summary>
internal sealed class MacNotificationBackend : INotificationBackend
{
    private readonly IMacUserNotificationCenter _center;
    private readonly Action<Action> _post;
    // Electron's presenter notifications, in the order shown; a response goes to the first with its identifier.
    private readonly List<Handle> _live = [];
    private bool _prepared;

    private sealed class Handle(INotificationSink sink, string id)
    {
        public readonly INotificationSink Sink = sink;
        public readonly string Id = id;
        public bool Restored;
        public bool Released;
        // Electron's notification_request_: set when scheduled or restored, cleared by Dismiss; responses and removals
        // need it.
        public bool HasRequest;
    }

    [SupportedOSPlatform("macos")]
    public MacNotificationBackend() : this(new MacUserNotificationCenter(), CefThread.PostUi) { }

    internal MacNotificationBackend(IMacUserNotificationCenter center, Action<Action> post) => (_center, _post) = (center, post);

    /// <summary>Electron's is always true; without a bundle identifier the center would abort the process.</summary>
    public bool IsSupported => _center.HasBundleIdentifier();

    public bool PostsShowEvents => true;

    public bool KeepsDismissed => true;

    public bool ReportsShown => true;

    private readonly Lock _prepareLock = new();

    public void Prepare()
    {
        lock (_prepareLock)
        {
            if (_prepared || !IsSupported) return;
            _prepared = true;
        }
        _center.Prepare((id, response) => _post(() => OnResponse(id, response)));
    }

    public void Post(Action action) => _post(action);

    public object Show(Notification notification, INotificationSink sink)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Notifications on macOS need an app bundle with a CFBundleIdentifier.");
        Prepare();
        var handle = new Handle(sink, notification.Id);
        _live.Add(handle);
        var content = Content(notification);
        var icon = notification.IconImage ?? (notification.Icon is { Length: > 0 } path ? NativeImage.CreateFromPath(path) : null);
        if (icon is null || icon.IsEmpty())
        {
            Schedule(handle, content);
            return handle;
        }
        // Electron writes the PNG on its image task runner and schedules only if the notification still exists.
        _center.RegisterImageAsync(icon).ContinueWith(t => _post(() =>
        {
            if (!handle.Released) Schedule(handle, content with { AttachmentPngPath = t.IsCompletedSuccessfully ? t.Result : null });
        }), TaskScheduler.Default);
        return handle;
    }

    // ScheduleNotification. The sink ignores an answer for a notification closed or shown again since.
    private void Schedule(Handle handle, MacNotificationContent content)
    {
        handle.HasRequest = true;
        _center.Add(content, error => _post(() =>
        {
            if (error is null) handle.Sink.Shown();
            else handle.Sink.Failed(error);
        }));
    }

    /// <summary><c>CocoaNotification::Restore</c>: a handle for a notification already in Notification Center, which
    /// answers its responses but is never removed from there on its behalf.</summary>
    public object Restore(Notification notification, INotificationSink sink)
    {
        var handle = new Handle(sink, notification.Id) { Restored = true, HasRequest = true };
        _live.Add(handle);
        return handle;
    }

    public void GetDelivered(Action<IReadOnlyList<DeliveredNotification>> done)
    {
        if (!IsSupported)
        {
            done([]);
            return;
        }
        Prepare();
        _center.GetDelivered(list => _post(() => done([.. list.Select(n => new DeliveredNotification(n.Id, n.Title, n.Subtitle, n.Body, n.GroupId))])));
    }

    public void RemoveDelivered(IReadOnlyList<string> identifiers)
    {
        if (IsSupported) _center.RemoveDelivered(identifiers);
    }

    public void RemoveAllDelivered()
    {
        if (IsSupported) _center.RemoveAllDelivered();
    }

    /// <summary><c>RemoveDeliveredNotificationsByGroupId</c>: the delivered notifications whose thread is the group.</summary>
    public void RemoveDeliveredGroup(string groupId)
    {
        if (!IsSupported) return;
        _center.GetDelivered(list =>
        {
            var ids = list.Where(n => n.GroupId == groupId).Select(n => n.Id).ToList();
            if (ids.Count > 0) _center.RemoveDelivered(ids);
        });
    }

    /// <summary><c>CocoaNotification::Dismiss</c>: out of Notification Center, then <c>close</c>.</summary>
    public void Hide(object handle)
    {
        var h = (Handle)handle;
        if (h.HasRequest && !h.Restored) _center.RemoveDelivered([h.Id]);
        h.Sink.Dismissed(NotificationCloseReason.ApplicationHidden);
        h.HasRequest = false;
    }

    // A user dismissal leaves Electron's notification undismissed, so a later close() dismisses it again.
    public void Remove(object handle) => Hide(handle);

    /// <summary>The <c>CocoaNotification</c> destructor: the request leaves Notification Center.</summary>
    public void Release(object handle)
    {
        var h = (Handle)handle;
        h.Released = true;
        _live.Remove(h);
        if (h.HasRequest && !h.Restored) _center.RemoveDelivered([h.Id]);
    }

    /// <summary>What <c>CocoaNotification::Show</c> reads from the options, before the icon is written.</summary>
    internal static MacNotificationContent Content(Notification n)
        => new(n.Id, n.Title, n.Subtitle, n.Body, n.GroupId, n.Silent, n.Sound, n.HasReply, n.ReplyPlaceholder,
            [.. n.Actions.Select(a => a.Text)], n.CloseButtonText, null);

    // NotificationCenterDelegate's didReceiveNotificationResponse:, on the UI thread.
    private void OnResponse(string id, MacNotificationResponse response)
    {
        if (_live.Find(h => h.HasRequest && h.Id == id) is not { } handle) return;
        switch (response.Kind)
        {
            case MacNotificationResponseKind.Click: handle.Sink.Clicked(); break;
            case MacNotificationResponseKind.Dismiss: handle.Sink.Dismissed(NotificationCloseReason.UserCanceled); break;
            case MacNotificationResponseKind.Reply: handle.Sink.Replied(response.Reply); break;
            case MacNotificationResponseKind.Action: handle.Sink.ActionInvoked(response.ActionIndex); break;
        }
    }
}
