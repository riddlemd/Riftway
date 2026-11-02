using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class MacNotificationRuleTests
{
    [Fact]
    public void ActionString_IsCocoaNotifications()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal("REPLY_Reply_Say hiACTION_0_OkACTION_1_ShowCLOSE_Nope", MacNotifications.ActionString(true, "Say hi", ["Ok", ""], "Nope"));
        Assert.Equal("", MacNotifications.ActionString(false, "ignored", [], ""));
        Assert.Equal("CLOSE_X", MacNotifications.ActionString(false, "", [], "X"));
    }

    [Fact]
    public void CategoryIdentifier_IsTheActionStringsHash()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var a = MacNotifications.CategoryIdentifier("ACTION_0_Ok");
        Assert.Matches("^CATEGORY_[0-9]+$", a);
        Assert.Equal(a, MacNotifications.CategoryIdentifier("ACTION_0_Ok"));
        Assert.NotEqual(a, MacNotifications.CategoryIdentifier("ACTION_0_No"));
    }

    [Theory]
    [InlineData("com.apple.UNNotificationDefaultActionIdentifier", "", false, "", MacNotificationResponseKind.Click, 0, "")]
    [InlineData("com.apple.UNNotificationDismissActionIdentifier", "", false, "", MacNotificationResponseKind.Dismiss, 0, "")]
    [InlineData("REPLY_ACTION", "CATEGORY_1", true, "hey", MacNotificationResponseKind.Reply, 0, "hey")]
    [InlineData("REPLY_ACTION", "CATEGORY_1", false, "", MacNotificationResponseKind.None, 0, "")]
    [InlineData("ACTION_3", "CATEGORY_1", false, "", MacNotificationResponseKind.Action, 3, "")]
    [InlineData("ACTION_3", "OTHER", false, "", MacNotificationResponseKind.None, 0, "")]
    [InlineData("CLOSE_ACTION", "CATEGORY_1", false, "", MacNotificationResponseKind.Dismiss, 0, "")]
    [InlineData("SOMETHING", "CATEGORY_1", false, "", MacNotificationResponseKind.None, 0, "")]
    public void ParseResponse_IsTheDelegates(string action, string category, bool textInput, string text, MacNotificationResponseKind kind, int index, string reply)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(new MacNotificationResponse(kind, index, reply), MacNotifications.ParseResponse(action, category, textInput, text));
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData(" 12x", 12)]
    [InlineData("x", 0)]
    [InlineData("", 0)]
    [InlineData("-4", -4)]
    [InlineData("+5", 5)]
    [InlineData("99999999999", 1215752191)]
    [InlineData("99999999999999999999", -1)]
    [InlineData("-99999999999999999999", 0)]
    public void IntegerValue_IsNSStringsCastToInt(string text, int expected)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(expected, MacNotifications.IntegerValue(text));
    }
}

[Collection("Notification")]
public sealed class MacNotificationBackendTests : IDisposable
{
    private readonly INotificationBackend _saved = Notification.Backend;
    private readonly Func<bool> _savedReady = Notification.IsAppReady;
    private readonly FakeCenter _center = new();
    private readonly List<Action> _queue = [];
    private readonly List<string> _events = [];

    public MacNotificationBackendTests()
    {
        Notification.IsAppReady = () => true;
        Notification.Backend = new MacNotificationBackend(_center, a => { lock (_queue) _queue.Add(a); });
    }

    public void Dispose()
    {
        Notification.Backend = _saved;
        Notification.IsAppReady = _savedReady;
    }

    private Notification Watched(string id = "n1")
    {
        var n = new Notification { Id = id, Title = "T" };
        n.Shown += (_, _) => _events.Add("show");
        n.Click += (_, _) => _events.Add("click");
        n.Closed += (_, r) => _events.Add($"close:{r}");
        n.Failed += (_, e) => _events.Add($"failed:{e}");
        n.Action += (_, a) => _events.Add($"action:{a.ActionIndex}:{a.SelectionIndex}");
        n.Reply += (_, r) => _events.Add($"reply:{r}");
        return n;
    }

