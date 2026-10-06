using System.Runtime.Versioning;
using Microsoft.Win32;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class WinRtIidTests
{
    [Fact]
    // Expected values are the SDK's windows.ui.notifications.h uuids.
    public void ToastHandlerIids_MatchTheSdk()
    {
        Assert.Equal(new Guid("ab54de2d-97d9-5528-b6ad-105afe156530"), WinRtIid.ActivatedHandler);
        Assert.Equal(new Guid("61c2402f-0ed0-5a18-ab69-59f4aa99a368"), WinRtIid.DismissedHandler);
        Assert.Equal(new Guid("95e3e803-c969-5e3a-9753-ea2ad22a9a33"), WinRtIid.FailedHandler);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    // Electron's FailureResultToString: known notification-platform codes get a reason, others only the code.
    public void FailureResultToString_MatchesElectron()
    {
        Assert.Equal("Notification failed - The size of the notification tag is too large. (HRESULT: -2143420138)",
            WindowsToast.FailureResultToString(-2143420138));
        Assert.Equal(" (HRESULT: -1072896685)", WindowsToast.FailureResultToString(-1072896685));
    }
}

// Notification.Backend and IsAppReady are process-wide, so the classes that swap them run one at a time.
[Collection("Notification")]
public sealed class ToastXmlTests : IDisposable
{
    private readonly Func<bool> _savedReady = Notification.IsAppReady;

    public ToastXmlTests() => Notification.IsAppReady = () => true;

    public void Dispose() => Notification.IsAppReady = _savedReady;

    private static string Xml(Notification n, string? icon = null) => Notification.BuildToastXml(n, icon);

    [Fact]
    public void TitleAndBody_TwoTextsAndNoLaunch()
        => Assert.Equal("<toast><visual><binding template=\"ToastGeneric\"><text>Hi</text><text>There</text></binding></visual></toast>",
            Xml(new Notification { Title = "Hi", Body = "There" }));

    [Theory]
    [InlineData("Hi", "", "<text>Hi</text>")]
    [InlineData("", "There", "<text>There</text>")]
    [InlineData("", "", "<text>[no message]</text>")]
    public void OneLineMissing_OneText(string title, string body, string text)
        => Assert.Equal($"<toast><visual><binding template=\"ToastGeneric\">{text}</binding></visual></toast>",
            Xml(new Notification { Title = title, Body = body }));

    [Fact]
    public void Text_IsEscaped()
        => Assert.Contains("<text>a &lt;b&gt; &amp; &quot;c&quot;</text>", Xml(new Notification { Title = "a <b> & \"c\"" }));

    [Fact]
    public void Silent_AddsSilentAudio()
    {
        Assert.EndsWith("<audio silent=\"true\"/></toast>", Xml(new Notification { Silent = true }));
        Assert.DoesNotContain("<audio", Xml(new Notification()));
    }

    [Fact]
    public void TimeoutNever_IsAReminderWithASystemClose()
    {
        var x = Xml(new Notification { Title = "T", TimeoutType = "never" });
        Assert.StartsWith("<toast scenario=\"reminder\"><visual>", x);
        Assert.Contains("</visual><actions><action activationType=\"system\" arguments=\"dismiss\" content=\"Close\"/></actions></toast>", x);
        Assert.DoesNotContain("scenario", Xml(new Notification()));
    }

    [Fact]
    public void GroupIdAndTitle_AddAHeaderBeforeTheVisual()
    {
        Assert.StartsWith("<toast><header id=\"g&amp;1\" title=\"Chat\" arguments=\"\"/><visual>",
            Xml(new Notification { GroupId = "g&1", GroupTitle = "Chat" }));
        Assert.DoesNotContain("<header", Xml(new Notification { GroupId = "g" }));
    }

    [Fact]
    public void Buttons_ActivateInTheForegroundWithTheirIndexAndTag()
        => Assert.Contains("<actions><action activationType=\"foreground\" arguments=\"type=action&amp;action=0&amp;tag=t1\" content=\"Yes\"/>"
            + "<action activationType=\"foreground\" arguments=\"type=action&amp;action=1&amp;tag=t1\" content=\"No &amp; never\"/></actions>",
            Xml(new Notification { Id = "t1", Actions = [new() { Text = "Yes" }, new() { Type = "", Text = "No & never" }] }));

    [Fact]
    public void Selection_IsAnInputAndASubmitButton()
        => Assert.Contains("<actions><input id=\"selection0\" type=\"selection\"><selection id=\"0\" content=\"a\"/><selection id=\"1\" content=\"b\"/></input>"
            + "<action activationType=\"foreground\" arguments=\"type=action&amp;action=0&amp;tag=t\" content=\"Select\" hint-inputId=\"selection0\"/>"
            + "<input id=\"selection1\" type=\"selection\"/>"
            + "<action activationType=\"foreground\" arguments=\"type=action&amp;action=1&amp;tag=t\" content=\"Go\" hint-inputId=\"selection1\"/></actions>",
            Xml(new Notification { Id = "t", Actions = [new() { Type = "selection", Items = ["a", "b"] }, new() { Type = "selection", Text = "Go" }] }));

    [Fact]
    public void UnknownActionType_IsLeftOut()
        => Assert.DoesNotContain("<action ", Xml(new Notification { Actions = [new() { Type = "menu", Text = "X" }] }));

    [Fact]
    public void Reply_IsAnInputFirstAndAReplyButtonLast()
    {
        Assert.Contains("<actions><action activationType=\"system\" arguments=\"dismiss\" content=\"Close\"/>"
            + "<input id=\"reply\" type=\"text\" placeHolderContent=\"Say\"/>"
            + "<action activationType=\"foreground\" arguments=\"type=action&amp;action=0&amp;tag=t\" content=\"B\"/>"
            + "<action activationType=\"foreground\" arguments=\"type=reply&amp;tag=t\" content=\"Reply\" hint-inputId=\"reply\"/></actions>",
            Xml(new Notification { Id = "t", TimeoutType = "never", HasReply = true, ReplyPlaceholder = "Say", Actions = [new() { Text = "B" }] }));
        Assert.Contains("<input id=\"reply\" type=\"text\"/>", Xml(new Notification { HasReply = true }));
    }

    [Fact]
    public void NoActions_NoActionsElement()
        => Assert.DoesNotContain("<actions", Xml(new Notification()));

    [Fact]
    public void Icon_IsAPlainPathLogoWithoutCropping()
    {
        Assert.Contains("<image id=\"1\" placement=\"appLogoOverride\" hint-crop=\"none\" src=\"C:\\tmp\\a &amp; b.png\"/></binding>",
            Xml(new Notification(), @"C:\tmp\a & b.png"));
        Assert.DoesNotContain("<image", Xml(new Notification(), null));
    }
}

[Collection("Notification")]
public sealed class NotificationTests : IDisposable
{
    private readonly INotificationBackend _saved = Notification.Backend;
    private readonly Func<bool> _savedReady = Notification.IsAppReady;
    private readonly FakeBackend _backend = new();
    private readonly List<string> _events = [];

    public NotificationTests()
    {
        Notification.Backend = _backend;
        Notification.IsAppReady = () => true;
        Notification.ResetActivationHandler();
    }

    public void Dispose()
    {
        Notification.Backend = _saved;
        Notification.IsAppReady = _savedReady;
        Notification.ResetActivationHandler();
    }

    private Notification Watched(Notification? n = null)
    {
        n ??= new Notification { Title = "T" };
        n.Shown += (_, _) => _events.Add("shown");
        n.Click += (_, _) => _events.Add("click");
        n.Action += (_, e) => _events.Add($"action {e.ActionIndex} {e.SelectionIndex}");
        n.Reply += (_, r) => _events.Add($"reply {r}");
        n.Closed += (_, r) => _events.Add($"closed {r}");
        n.Failed += (_, e) => _events.Add($"failed {e}");
        return n;
    }

    [Fact]
    public void Show_SendsTheXmlTagAndDefaultGroup()
    {
        var n = Watched();
        n.Show();
        Assert.Equal(Notification.BuildToastXml(n, null), _backend.Xml);
        Assert.Equal(n.Id, _backend.Tag);
        Assert.Equal("Notifications", _backend.Group);
        Assert.False(_backend.HighPriority);
        Assert.True(n.IsShown);
        Assert.Equal(["shown"], _events);
    }

    [Fact]
    public void Show_UsesTheGroupIdAndCriticalUrgency()
    {
        Watched(new Notification { GroupId = "chat", Urgency = "critical" }).Show();
        Assert.Equal("chat", _backend.Group);
        Assert.True(_backend.HighPriority);
        Watched(new Notification { Urgency = "normal" }).Show();
        Assert.False(_backend.HighPriority);
    }

    [Fact]
    public void Id_DefaultsToALowercaseV4Uuid()
    {
        foreach (var id in new[] { new Notification().Id, new Notification { Id = "" }.Id })
        {
            Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", id);
        }
        Assert.NotEqual(new Notification().Id, new Notification().Id);
        Assert.Equal("mine", new Notification { Id = "mine" }.Id);
    }

    [WindowsFact]
    public void IdsOverWindowsLimit_Throw()
    {
        Assert.Equal(new string('a', 64), new Notification { Id = new string('a', 64), GroupId = new string('b', 64) }.Id);
        var id = Assert.Throws<ArgumentException>(() => new Notification { Id = new string('a', 65) });
        Assert.StartsWith("Notification id exceeds Windows limit of 64 UTF-16 characters", id.Message);
        var group = Assert.Throws<ArgumentException>(() => new Notification { GroupId = new string('a', 65) });
        Assert.StartsWith("Notification groupId exceeds Windows limit of 64 UTF-16 characters", group.Message);
    }

    [WindowsFact]
    public void GroupTitleWithoutGroupId_Throws()
    {
        var e = Assert.Throws<InvalidOperationException>(() => new Notification { GroupTitle = "Chat" }.Show());
        Assert.Equal("Notification groupTitle requires groupId to be set", e.Message);
        Assert.Null(_backend.Xml);
    }

    [Fact]
    public void Show_SavesTheIconForTheGeneratedXmlOnly()
    {
        Watched(new Notification { Title = "T", Icon = "in.png" }).Show();
        Assert.Equal(["in.png"], _backend.Icons);
        Assert.Contains("src=\"saved-in.png\"", _backend.Xml);
        Watched(new Notification { Title = "T", Icon = "x.png", ToastXml = "<toast/>" }).Show();
        Assert.Equal(["in.png"], _backend.Icons);
        Assert.Equal("<toast/>", _backend.Xml);
    }

    [Fact]
    public void Show_UnreadableIconLeavesTheImageOut()
    {
        _backend.IconPath = null;
        Watched(new Notification { Title = "T", Icon = "bad.png" }).Show();
        Assert.DoesNotContain("<image", _backend.Xml);
    }

    [Fact]
    public void Constructor_BeforeReadyThrows()
    {
        Notification.IsAppReady = () => false;
        Assert.Equal("Cannot create Notification before app is ready", Assert.Throws<InvalidOperationException>(() => new Notification()).Message);
    }

    [Fact]
    public void Shown_IsPostedAndDroppedByAnEarlierClose()
    {
        _backend.Deferred = true;
        var n = Watched();
        n.Show();
        Assert.Empty(_events);
        _backend.Drain();
        Assert.Equal(["shown"], _events);
        n.Show();
        n.Close();
        _backend.Drain();
        Assert.Equal(["shown"], _events);
    }

    [Fact]
    public void Failed_IsPostedAndDroppedByAnEarlierClose()
    {
        _backend.Deferred = true;
        _backend.Refuse = true;
        var n = Watched();
        n.Show();
        Assert.Empty(_events);
        _backend.Drain();
        Assert.Equal(["failed no"], _events);
        n.Show();
        n.Close();
        _backend.Drain();
        Assert.Equal(["failed no"], _events);
    }

    [Fact]
    public void Show_SavesTheIconImageOverThePath()
    {
        var image = NativeImage.CreateEmpty();
        Watched(new Notification { Title = "T", Icon = "in.png", IconImage = image }).Show();
        Assert.Same(image, _backend.Image);
        Assert.Equal(["in.png"], _backend.Icons);
        Watched(new Notification { Title = "T", IconImage = image }).Show();
        Assert.Equal(["in.png", ""], _backend.Icons);
    }

    [Fact]
    public void Show_RefusedRaisesFailedNotShown()
    {
        _backend.Refuse = true;
        var n = Watched();
        n.Show();
        Assert.False(n.IsShown);
        Assert.Equal(["failed no"], _events);
    }

    [Fact]
    public void ShowAgain_HidesTheFirstWithoutClosed()
    {
        var n = Watched();
        n.Show();
        var first = _backend.Sink;
        n.Show();
        Assert.Equal([1], _backend.Hidden);
        Assert.Equal([1], _backend.Released);
        first!.Dismissed(NotificationCloseReason.ApplicationHidden);
        Assert.Equal(["shown", "shown"], _events);
    }

    [Fact]
    public void Close_HidesReleasesAndRaisesNothing()
    {
        var n = Watched();
        n.Show();
        var sink = _backend.Sink!;
        n.Close();
        Assert.Equal([1], _backend.Hidden);
        Assert.Equal([1], _backend.Released);
        Assert.Empty(_backend.Removed);
        sink.Dismissed(NotificationCloseReason.ApplicationHidden);
        Assert.False(n.IsShown);
        Assert.Equal(["shown"], _events);
    }

    [Fact]
    public void Dismissal_RaisesClosedAndKeepsItForTheActionCenter()
    {
        var n = Watched();
        n.Show();
        _backend.Sink!.Dismissed(NotificationCloseReason.TimedOut);
        Assert.False(n.IsShown);
        Assert.Empty(_backend.Released);
        _backend.Sink!.Dismissed(NotificationCloseReason.UserCanceled);
        Assert.Equal(["shown", "closed TimedOut", "closed UserCanceled"], _events);
        n.Close();
        Assert.Equal([1], _backend.Removed);
        Assert.Empty(_backend.Hidden);
        Assert.Equal([1], _backend.Released);
    }

    [Fact]
    public void Close_WhenNotShown_DoesNothing()
    {
        Watched().Close();
        Assert.Empty(_backend.Hidden);
        Assert.Empty(_backend.Removed);
    }

    [Theory]
    [InlineData("", "click")]
    [InlineData("launch", "click")]
    [InlineData("action=2", "click")]
    public void PlainActivation_IsAClickThatEndsIt(string arguments, string expected)
    {
        var n = Watched();
        n.Show();
        _backend.Sink!.Activated(arguments, []);
        Assert.Equal(["shown", expected], _events);
        Assert.False(n.IsShown);
        Assert.Equal([1], _backend.Released);
    }

    [Fact]
    public void StructuredActivation_ReachesTheNotificationWithTheTag()
    {
        // s_live outlives a test, so tags are unique per test.
        var me = Guid.NewGuid().ToString();
        var n = Watched(new Notification { Id = me });
        var other = new Notification { Id = Guid.NewGuid().ToString() };
        n.Show();
        other.Show();
        _backend.Sinks[0].Activated("type=action&action=2&tag=" + me, []);
        _backend.Sinks[0].Activated("type=action&action=1&tag=" + me, [new("reply", "x"), new("selection1", "3"), new("selection2", "5")]);
        _backend.Sinks[0].Activated("type=reply&tag=" + me, [new("reply", "hello")]);
        _backend.Sinks[0].Activated("type=action&action=0&tag=" + other.Id, []);
        _backend.Sinks[0].Activated("type=action&tag=" + me, []);
        Assert.Equal(["shown", "action 2 -1", "action 1 3", "reply hello"], _events);
        Assert.True(n.IsShown);
    }

    [Theory]
    [InlineData("type=action&action=0&tag=me", "selection0", "-2", "action 0 -1")]
    [InlineData("type=action&action=0&tag=me", "selection0", "x", "action 0 -1")]
    [InlineData("type=click&tag=me", "k", "v", "click")]
    [InlineData("tag=me", "reply", "typed", "reply typed")]
    [InlineData("tag=me", "k", "v", "click")]
    public void Activation_DispatchesAsElectron(string arguments, string key, string value, string expected)
    {
        var me = Guid.NewGuid().ToString();
        var n = Watched(new Notification { Id = me });
        n.Show();
        Notification.HandleToastActivation(arguments.Replace("tag=me", "tag=" + me, StringComparison.Ordinal), [new(key, value)]);
        Assert.Equal(["shown", expected], _events);
    }

    [Fact]
    public void HandleActivation_GetsEveryActivationWithItsDetails()
    {
        var seen = new List<ActivationArguments>();
        Notification.HandleActivation(seen.Add);
        Notification.HandleToastActivation("type=action&action=3&tag=gone", [new("selection3", "1")]);
        Notification.HandleToastActivation("type=reply&tag=gone", [new("reply", "hi")]);
        Notification.HandleToastActivation("tag=gone", []);
        Assert.Equal(3, seen.Count);
        Assert.Equal(("action", "type=action&action=3&tag=gone", 3, (string?)null), (seen[0].Type, seen[0].Arguments, seen[0].ActionIndex, seen[0].Reply));
        Assert.Equal("1", seen[0].UserInputs!["selection3"]);
        Assert.Equal(("reply", (int?)null, "hi"), (seen[1].Type, seen[1].ActionIndex, seen[1].Reply));
        Assert.Equal(("click", (IReadOnlyDictionary<string, string>?)null), (seen[2].Type, seen[2].UserInputs));
    }

    [Fact]
    public void HandleActivation_GetsTheLastActivationBeforeIt()
    {
        Notification.HandleToastActivation("type=action&action=1&tag=a", []);
        Notification.HandleToastActivation("type=action&action=2&tag=a", []);
        var seen = new List<ActivationArguments>();
        Notification.HandleActivation(seen.Add);
        Assert.Equal([2], seen.Select(a => a.ActionIndex ?? -9));
        Notification.HandleActivation(seen.Add);
        Assert.Single(seen);
    }

    [Fact]
    public void Failure_AfterShowRaisesFailedAndReleases()
    {
        var n = Watched();
        n.Show();
        _backend.Sink!.Failed("boom");
        Assert.False(n.IsShown);
        Assert.Equal([1], _backend.Released);
        Assert.Equal(["shown", "failed boom"], _events);
    }

    private sealed class FakeBackend : INotificationBackend
    {
        private int _next;
        public string? Xml, Tag, Group;
        public string? IconPath = "saved-";
        public bool HighPriority, Refuse, Deferred;
        public NativeImage? Image;
        private readonly Queue<Action> _posted = new();

        public void Post(Action action)
        {
            if (Deferred) _posted.Enqueue(action);
            else action();
        }

        public void Drain()
        {
            while (_posted.TryDequeue(out var a)) a();
        }
        public INotificationSink? Sink;
        public List<INotificationSink> Sinks = [];
        public List<int> Hidden = [], Removed = [], Released = [];
        public List<string> Icons = [];

        public bool IsSupported => true;

        public void Prepare() { }

        public string? SaveIcon(string? path, NativeImage? image)
        {
            Icons.Add(path ?? "");
            Image = image;
            return IconPath is null ? null : IconPath + path;
        }

        public object Show(string xml, string tag, string group, bool highPriority, INotificationSink sink)
        {
            if (Refuse) throw new InvalidOperationException("no");
            (Xml, Tag, Group, HighPriority, Sink) = (xml, tag, group, highPriority, sink);
            Sinks.Add(sink);
            return ++_next;
        }

        public void Hide(object handle) => Hidden.Add((int)handle);

        public void Remove(object handle) => Removed.Add((int)handle);

        public void Release(object handle) => Released.Add((int)handle);
    }
}

[SupportedOSPlatform("windows")]
[Collection("WindowsAppIdentity")]
public sealed class WindowsAppIdentityTests
{
    [Fact]
    public void Resolve_KeepsAnExplicitId()
    {
        var set = new List<string>();
        Assert.Equal("Mine.App", WindowsAppIdentity.Resolve(() => "Mine.App", set.Add, "Product"));
        Assert.Empty(set);
    }

    [Theory]
    [InlineData("Product", "electron.app.Product")]
    [InlineData("", "electron.app.")]
    public void Resolve_SetsElectronsDefault(string product, string expected)
    {
        var set = new List<string>();
        Assert.Equal(expected, WindowsAppIdentity.Resolve(() => null, set.Add, product));
        Assert.Equal([expected], set);
    }

    [Theory]
    [InlineData("{0f8fad5b-d9cb-469f-a165-70867728950e}", "{0F8FAD5B-D9CB-469F-A165-70867728950E}")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e", "{0F8FAD5B-D9CB-469F-A165-70867728950E}")]
    [InlineData("0f8fad5bd9cb469fa16570867728950e", null)]
    [InlineData("{0f8fad5b-d9cb-469f-a165-70867728950e", null)]
    [InlineData("", null)]
    public void NormalizeClsid_AcceptsCLSIDFromStringsForm(string input, string? expected)
        => Assert.Equal(expected, WindowsAppIdentity.NormalizeClsid(input));

    [WindowsFact]
    public void SetToastActivatorClsid_NormalizesOrRefuses()
    {
        Assert.True(WindowsAppIdentity.SetToastActivatorClsid("0f8fad5b-d9cb-469f-a165-70867728950e"));
        Assert.Equal("{0F8FAD5B-D9CB-469F-A165-70867728950E}", WindowsAppIdentity.ToastActivatorClsid);
        Assert.False(WindowsAppIdentity.SetToastActivatorClsid("nope"));
        Assert.Equal("{0F8FAD5B-D9CB-469F-A165-70867728950E}", WindowsAppIdentity.ToastActivatorClsid);
    }
}

// Shares the process-wide activator CLSID with WindowsAppIdentityTests, so the two must not run in parallel.
[SupportedOSPlatform("windows")]
[Collection("WindowsAppIdentity")]
public sealed class ToastActivatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oriel-activator-" + Guid.NewGuid().ToString("N"));

    public ToastActivatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Exe(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    [WindowsFact]
    public void EnsureClsidRegistry_RegistersTheLocalServer()
    {
        var clsid = Guid.NewGuid().ToString("B").ToUpperInvariant();
        var path = @"Software\Classes\CLSID\" + clsid;
        try
        {
            WindowsToastActivator.EnsureClsidRegistry(Registry.CurrentUser, clsid, @"C:\x\app.exe");
            using var key = Registry.CurrentUser.OpenSubKey(path)!;
            Assert.Equal("Electron Notification Activator", key.GetValue(null));
            Assert.Equal(1, key.GetValue("CustomActivator"));
            Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("CustomActivator"));
            using var server = key.OpenSubKey("LocalServer32")!;
            Assert.Equal(@"C:\x\app.exe", server.GetValue(null));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    [WindowsFact]
    public void EnsureShortcut_WritesTheShortcutElectronDoes()
    {
        var exe = Exe(@"app\App.exe");
        WindowsAppIdentity.SetToastActivatorClsid("{11111111-2222-3333-4444-555555555555}");
        WindowsToastActivator.EnsureShortcut(Path.Combine(_dir, "Programs"), "My App", exe, "electron.app.My App");
        var lnk = WindowsToastActivator.ReadShortcut(Path.Combine(_dir, "Programs", "My App.lnk"));
        Assert.Equal(new WindowsToastActivator.ShortcutInfo(exe, Path.Combine(_dir, "app"), "electron.app.My App",
            new Guid("11111111-2222-3333-4444-555555555555")), lnk);
    }

    [WindowsFact]
    public void EnsureShortcut_TargetsAStubOneFolderUp()
    {
        var exe = Exe(@"app\app-1.0\App.exe");
        var stub = Exe(@"app\App.exe");
        WindowsToastActivator.EnsureShortcut(_dir, "Stubbed", exe, "id");
        var lnk = WindowsToastActivator.ReadShortcut(Path.Combine(_dir, "Stubbed.lnk"))!;
        Assert.Equal(stub, lnk.Target);
        Assert.Equal(Path.GetDirectoryName(exe), lnk.WorkingDirectory);
    }

    [WindowsFact]
    public void EnsureShortcut_AdoptsAnExistingShortcutsClsidAndKeepsAValidOne()
    {
        var exe = Exe("App.exe");
        WindowsAppIdentity.SetToastActivatorClsid("{AAAAAAAA-0000-0000-0000-000000000001}");
        WindowsToastActivator.EnsureShortcut(_dir, "Kept", exe, "id.one");
        var lnk = Path.Combine(_dir, "Kept.lnk");
        var written = File.GetLastWriteTimeUtc(lnk);
        File.SetLastWriteTimeUtc(lnk, written.AddHours(-1));

        WindowsAppIdentity.SetToastActivatorClsid("{BBBBBBBB-0000-0000-0000-000000000002}");
        WindowsToastActivator.EnsureShortcut(_dir, "Kept", exe, "ID.ONE");
        Assert.Equal("{AAAAAAAA-0000-0000-0000-000000000001}", WindowsAppIdentity.ToastActivatorClsid);
        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(lnk));
    }

    [WindowsFact]
    public void EnsureShortcut_RewritesOneWithAnotherId()
    {
        var exe = Exe("App.exe");
        WindowsToastActivator.EnsureShortcut(_dir, "Changed", exe, "id.one");
        WindowsToastActivator.EnsureShortcut(_dir, "Changed", exe, "id.two");
        Assert.Equal("id.two", WindowsToastActivator.ReadShortcut(Path.Combine(_dir, "Changed.lnk"))!.AppUserModelId);
    }
}
