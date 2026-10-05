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
}

public sealed class ToastXmlTests
{
    [Fact]
    public void TitleAndBody_InAGenericBindingLaunchingClick()
        => Assert.Equal("<toast launch=\"click\"><visual><binding template=\"ToastGeneric\"><text>Hi</text><text>There</text></binding></visual></toast>",
            Notification.BuildToastXml(new Notification { Title = "Hi", Body = "There" }));

    [Fact]
    public void EmptyBody_IsLeftOut()
        => Assert.DoesNotContain("<text></text>", Notification.BuildToastXml(new Notification { Title = "Hi" }));

    [Fact]
    public void Text_IsEscaped()
        => Assert.Contains("<text>a &lt;b&gt; &amp; &quot;c&quot;</text>", Notification.BuildToastXml(new Notification { Title = "a <b> & \"c\"" }));

    [Fact]
    public void Silent_AddsSilentAudio()
    {
        Assert.Contains("<audio silent=\"true\"/>", Notification.BuildToastXml(new Notification { Silent = true }));
        Assert.DoesNotContain("<audio", Notification.BuildToastXml(new Notification()));
    }

    [Fact]
    public void TimeoutNever_IsAReminder()
    {
        Assert.StartsWith("<toast launch=\"click\" scenario=\"reminder\">", Notification.BuildToastXml(new Notification { TimeoutType = "never" }));
        Assert.DoesNotContain("scenario", Notification.BuildToastXml(new Notification()));
    }

    [Fact]
    public void Actions_AreIndexedButtons()
        => Assert.Contains("<actions><action content=\"Yes\" arguments=\"action=0\" activationType=\"foreground\"/>"
            + "<action content=\"No &amp; never\" arguments=\"action=1\" activationType=\"foreground\"/></actions>",
            Notification.BuildToastXml(new Notification { Actions = [new("Yes"), new("No & never")] }));

    [Fact]
    public void NoActions_NoActionsElement()
        => Assert.DoesNotContain("<actions", Notification.BuildToastXml(new Notification()));

    [Fact]
    public void Icon_IsAnAbsoluteFileUriLogo()
    {
        var path = Path.Combine(Path.GetTempPath(), "a b.png");
        Assert.Contains($"<image placement=\"appLogoOverride\" src=\"{new Uri(path).AbsoluteUri}\"/>",
            Notification.BuildToastXml(new Notification { Icon = path }));
        Assert.DoesNotContain("<image", Notification.BuildToastXml(new Notification { Icon = "" }));
    }
}

public sealed class NotificationTests : IDisposable
{
    private readonly INotificationBackend _saved = Notification.Backend;
    private readonly FakeBackend _backend = new();
    private readonly List<string> _events = [];

    public NotificationTests() => Notification.Backend = _backend;

    public void Dispose() => Notification.Backend = _saved;

    private Notification Watched(Notification? n = null)
    {
        n ??= new Notification { Title = "T" };
        n.Shown += (_, _) => _events.Add("shown");
        n.Click += (_, _) => _events.Add("click");
        n.Action += (_, i) => _events.Add($"action {i}");
        n.Closed += (_, r) => _events.Add($"closed {r}");
        n.Failed += (_, e) => _events.Add($"failed {e}");
        return n;
    }

    [Fact]
    public void Show_SendsTheGeneratedXmlAndRaisesShown()
    {
        var n = Watched();
        n.Show();
        Assert.Equal(Notification.BuildToastXml(n), _backend.Xml);
        Assert.StartsWith("oriel.app.", _backend.AppId);
        Assert.True(n.IsShown);
        Assert.Equal(["shown"], _events);
    }

    [Fact]
    public void Show_PrefersToastXml()
    {
        Watched(new Notification { Title = "T", ToastXml = "<toast/>" }).Show();
        Assert.Equal("<toast/>", _backend.Xml);
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
    public void ShowAgain_ReleasesTheFirst()
    {
        var n = Watched();
        n.Show();
        var first = _backend.Sink;
        n.Show();
        Assert.Equal([1], _backend.Released);
        first!.Dismissed(NotificationCloseReason.UserCanceled);
        Assert.Equal(["shown", "shown", "closed UserCanceled"], _events);
    }

    [Fact]
    public void Close_HidesAndTheDismissalRaisesClosedOnce()
    {
        var n = Watched();
        n.Show();
        n.Close();
        Assert.Equal([1], _backend.Hidden);
        _backend.Sink!.Dismissed(NotificationCloseReason.ApplicationHidden);
        _backend.Sink!.Dismissed(NotificationCloseReason.ApplicationHidden);
        Assert.False(n.IsShown);
        Assert.Equal([1], _backend.Released);
        Assert.Equal(["shown", "closed ApplicationHidden"], _events);
    }

    [Fact]
    public void Close_WhenNotShown_DoesNothing()
    {
        Watched().Close();
        Assert.Empty(_backend.Hidden);
    }

    [Theory]
    [InlineData("click", "click")]
    [InlineData("action=2", "action 2")]
    [InlineData("action=", "click")]
    [InlineData("action=-1", "click")]
    [InlineData("button=3", "click")]
    public void Activation_IsAClickOrAnAction(string arguments, string expected)
    {
        var n = Watched();
        n.Show();
        _backend.Sink!.Activated(arguments);
        Assert.Equal(["shown", expected], _events);
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
        public string? AppId, Xml;
        public INotificationSink? Sink;
        public bool Refuse;
        public List<int> Hidden = [], Released = [];

        public bool IsSupported => true;

        public object Show(string appId, string xml, INotificationSink sink)
        {
            if (Refuse) throw new InvalidOperationException("no");
            (AppId, Xml, Sink) = (appId, xml, sink);
            return ++_next;
        }

        public void Hide(object handle) => Hidden.Add((int)handle);

        public void Release(object handle) => Released.Add((int)handle);
    }
}