    private void Drain()
    {
        while (true)
        {
            Action next;
            lock (_queue)
            {
                if (_queue.Count == 0) return;
                next = _queue[0];
                _queue.RemoveAt(0);
            }
            next();
        }
    }

    // The icon's continuation posts from the thread pool.
    private void DrainWhenPosted()
    {
        Assert.True(SpinWait.SpinUntil(() => { lock (_queue) return _queue.Count > 0; }, 5000));
        Drain();
    }

    private static NativeImage Icon() => NativeImage.CreateFromBitmap([1, 2, 3, 255], new NativeImageBitmapOptions { Width = 1, Height = 1 });

    [Fact]
    public void AnIconIsWrittenFirstAndAttached()
    {
        var png = new TaskCompletionSource<string?>();
        _center.Png = png.Task;
        var n = Watched();
        n.IconImage = Icon();
        n.Show();
        Assert.Empty(_center.Added);
        Assert.Single(_center.Images);
        png.SetResult("/tmp/icon.png");
        DrainWhenPosted();
        Assert.Equal("/tmp/icon.png", Assert.Single(_center.Added).AttachmentPngPath);
    }

    [Fact]
    public void AnIconThatCannotBeWrittenIsLeftOut()
    {
        _center.Png = Task.FromException<string?>(new IOException("disk"));
        var n = Watched();
        n.IconImage = Icon();
        n.Show();
        DrainWhenPosted();
        Assert.Null(Assert.Single(_center.Added).AttachmentPngPath);
    }

    [Fact]
    public void ANotificationClosedBeforeItsIconIsWrittenIsNeverAdded()
    {
        var png = new TaskCompletionSource<string?>();
        _center.Png = png.Task;
        var n = Watched();
        n.IconImage = Icon();
        n.Show();
        n.Close();
        png.SetResult("/tmp/icon.png");
        DrainWhenPosted();
        Assert.Empty(_center.Added);
    }

    [Fact]
    public void AnEmptyIconIsNoIcon()
    {
        var n = Watched();
        n.IconImage = NativeImage.CreateEmpty();
        n.Show();
        Assert.Single(_center.Added);
        Assert.Empty(_center.Images);
    }

    [Fact]
    public async Task HistoryIsTheDeliveredNotificationsRestored()
    {
        _center.Delivered = [new MacDeliveredNotification("h1", "T", "S", "B", "g"), new MacDeliveredNotification("h2", "", "", "", "")];
        var history = Notification.GetHistoryAsync();
        Assert.False(history.IsCompleted);
        Drain();
        var list = await history;
        Assert.Equal(["h1", "h2"], list.Select(n => n.Id));
        var first = list[0];
        Assert.Equal(("T", "S", "B", "g"), (first.Title, first.Subtitle, first.Body, first.GroupId));
        Assert.True(first.IsShown);
        Assert.Single(_center.Prepares);
    }

    [Fact]
    public async Task ARestoredNotificationAnswersButIsNeverShownOrRemoved()
    {
        _center.Delivered = [new MacDeliveredNotification("h1", "T", "", "", "")];
        var history = Notification.GetHistoryAsync();
        Drain();
        var restored = (await history)[0];
        var events = new List<string>();
        restored.Click += (_, _) => events.Add("click");
        restored.Closed += (_, r) => events.Add($"close:{r}");
        restored.Show();
        Assert.Empty(_center.Added);
        restored.Close();
        Assert.Equal(["close:ApplicationHidden"], events);
        Assert.Empty(_center.Removed);

        var again = Notification.GetHistoryAsync();
        Drain();
        var second = (await again)[0];
        second.Click += (_, _) => events.Add("click");
        Respond("h1", MacNotificationResponseKind.Click);
        Assert.Equal(["close:ApplicationHidden", "click"], events);
        Assert.Empty(_center.Removed);
    }

    [Fact]
    public async Task WithoutABundleHistoryIsEmptyAndRemovingDoesNothing()
    {
        _center.HasBundle = false;
        Assert.Empty(await Notification.GetHistoryAsync());
        Notification.Remove("a");
        Notification.RemoveAll();
        Notification.RemoveGroup("g");
        Assert.Empty(_center.Removed);
        Assert.False(_center.RemovedAll);
        Assert.Equal(0, _center.GetDeliveredCalls);
    }

    [Fact]
    public void RemoveTakesOneOrManyIdentifiers()
    {
        Notification.Remove("a");
        Notification.Remove(["b", "c"]);
        Assert.Equal(["a", "b", "c"], _center.Removed);
        Notification.RemoveAll();
        Assert.True(_center.RemovedAll);
    }

    [Fact]
    public void RemoveGroupRemovesTheDeliveredNotificationsOfThatThread()
    {
        _center.Delivered = [new("a", "", "", "", "g"), new("b", "", "", "", "other"), new("c", "", "", "", "g")];
        Notification.RemoveGroup("g");
        Assert.Equal(["a", "c"], _center.Removed);
        _center.Removed.Clear();
        Notification.RemoveGroup("none");
        Assert.Empty(_center.Removed);
        Assert.Equal(1, _center.RemoveCalls);
    }

    [Fact]
    public void ShowIsRaisedOnlyOnceTheCenterHasTheRequest()
    {
        var n = Watched();
        n.Show();
        Drain();
        Assert.Empty(_events);
        Assert.Single(_center.Added);
        Assert.Single(_center.Prepares);
        _center.Done[0](null);
        Assert.Empty(_events);
        Drain();
        Assert.Equal(["show"], _events);
        Assert.True(n.IsShown);
    }

    [Fact]
    public void ARefusedRequestFailsAndEnds()
    {
        var n = Watched();
        n.Show();
        _center.Done[0]("no permission");
        Drain();
        Assert.Equal(["failed:no permission"], _events);
        Assert.False(n.IsShown);
        Assert.Equal(["n1"], _center.Removed);
    }

    [Fact]
    public void CloseRaisesCloseAndAnAnswerAfterItIsDropped()
    {
        var n = Watched();
        var removedAtClose = -1;
        n.Closed += (_, _) => removedAtClose = _center.Removed.Count;
        n.Show();
        n.Close();
        Assert.Equal(["close:ApplicationHidden"], _events);
        Assert.Equal(1, removedAtClose);
        Assert.Contains("n1", _center.Removed);
        _center.Done[0](null);
        Drain();
        Assert.Equal(["close:ApplicationHidden"], _events);
        Assert.False(n.IsShown);
    }

    [Fact]
    public void AnAnswerForAnEarlierShowDoesNotSpeakForTheNextOne()
    {
        var n = Watched();
        n.Show();
        n.Show();
        _events.Clear();
        _center.Done[0]("stale");
        Drain();
        Assert.Empty(_events);
        Assert.True(n.IsShown);
        _center.Done[1](null);
        Drain();
        Assert.Equal(["show"], _events);
    }

    [Fact]
    public void AClickEndsItAndTakesItOutOfNotificationCenter()
    {
        var n = Watched();
        n.Show();
        Respond("n1", MacNotificationResponseKind.Click);
        Assert.Equal(["click"], _events);
        Assert.False(n.IsShown);
        Assert.Equal(["n1"], _center.Removed);
        Respond("n1", MacNotificationResponseKind.Click);
        Assert.Equal(["click"], _events);
    }

    [Fact]
    public void ADismissalClosesButKeepsItSoCloseClosesAgain()
    {
        var n = Watched();
        n.Show();
        Respond("n1", MacNotificationResponseKind.Dismiss);
        Assert.Equal(["close:UserCanceled"], _events);
        Assert.Empty(_center.Removed);
        n.Close();
        Assert.Equal(["close:UserCanceled", "close:ApplicationHidden"], _events);
        Assert.Contains("n1", _center.Removed);
    }

    [Fact]
    public void ActionsAndRepliesAreReportedAndKeepIt()
    {
        var n = Watched();
        n.Show();
        Respond("n1", MacNotificationResponseKind.Action, index: 2);
        Respond("n1", MacNotificationResponseKind.Reply, reply: "hi");
        Respond("n1", MacNotificationResponseKind.None);
        Assert.Equal(["action:2:-1", "reply:hi"], _events);
        Assert.True(n.IsShown);
    }

    [Fact]
    public void AResponseAfterCloseIsDropped()
    {
        var n = Watched();
        n.Show();
        n.Close();
        _events.Clear();
        Respond("n1", MacNotificationResponseKind.Click);
        Respond("n1", MacNotificationResponseKind.Reply, reply: "late");
        Assert.Empty(_events);
        // Dismiss clears the request, so the release that follows does not remove it a second time.
        Assert.Equal(["n1"], _center.Removed);
    }

    [Fact]
    public void AResponseWhileTheIconIsWrittenIsDropped_AndClosingThenRemovesNothing()
    {
        _center.Png = new TaskCompletionSource<string?>().Task;
        var n = Watched();
        n.IconImage = Icon();
        n.Show();
        Respond("n1", MacNotificationResponseKind.Click);
        Assert.Empty(_events);
        n.Close();
        Assert.Empty(_center.Removed);
    }

    [Fact]
    public void AResponseGoesToTheFirstLiveNotificationWithItsIdentifier()
    {
        var first = Watched("same");
        var second = new Notification { Id = "same" };
        var secondClicked = false;
        second.Click += (_, _) => secondClicked = true;
        first.Show();
        second.Show();
        Respond("other", MacNotificationResponseKind.Click);
        Assert.Empty(_events);
        Respond("same", MacNotificationResponseKind.Click);
        Assert.Equal(["click"], _events);
        Assert.False(secondClicked);
        Respond("same", MacNotificationResponseKind.Click);
        Assert.True(secondClicked);
    }

    [Fact]
    public void TheCenterIsPreparedOnce()
    {
        Watched("a").Show();
        Watched("b").Show();
        Assert.True(Notification.IsSupported());
        Assert.Single(_center.Prepares);
    }

    [Fact]
    public void WithoutABundleIdentifierNothingReachesTheCenter()
    {
        _center.HasBundle = false;
        Assert.False(Notification.IsSupported());
        Assert.Throws<PlatformNotSupportedException>(() => Watched().Show());
        Assert.Empty(_center.Prepares);
        Assert.Empty(_center.Added);
    }

    [Fact]
    public void TheContentIsWhatCocoaNotificationReads()
    {
        var n = new Notification
        {
            Id = "x", GroupId = "g", Title = "T", Subtitle = "S", Body = "B", Silent = true, Sound = "Ping", HasReply = true,
            ReplyPlaceholder = "P", Actions = [new NotificationAction { Text = "A" }, new NotificationAction { Type = "selection", Text = "" }],
            CloseButtonText = "C",
        };
        var c = MacNotificationBackend.Content(n);
        Assert.Equal(new MacNotificationContent("x", "T", "S", "B", "g", true, "Ping", true, "P", c.ActionTexts, "C", null), c);
        Assert.Equal(["A", ""], c.ActionTexts);
    }

    private void Respond(string id, MacNotificationResponseKind kind, int index = 0, string reply = "")
    {
        _center.Prepares[0](id, new MacNotificationResponse(kind, index, reply));
        Drain();
    }

    private sealed class FakeCenter : IMacUserNotificationCenter
    {
        public bool HasBundle = true;
        public List<Action<string, MacNotificationResponse>> Prepares = [];
        public List<MacNotificationContent> Added = [];
        public List<Action<string?>> Done = [];
        public List<string> Removed = [];
        public int RemoveCalls;
        public bool RemovedAll;
        public int GetDeliveredCalls;
        public List<MacDeliveredNotification> Delivered = [];
        public List<NativeImage> Images = [];
        public Task<string?> Png = Task.FromResult<string?>(null);

        public bool HasBundleIdentifier() => HasBundle;

        public void RemoveAllDelivered() => RemovedAll = true;

        public void GetDelivered(Action<IReadOnlyList<MacDeliveredNotification>> done)
        {
            GetDeliveredCalls++;
            done([.. Delivered]);
        }

        public Task<string?> RegisterImageAsync(NativeImage icon)
        {
            Images.Add(icon);
            return Png;
        }

        public void Prepare(Action<string, MacNotificationResponse> responded) => Prepares.Add(responded);

        public void Add(MacNotificationContent content, Action<string?> done)
        {
            Added.Add(content);
            Done.Add(done);
        }

        public void RemoveDelivered(IReadOnlyList<string> identifiers)
        {
            RemoveCalls++;
            Removed.AddRange(identifiers);
        }
    }
}

[Collection("Notification")]
public sealed class NotificationHistoryElsewhereTests : IDisposable
{
    private readonly INotificationBackend _saved = Notification.Backend;

    public NotificationHistoryElsewhereTests() => Notification.Backend = new UnsupportedNotificationBackend();

    public void Dispose() => Notification.Backend = _saved;

    [Fact]
    public async Task OtherBackendsHaveNoHistoryAndRemoveNothing()
    {
        var history = Notification.GetHistoryAsync();
        Assert.True(history.IsCompleted);
        Assert.Empty(await history);
        Notification.Remove("a");
        Notification.RemoveAll();
        Notification.RemoveGroup("g");
    }
}

public sealed class NotificationImageRetainerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "riftway-retainer-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private NotificationImageRetainer Retainer() => new(_dir, "com.example.app") { Now = () => _now, UseTimer = false };

    [Fact]
    public void APngGoesInAHiddenTemporaryFileNamedForTheBundle()
    {
        var path = Retainer().Register([1, 2, 3]);
        Assert.NotNull(path);
        Assert.Equal(_dir, Path.GetDirectoryName(path));
        Assert.Matches(@"^\.com\.example\.app\.[A-Za-z0-9]{6}$", Path.GetFileName(path));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path!));
    }

    [Fact]
    public void AnEmptyPngIsNoFile()
    {
        Assert.Null(Retainer().Register([]));
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void FilesAreDeletedTwelveSecondsAfterTheyWereWritten()
    {
        var images = Retainer();
        var first = images.Register([1])!;
        _now += TimeSpan.FromSeconds(5);
        var second = images.Register([2])!;
        _now += TimeSpan.FromSeconds(7) - TimeSpan.FromTicks(1);
        images.DeleteExpiredFiles();
        Assert.True(File.Exists(first));
        _now += TimeSpan.FromTicks(1);
        images.DeleteExpiredFiles();
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(1, images.Pending);
        _now += TimeSpan.FromSeconds(5);
        images.DeleteExpiredFiles();
        Assert.False(File.Exists(second));
        Assert.Equal(0, images.Pending);
    }

    [Fact]
    public void CleanupRemovesWhatEarlierSessionsLeft()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "old-dir"));
        File.WriteAllBytes(Path.Combine(_dir, ".old"), [9]);
        var images = Retainer();
        var mine = images.Register([1])!;
        images.CleanupPreviousSessions();
        Assert.Equal([mine], Directory.EnumerateFileSystemEntries(_dir));
    }

    [Fact]
    public void CleanupOfAMissingDirectoryDoesNothing() => Retainer().CleanupPreviousSessions();
}
